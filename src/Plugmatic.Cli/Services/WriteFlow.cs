using Plugmatic.Core.Model;
using Plugmatic.Core.Runs;
using Plugmatic.Radios;

namespace Plugmatic.Cli.Services;

/// <summary>
/// The §7.5 write sequence — the only code path that reaches WriteImageAsync.
/// D7/I1: the hardware write step takes the same-run pre-write artifact as a required
/// argument and validates it; there is no way to invoke it without a fresh read.
/// </summary>
public static class WriteFlow
{
    public sealed record Source(Codeplug? Ir, byte[]? RawImage, string Label);

    /// <summary>How long to wait for the radio's port to re-enumerate between phases.</summary>
    private static readonly TimeSpan ReopenTimeout = TimeSpan.FromSeconds(30);

    public static async Task<int> RunAsync(
        IRadioDefinition radio, IRunManager runs, string? portOption, Source source, bool assumeYes,
        CancellationToken ct)
    {
        var codec = radio.Codec;
        var run = runs.CreateRun(radio.Model, "write");
        Console.WriteLine($"Run: {run.Directory}");
        var outcome = RunOutcome.Failed;

        // Some radios discard every staged write the moment they see a read, so read,
        // write and verify each need their own session. Everything else keeps the single
        // session this flow has always used. [d878uv-protocol.md §5.1]
        bool perPhase = radio.SeparateReadWriteSessions;
        RadioSession? shared = null;
        bool first = true;

        async Task<RadioSession> BeginAsync()
        {
            if (!perPhase)
            {
                if (shared is null)
                {
                    shared = await RadioSession.OpenAsync(radio, portOption, run, ct);
                    await IdentifyAndRecordAsync(shared);
                }
                return shared;
            }
            // Always wait, including for the first session: the radio drops its USB device at
            // the end of every session, and the one that just ended may have been a previous
            // command's. [d878uv-protocol.md §2]
            var session = await RadioSession.ReopenAsync(radio, portOption, run, ReopenTimeout, ct);
            await IdentifyAndRecordAsync(session);
            return session;
        }

        // Ends the phase. For the per-phase radios this is also the commit point, so a
        // failure here is reported rather than swallowed by teardown.
        async Task EndAsync(RadioSession session)
        {
            if (!perPhase) return;
            try { await session.Protocol.EndSessionAsync(session.Link, ct); }
            finally { await session.DisposeAsync(); }
        }

        async Task IdentifyAndRecordAsync(RadioSession session)
        {
            var id = await session.IdentifyAsync(ct);           // I2 model gate, every session
            if (!first) return;
            first = false;
            run.Extra["radio"] = new System.Text.Json.Nodes.JsonObject
            {
                ["model"] = radio.Model, ["reportedId"] = id.Model, ["firmware"] = id.FirmwareVersion,
            };
            run.Extra["port"] = session.PortName;
            Console.WriteLine($"Radio: {id.Model}, firmware {id.FirmwareVersion}");
        }

        try
        {
            // 1. Unconditional fresh read -> pre-write artifacts (D7). No skip flag exists.
            Console.WriteLine("Reading current codeplug (pre-write backup)...");
            var readSession = await BeginAsync();
            var preWrite = await readSession.Protocol.ReadImageAsync(readSession.Link, ConsoleProgress(), ct);
            await EndAsync(readSession);

            var preWritePath = run.WriteArtifact("pre-write.bin", preWrite);
            var preIr = codec.Decode(preWrite);
            run.WriteArtifact("pre-write.yaml", IrYaml.Serialize(preIr));

            // 2. Produce generated.bin + round-trip gate (I3).
            byte[] generated;
            if (source.Ir is { } ir)
            {
                generated = codec.Encode(ir, preWrite);
                var decoded = codec.Decode(generated);
                var gateErrors = StructuralGate(ir, decoded);
                if (gateErrors.Count > 0)
                    throw new CliError("Round-trip gate failed (I3); refusing to write:\n  " +
                                       string.Join("\n  ", gateErrors), 3);
            }
            else
            {
                generated = source.RawImage!;
                Codeplug decoded;
                try { decoded = codec.Decode(generated); }
                catch (Exception e) { throw new CliError($"--image gate failed (I3): image does not decode: {e.Message}", 1); }
                var reEncoded = codec.Encode(decoded, generated);
                var cmp = codec.Compare(reEncoded, generated);
                if (!cmp.Equal)
                    throw new CliError("--image gate failed (I3): Encode(Decode(img)) differs from img:\n  " +
                                       string.Join("\n  ", cmp.Differences.Take(10)), 1);
            }
            run.WriteArtifact("generated.bin", generated);
            if (source.Ir is not null) run.WriteArtifact("generated.yaml", IrYaml.Serialize(source.Ir));

            // 3. Summary diff + confirmation.
            var genIr = codec.Decode(generated);
            Console.WriteLine();
            Console.WriteLine($"About to write ({source.Label}):");
            Console.WriteLine(DiffSummary(preIr, genIr));
            if (!assumeYes)
            {
                Console.Write("Proceed with write? [y/N] ");
                if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                {
                    outcome = RunOutcome.Aborted;
                    Console.WriteLine("Aborted; nothing written.");
                    return 1;
                }
            }

            // 4. Write (I1: pre-write artifact validated here, same run).
            var writeSession = await BeginAsync();
            await WriteHardwareAsync(writeSession, generated, preWrite, preWritePath, ct);
            await EndAsync(writeSession);           // the commit, for radios that stage writes

            // 5. Read back and compare (masked) — necessarily a new session on those radios,
            //    because reading in the writing session would have discarded the write.
            Console.WriteLine("Reading back for verification...");
            var verifySession = await BeginAsync();
            var postWrite = await verifySession.Protocol.ReadImageAsync(verifySession.Link, ConsoleProgress(), ct);
            await EndAsync(verifySession);

            run.WriteArtifact("post-write.bin", postWrite);
            var post = codec.Compare(generated, postWrite);
            run.Extra["verification"] = new System.Text.Json.Nodes.JsonObject
            {
                ["postWriteMatches"] = post.Equal,
                ["differences"] = post.Equal ? null : string.Join("; ", post.Differences.Take(20)),
            };
            Console.WriteLine(post.Equal
                ? "Post-write verification: the radio's image matches what was written."
                : "WARNING: post-write differences:\n  " + string.Join("\n  ", post.Differences.Take(20)));
            if (!post.Equal) return 3;

            outcome = RunOutcome.Success;
            return 0;
        }
        catch (CliError e)
        {
            Console.Error.WriteLine(e.Message);
            if (e.ExitCode == 3) PrintRecovery(run.Directory, radio.Model);
            return e.ExitCode;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Write failed: {e.Message}");
            PrintRecovery(run.Directory, radio.Model);
            return 3;
        }
        finally
        {
            if (shared is not null) await shared.DisposeAsync();
            runs.Finalize(run, outcome);
        }
    }

