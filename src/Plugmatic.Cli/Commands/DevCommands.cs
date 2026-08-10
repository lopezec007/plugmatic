using System.CommandLine;
using Plugmatic.Cli.Services;
using Plugmatic.Core.Model;
using Plugmatic.Core.Runs;
using Plugmatic.Radios;
using Plugmatic.Radios.Dm32uv.Format;

namespace Plugmatic.Cli.Commands;

/// <summary>Protocol bring-up & RE commands; every invocation creates a `dev` run. [spec §7.9]</summary>
public static class DevCommands
{
    public static Command Build()
    {
        var dev = new Command("dev", "Protocol bring-up and reverse-engineering commands");
        dev.Subcommands.Add(BuildIdentify());
        dev.Subcommands.Add(BuildDump());
        dev.Subcommands.Add(BuildPeek());
        dev.Subcommands.Add(BuildWriteTest());
        dev.Subcommands.Add(BuildDecode());
        dev.Subcommands.Add(BuildDiffBin());
        dev.Subcommands.Add(BuildReplay());
        dev.Subcommands.Add(BuildWriteback());
        return dev;
    }

    private static Option<string?> PortOption() => new("--port") { Description = "Serial port" };

    // ---------------------------------------------------------------- identify

    private static Command BuildIdentify()
    {
        var port = PortOption();
        var radioOpt = Common.RadioOption();
        var cmd = new Command("identify", "Handshake + identify; print and archive the raw response");
        cmd.Options.Add(port); cmd.Options.Add(radioOpt);
        cmd.SetAction(async (pr, ct) =>
        {
            var radio = Common.Resolve(pr.GetValue(radioOpt));
            var runs = new RunManager();
            var run = runs.CreateRun(radio.Model, "dev");
            var outcome = RunOutcome.Failed;
            try
            {
                await using var session = await RadioSession.OpenAsync(radio, pr.GetValue(port), run, ct);
                var id = await session.IdentifyAsync(ct);
                var text =
                    $"model: {id.Model}\nfirmware: {id.FirmwareVersion}\nbuildDate: {id.BuildDate}\n" +
                    $"codeplugMemory: 0x{id.CodeplugMemoryStart:X6}-0x{id.CodeplugMemoryEnd:X6}\n" +
                    $"rawPSEARCHResponse: {id.RawIdentifyHex}\n";
                Console.Write(text);
                run.WriteArtifact("identify.txt", text);
                run.Extra["radio"] = new System.Text.Json.Nodes.JsonObject
                { ["model"] = radio.Model, ["reportedId"] = id.Model, ["firmware"] = id.FirmwareVersion };
                outcome = RunOutcome.Success;
                return 0;
            }
            catch (CliError e) { Console.Error.WriteLine(e.Message); return e.ExitCode; }
            catch (Exception e) { Console.Error.WriteLine($"identify failed: {e.Message}"); return 3; }
            finally { runs.Finalize(run, outcome); }
        });
        return cmd;
    }

    // ---------------------------------------------------------------- dump

    private static Command BuildDump()
    {
        var port = PortOption();
        var outOpt = new Option<string?>("--out") { Description = "Also copy dump.bin to this path" };
        var tagOpt = new Option<string?>("--tag") { Description = "Tag for the run manifest (e.g. factory-golden)" };
        var radioOpt = Common.RadioOption();
        var cmd = new Command("dump", "Full codeplug image read via the native protocol (ladder step 2)");
        cmd.Options.Add(port); cmd.Options.Add(outOpt); cmd.Options.Add(tagOpt); cmd.Options.Add(radioOpt);
        cmd.SetAction(async (pr, ct) =>
        {
            var radio = Common.Resolve(pr.GetValue(radioOpt));
            var runs = new RunManager();
            var run = runs.CreateRun(radio.Model, "dev");
            if (pr.GetValue(tagOpt) is { } tag) run.Tags.Add(tag);
            Console.WriteLine($"Run: {run.Directory}");
            var outcome = RunOutcome.Failed;
            try
            {
                await using var session = await RadioSession.OpenAsync(radio, pr.GetValue(port), run, ct);
                var id = await session.IdentifyAsync(ct);
                run.Extra["radio"] = new System.Text.Json.Nodes.JsonObject
                { ["model"] = radio.Model, ["reportedId"] = id.Model, ["firmware"] = id.FirmwareVersion };
                Console.WriteLine($"Radio: {id.Model} fw {id.FirmwareVersion}; codeplug 0x{id.CodeplugMemoryStart:X6}-0x{id.CodeplugMemoryEnd:X6}");
                var image = await session.Protocol.ReadImageAsync(session.Link, WriteFlow.ConsoleProgress(), ct);
                var path = run.WriteArtifact("dump.bin", image);
                if (pr.GetValue(outOpt) is { } outPath) File.Copy(path, outPath, overwrite: true);
                Console.WriteLine($"Dumped 0x{image.Length:X} bytes -> {path}");
                outcome = RunOutcome.Success;
                return 0;
            }
            catch (CliError e) { Console.Error.WriteLine(e.Message); return e.ExitCode; }
            catch (Exception e) { Console.Error.WriteLine($"dump failed: {e.Message}"); return 3; }
            finally { runs.Finalize(run, outcome); }
        });
        return cmd;
    }

    // ---------------------------------------------------------------- peek

    /// <summary>Read-class probe of an arbitrary address range (spec §3.3 allows this freely).</summary>
    private static Command BuildPeek()
    {
        var addrArg = new Argument<string>("address") { Description = "Radio address, e.g. 0x024C1500" };
        var lenOpt = new Option<int>("--length") { DefaultValueFactory = _ => 64, Description = "Bytes to read" };
        var port = PortOption();
        var radioOpt = Common.RadioOption();
        var cmd = new Command("peek", "Read a raw address range from the radio and hex-dump it");
        cmd.Arguments.Add(addrArg);
        cmd.Options.Add(lenOpt); cmd.Options.Add(port); cmd.Options.Add(radioOpt);
        cmd.SetAction(async (pr, ct) =>
        {
            var radio = Common.Resolve(pr.GetValue(radioOpt));
            var text = pr.GetValue(addrArg)!;
            uint address = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt32(text[2..], 16) : Convert.ToUInt32(text, 16);
            int length = pr.GetValue(lenOpt);

            var runs = new RunManager();
            var run = runs.CreateRun(radio.Model, "dev");
            var outcome = RunOutcome.Failed;
            try
            {
                await using var session = await RadioSession.OpenAsync(radio, pr.GetValue(port), run, ct);
                await session.IdentifyAsync(ct);
                if (session.Protocol is not Plugmatic.Radios.D878uv.Protocol.D878uvProtocol anytone)
                    throw new CliError($"peek is only implemented for AnyTone radios so far.", 1);

                var data = await anytone.ReadRegionAsync(session.Link, address, length, null, "peek", ct);
                for (int i = 0; i < data.Length; i += 16)
                {
                    var row = data.AsSpan(i, Math.Min(16, data.Length - i));
                    var ascii = new string([.. row.ToArray().Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.')]);
                    Console.WriteLine($"{address + (uint)i:X8}  {Convert.ToHexString(row),-32}  {ascii}");
                }
                run.WriteArtifact("peek.bin", data);
                outcome = RunOutcome.Success;
                return 0;
            }
            catch (CliError e) { Console.Error.WriteLine(e.Message); return e.ExitCode; }
            catch (Exception e) { Console.Error.WriteLine($"peek failed: {e.Message}"); return 3; }
            finally { runs.Finalize(run, outcome); }
        });
        return cmd;
    }

    // ---------------------------------------------------------------- writetest

    /// <summary>
    /// Smallest possible write-class validation: read one 16-byte chunk, write the very
    /// same bytes back, read again and compare. Proves framing, checksum and ACK with no
    /// semantic change even if every byte lands. Targets an unallocated record by default,
    /// so the radio does not use the content either way.
    /// </summary>
    private static Command BuildWriteTest()
    {
        var port = PortOption();
        var addrOpt = new Option<string?>("--address")
        { Description = "Target address; defaults to the last (unallocated) channel slot" };
        var probeByteOpt = new Option<int>("--probe-offset")
        { DefaultValueFactory = _ => -1, Description = "Mutate only this byte of the chunk (default: whole chunk)" };
        var keepOpt = new Option<bool>("--keep")
        { Description = "Leave the probe pattern in place (target is unallocated filler) to test commit-on-close" };
        var proveOpt = new Option<bool>("--prove")
        { Description = "Also write a probe pattern, confirm it landed, then restore the original" };
        var cmd = new Command("writetest", "Ladder step 4a: write one chunk back byte-identical and verify");
        cmd.Options.Add(port); cmd.Options.Add(addrOpt); cmd.Options.Add(proveOpt); cmd.Options.Add(keepOpt); cmd.Options.Add(probeByteOpt);
        cmd.SetAction(async (pr, ct) =>
        {
            var radio = Common.Resolve("d878uv");
            uint address = pr.GetValue(addrOpt) is { } t
                ? Convert.ToUInt32(t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t, 16)
                : Plugmatic.Radios.D878uv.Format.Layout.ChannelSlot(
                      Plugmatic.Radios.D878uv.Format.Layout.MaxChannels - 1).Address;

            var runs = new RunManager();
            var run = runs.CreateRun(radio.Model, "dev");
            var outcome = RunOutcome.Failed;
            try
            {
                await using var session = await RadioSession.OpenAsync(radio, pr.GetValue(port), run, ct);
                await session.IdentifyAsync(ct);
                var proto = (Plugmatic.Radios.D878uv.Protocol.D878uvProtocol)session.Protocol;

                var before = await proto.ReadRegionAsync(session.Link, address, 16, null, "read", ct);
                run.WriteArtifact("before.bin", before);
                Console.WriteLine($"Address 0x{address:X8} currently reads: {Convert.ToHexString(before)}");
                Console.WriteLine("This writes those exact bytes back — no value changes, whatever happens.");
                Console.Write("Type WRITE to send one 16-byte frame: ");
                if (Console.ReadLine()?.Trim() != "WRITE")
                {
                    outcome = RunOutcome.Aborted;
                    Console.WriteLine("Aborted; nothing sent.");
                    return 1;
                }

                await proto.WriteChunkAsync(session.Link, address, before, ct);
                var after = await proto.ReadRegionAsync(session.Link, address, 16, null, "read", ct);
                run.WriteArtifact("after.bin", after);

                bool same = before.AsSpan().SequenceEqual(after);
                Console.WriteLine($"After write:  {Convert.ToHexString(after)}");
                if (!same)
                {
                    Console.WriteLine("FAIL — memory changed; the write framing is wrong. Do not proceed.");
                    return 3;
                }
                Console.WriteLine("Identical write accepted.");

                // An identical write proves nothing if the radio simply ignored us: mutate,
                // confirm the change landed, then restore. Same unallocated slot throughout.
                bool proved = false, restored = false;
                if (pr.GetValue(proveOpt))
                {
                    var probe = (byte[])before.Clone();
                    int only = pr.GetValue(probeByteOpt);
                    if (only >= 0 && only < 16) probe[only] ^= 0x5A;   // single-byte mutation
                    else Array.Fill(probe, (byte)0x5A);
                    await proto.WriteChunkAsync(session.Link, address, probe, ct);
                    var mutated = await proto.ReadRegionAsync(session.Link, address, 16, null, "read", ct);
                    proved = mutated.AsSpan().SequenceEqual(probe);
                    Console.WriteLine($"Probe write:  {Convert.ToHexString(mutated)} — " +
                                      (proved ? "writes take effect" : "radio IGNORED the write"));

                    if (pr.GetValue(keepOpt))
                    {
                        restored = true;   // deliberately left in place for the next session's read
                        Console.WriteLine("Probe left in place (--keep) to test whether writes commit on close.");
                    }
                    else
                    {
                        await proto.WriteChunkAsync(session.Link, address, before, ct);
                        var final = await proto.ReadRegionAsync(session.Link, address, 16, null, "read", ct);
                        restored = final.AsSpan().SequenceEqual(before);
                        Console.WriteLine($"Restored:     {Convert.ToHexString(final)} — " +
                                          (restored ? "original bytes back" : "RESTORE FAILED"));
                    }
                }

                bool ok = same && (!pr.GetValue(proveOpt) || (proved && restored));
                Console.WriteLine(ok ? "PASS — write path validated." : "FAIL — see above.");
                run.Extra["writeTest"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["address"] = $"0x{address:X8}", ["identicalWriteOk"] = same,
                    ["mutationLanded"] = proved, ["restored"] = restored,
                };
                outcome = ok ? RunOutcome.Success : RunOutcome.Failed;
                return ok ? 0 : 3;
            }
            catch (CliError e) { Console.Error.WriteLine(e.Message); return e.ExitCode; }
            catch (Exception e) { Console.Error.WriteLine($"writetest failed: {e.Message}"); return 3; }
            finally { runs.Finalize(run, outcome); }
        });
        return cmd;
    }

    // ---------------------------------------------------------------- decode

    private static Command BuildDecode()
    {
        var file = new Argument<string>("file") { Description = "Image (.bin) or CPS .data file" };
        var radioOpt = Common.RadioOption();
        var cmd = new Command("decode", "Run the native codec against an image; print IR summary + warnings");
        cmd.Arguments.Add(file); cmd.Options.Add(radioOpt);
        cmd.SetAction((pr, _) =>
        {
            var radio = Common.Resolve(pr.GetValue(radioOpt));
            var path = pr.GetValue(file)!;
            if (!File.Exists(path)) throw new CliError($"Not found: {path}", 1);
            var bytes = File.ReadAllBytes(path);
            var ir = radio.Codec.Decode(bytes);
            Console.WriteLine($"channels:    {ir.Channels.Count}");
            foreach (var (ch, i) in ir.Channels.Take(20).Select((c, i) => (c, i)))
                Console.WriteLine($"  {i + 1,4}  {ch.Name,-16} rx {ch.RxFrequency}  tx {ch.TxFrequency}  " +
                                  $"{(ch is DigitalChannel d ? $"DMR cc{d.ColorCode} {d.TimeSlot}" : "FM")}" +
                                  $"{(ch.TxPermit == TxPermit.Inhibited ? " RX-only" : "")}");
            if (ir.Channels.Count > 20) Console.WriteLine($"  ... {ir.Channels.Count - 20} more");
            Console.WriteLine($"zones:       {ir.Zones.Count}  ({string.Join(", ", ir.Zones.Select(z => z.Name).Take(10))})");
            Console.WriteLine($"contacts:    {ir.Contacts.Count}");
            Console.WriteLine($"group lists: {ir.RxGroupLists.Count}");
            Console.WriteLine($"scan lists:  {ir.ScanLists.Count}");
            Console.WriteLine($"radio id:    {ir.Settings.RadioId} '{ir.Settings.Callsign}'");
            Console.WriteLine($"raw blocks:  {ir.RawBlocks.Count} present");
            return Task.FromResult(0);
        });
        return cmd;
    }

    // ---------------------------------------------------------------- diffbin

    private static Command BuildDiffBin()
    {
        var a = new Argument<string>("a"); var b = new Argument<string>("b");
        var context = new Option<int>("--context") { DefaultValueFactory = _ => 8, Description = "Context bytes around differences" };
        var cmd = new Command("diffbin", "Hex diff of two binary images");
        cmd.Arguments.Add(a); cmd.Arguments.Add(b); cmd.Options.Add(context);
        cmd.SetAction((pr, _) =>
        {
            var ba = File.ReadAllBytes(pr.GetValue(a)!);
            var bb = File.ReadAllBytes(pr.GetValue(b)!);
            int ctx = pr.GetValue(context);
            if (ba.Length != bb.Length)
                Console.WriteLine($"Sizes differ: {ba.Length} vs {bb.Length} bytes; comparing common prefix.");
            int n = Math.Min(ba.Length, bb.Length), diffRuns = 0;
            for (int i = 0; i < n;)
            {
                if (ba[i] == bb[i]) { i++; continue; }
                int start = i;
                while (i < n && (ba[i] != bb[i] || i - start < 1)) i++;
                int from = Math.Max(0, start - ctx), to = Math.Min(n, i + ctx);
                Console.WriteLine($"@ 0x{start:X6} ({i - start} bytes differ)");
                Console.WriteLine($"  A: {Convert.ToHexString(ba.AsSpan(from, to - from))}");
                Console.WriteLine($"  B: {Convert.ToHexString(bb.AsSpan(from, to - from))}");
                if (++diffRuns >= 200) { Console.WriteLine("... (truncated at 200 runs)"); break; }
            }
            Console.WriteLine(diffRuns == 0 ? "Images identical." : $"{diffRuns} differing run(s).");
            return Task.FromResult(diffRuns == 0 ? 0 : 1);
        });
        return cmd;
    }

    // ---------------------------------------------------------------- replay

    private static Command BuildReplay()
    {
        var file = new Argument<string>("frames") { Description = "Hex frames file: one frame per line, '#' comments" };
        var port = PortOption();
        var radioOpt = Common.RadioOption();
        var cmd = new Command("replay", "Send captured frames byte-exact (write-class frames require confirmation)");
        cmd.Arguments.Add(file); cmd.Options.Add(port); cmd.Options.Add(radioOpt);
        cmd.SetAction(async (pr, ct) =>
        {
            var lines = File.ReadAllLines(pr.GetValue(file)!)
                .Select(l => l.Split('#')[0].Replace(" ", "").Replace("-", "").Trim())
                .Where(l => l.Length > 0)
                .Select(Convert.FromHexString)
                .ToList();
            if (lines.Count == 0) throw new CliError("No frames in file.", 1);

            // Write-class = leading 'W' (0x57) or PROGRAM-style prefixed frames that alter state.
            var writeFrames = lines.Where(f => f.Length > 0 && f[0] == 0x57).ToList();
            if (writeFrames.Count > 0)
            {
                Console.WriteLine($"This replay contains {writeFrames.Count} WRITE-class frame(s):");
                foreach (var f in writeFrames.Take(5))
                    Console.WriteLine($"  {Convert.ToHexString(f.AsSpan(0, Math.Min(24, f.Length)))}... ({f.Length} bytes)");
                Console.Write("Type REPLAY WRITES to proceed: ");
                if (Console.ReadLine()?.Trim() != "REPLAY WRITES")
                    throw new CliError("Aborted.", 1);
            }

            var radio = Common.Resolve(pr.GetValue(radioOpt));
            var runs = new RunManager();
            var run = runs.CreateRun(radio.Model, "dev");
            var outcome = RunOutcome.Failed;
            try
            {
                await using var session = await RadioSession.OpenAsync(radio, pr.GetValue(port), run, ct);
                foreach (var frame in lines)
                {
                    await session.Link.WriteAsync(frame, ct);
                    var buf = new byte[8192];
                    int got = await session.Link.ReadAsync(buf, TimeSpan.FromMilliseconds(1500), ct);
                    Console.WriteLine($">> {Convert.ToHexString(frame.AsSpan(0, Math.Min(32, frame.Length)))}{(frame.Length > 32 ? "..." : "")}");
                    Console.WriteLine($"<< {(got == 0 ? "(timeout)" : Convert.ToHexString(buf.AsSpan(0, Math.Min(got, 64))))}");
                }
                outcome = RunOutcome.Success;
                return 0;
            }
            finally { runs.Finalize(run, outcome); }
        });
        return cmd;
    }

    // ---------------------------------------------------------------- writeback

    private static Command BuildWriteback()
    {
        var runDir = new Argument<string>("read-run-dir") { Description = "A read/dev run directory containing read.bin or dump.bin" };
        var port = PortOption();
        var yes = new Option<bool>("--yes", "-y");
        var radioOpt = Common.RadioOption();
        var cmd = new Command("writeback", "Ladder step 4: no-op write of that run's own image (full §7.5 sequence)");
        cmd.Arguments.Add(runDir); cmd.Options.Add(port); cmd.Options.Add(yes); cmd.Options.Add(radioOpt);
        cmd.SetAction(async (pr, ct) =>
        {
            var radio = Common.Resolve(pr.GetValue(radioOpt));
            var dir = pr.GetValue(runDir)!;
            var bin = new[] { "read.bin", "dump.bin", "pre-write.bin" }
                .Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists)
                ?? throw new CliError($"No read.bin/dump.bin/pre-write.bin in {dir}.", 1);
            Console.WriteLine($"No-op writeback of {bin}");
            var source = new WriteFlow.Source(null, File.ReadAllBytes(bin), $"writeback {Path.GetFileName(bin)}");
            return await WriteFlow.RunAsync(radio, new RunManager(), pr.GetValue(port), source, pr.GetValue(yes), ct);
        });
        return cmd;
    }
}