    /// <summary>I1 enforcement point: hardware write requires the same-run pre-write.bin.</summary>
    private static async Task WriteHardwareAsync(
        RadioSession session, byte[] image, byte[] baseline, string preWriteBinPath, CancellationToken ct)
    {
        var fi = new FileInfo(preWriteBinPath);
        if (!fi.Exists || fi.Length == 0)
            throw new CliError("I1 violation: pre-write.bin missing or empty in this run; refusing to write.", 3);
        Console.WriteLine("Writing codeplug...");
        await session.Protocol.WriteImageAsync(session.Link, image, baseline, ConsoleProgress(), ct);
    }

    private static void PrintRecovery(string runDir, string model)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("Recovery: the radio's previous codeplug was archived before writing.");
        Console.Error.WriteLine($"  plugmatic write --radio {model} --image {Path.Combine(runDir, "pre-write.bin")}");
        Console.Error.WriteLine("If the radio is unresponsive: power-cycle it, then run the command above.");
    }

    /// <summary>Structural equality on the fields the IR models (names/order/values).</summary>
    private static List<string> StructuralGate(Codeplug expect, Codeplug got)
    {
        var errors = new List<string>();
        void Check(bool ok, string what) { if (!ok) errors.Add(what); }

        Check(expect.Channels.Count == got.Channels.Count, $"channel count {expect.Channels.Count} != {got.Channels.Count}");
        for (int i = 0; i < Math.Min(expect.Channels.Count, got.Channels.Count); i++)
        {
            var a = expect.Channels[i]; var b = got.Channels[i];
            Check(a.Name == b.Name, $"ch{i + 1} name '{a.Name}' != '{b.Name}'");
            Check(a.RxFrequency.Hz == b.RxFrequency.Hz, $"ch{i + 1} rx {a.RxFrequency} != {b.RxFrequency}");
            Check(a.TxFrequency.Hz == b.TxFrequency.Hz, $"ch{i + 1} tx {a.TxFrequency} != {b.TxFrequency}");
            Check(a.TxPermit == b.TxPermit, $"ch{i + 1} txPermit {a.TxPermit} != {b.TxPermit}");
            Check(a.Power == b.Power, $"ch{i + 1} power {a.Power} != {b.Power}");
            Check(a.GetType() == b.GetType(), $"ch{i + 1} kind {a.GetType().Name} != {b.GetType().Name}");
            if (a is DigitalChannel da && b is DigitalChannel db)
            {
                Check(da.ColorCode == db.ColorCode, $"ch{i + 1} cc {da.ColorCode} != {db.ColorCode}");
                Check(da.TimeSlot == db.TimeSlot, $"ch{i + 1} ts {da.TimeSlot} != {db.TimeSlot}");
                Check(da.TxContactName == db.TxContactName, $"ch{i + 1} contact '{da.TxContactName}' != '{db.TxContactName}'");
            }
            if (a is AnalogChannel aa && b is AnalogChannel ab)
            {
                Check(aa.RxTone == ab.RxTone, $"ch{i + 1} rxTone {aa.RxTone} != {ab.RxTone}");
                Check(aa.TxTone == ab.TxTone, $"ch{i + 1} txTone {aa.TxTone} != {ab.TxTone}");
                Check(aa.WideBandwidth == ab.WideBandwidth, $"ch{i + 1} bw");
            }
        }
        Check(expect.Zones.Count == got.Zones.Count, $"zone count {expect.Zones.Count} != {got.Zones.Count}");
        for (int i = 0; i < Math.Min(expect.Zones.Count, got.Zones.Count); i++)
        {
            Check(expect.Zones[i].Name == got.Zones[i].Name, $"zone{i + 1} name");
            Check(expect.Zones[i].ChannelNames.SequenceEqual(got.Zones[i].ChannelNames), $"zone '{expect.Zones[i].Name}' members");
        }
        Check(expect.Contacts.Count == got.Contacts.Count, $"contact count {expect.Contacts.Count} != {got.Contacts.Count}");
        for (int i = 0; i < Math.Min(expect.Contacts.Count, got.Contacts.Count); i++)
        {
            Check(expect.Contacts[i].Name == got.Contacts[i].Name, $"contact{i + 1} name");
            Check(expect.Contacts[i].DmrId == got.Contacts[i].DmrId, $"contact '{expect.Contacts[i].Name}' id");
            Check(expect.Contacts[i].Type == got.Contacts[i].Type, $"contact '{expect.Contacts[i].Name}' type");
        }
        return errors;
    }

    public static string DiffSummary(Codeplug old, Codeplug @new)
    {
        static string Line(string what, int o, int n) =>
            $"  {what}: {o} -> {n}" + (o == n ? "" : $"  ({(n > o ? "+" : "")}{n - o})");
        return string.Join("\n",
            Line("channels", old.Channels.Count, @new.Channels.Count),
            Line("zones", old.Zones.Count, @new.Zones.Count),
            Line("contacts", old.Contacts.Count, @new.Contacts.Count),
            Line("group lists", old.RxGroupLists.Count, @new.RxGroupLists.Count),
            Line("scan lists", old.ScanLists.Count, @new.ScanLists.Count));
    }

    public static IProgress<TransferProgress> ConsoleProgress()
    {
        int last = -1;
        return new Progress<TransferProgress>(p =>
        {
            if (p.Percent / 10 != last)
            {
                last = p.Percent / 10;
                Console.Write($"\r  {p.Phase}: {p.Percent}% ({p.Current}/{p.Total})    ");
                if (p.Current == p.Total) Console.WriteLine();
            }
        });
    }
}
