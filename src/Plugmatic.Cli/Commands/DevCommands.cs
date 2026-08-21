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
        dev.Subcommands.Add(BuildRawDump());
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

    // ---------------------------------------------------------------- rawdump

    /// <summary>
    /// Dump whole **erase blocks**, not the region table.
    ///
    /// A write erases 256 KB and reprograms only what the session staged, so the unit that
    /// has to be checked for damage is the block — the region table sees a fraction of it
    /// and would report "no change" over a crater. Two rawdumps either side of a write, run
    /// through `dev diffbin`, are the evidence ladder step 4 actually calls for.
    /// [d878uv-protocol.md §5.4/§5.6]
    /// </summary>
    private static Command BuildRawDump()
    {
        var port = PortOption();
        var outOpt = new Option<string?>("--out") { Description = "Also copy rawdump.bin to this path" };
        var blocksOpt = new Option<string?>("--blocks")
        { Description = "Comma-separated block addresses (default: every block the codeplug occupies)" };
        var cmd = new Command("rawdump", "Read whole erase blocks verbatim, for before/after damage checks");
        cmd.Options.Add(port); cmd.Options.Add(outOpt); cmd.Options.Add(blocksOpt);
        cmd.SetAction(async (pr, ct) =>
        {
            var radio = Common.Resolve("d878uv");
            var all = Plugmatic.Radios.D878uv.Format.Layout.CodeplugBanks.OrderBy(b => b).ToList();
            List<uint> blocks;
            if (pr.GetValue(blocksOpt) is { } list && list.Trim().Length > 0)
            {
                blocks = [];
                foreach (var piece in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    uint value = Convert.ToUInt32(
                        piece.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? piece[2..] : piece, 16);
                    uint block = Plugmatic.Radios.D878uv.Format.Layout.BankOf(value);
                    if (!all.Contains(block))
                        throw new CliError($"0x{block:X8} is not an erase block the codeplug occupies.", 1);
                    if (!blocks.Contains(block)) blocks.Add(block);
                }
                blocks.Sort();
            }
            else blocks = all;

            var runs = new RunManager();
            var run = runs.CreateRun(radio.Model, "dev");
            Console.WriteLine($"Run: {run.Directory}");
            var outcome = RunOutcome.Failed;
            uint blockSize = Plugmatic.Radios.D878uv.Format.Layout.BankStride;
            try
            {
                await using var session = await RadioSession.ReopenAsync(
                    radio, pr.GetValue(port), run, TimeSpan.FromSeconds(60), ct);
                await session.IdentifyAsync(ct);
                var proto = (Plugmatic.Radios.D878uv.Protocol.D878uvProtocol)session.Protocol;

                Console.WriteLine($"Dumping {blocks.Count} block(s) x 0x{blockSize:X} = " +
                                  $"{blocks.Count * (long)blockSize / 1024} KB");
                var image = new byte[blocks.Count * (long)blockSize];
                for (int i = 0; i < blocks.Count; i++)
                {
                    var bytes = await proto.ReadRegionAsync(session.Link, blocks[i], (int)blockSize, null, "read", ct);
                    bytes.CopyTo(image.AsSpan(i * (int)blockSize));
                    Console.Write($"\r  {i + 1}/{blocks.Count} (0x{blocks[i]:X8})    ");
                }
                Console.WriteLine();

                var path = run.WriteArtifact("rawdump.bin", image);
                // The block order is what makes two dumps comparable; record it beside them.
                run.WriteArtifact("rawdump.map",
                    string.Join("\n", blocks.Select((b, i) =>
                        $"0x{i * (long)blockSize:X8} 0x{b:X8}")) + "\n");
                if (pr.GetValue(outOpt) is { } outPath) File.Copy(path, outPath, overwrite: true);
                Console.WriteLine($"Wrote 0x{image.Length:X} bytes -> {path}");
                Console.WriteLine("Compare two dumps with: plugmatic dev diffbin <a> <b>");
                outcome = RunOutcome.Success;
                return 0;
            }
            catch (CliError e) { Console.Error.WriteLine(e.Message); return e.ExitCode; }
            catch (Exception e) { Console.Error.WriteLine($"rawdump failed: {e.Message}"); return 3; }
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
    /// Ladder step 4: the mutation test, done the only way this radio can answer it safely.
    ///
    /// Two hardware facts shape this. Writes are staged and commit on `END`, and any read
    /// after a write discards them — so the probe and its read-back must live in different
    /// sessions. And a 16-byte write erases the whole 256 KB block around it, reprogramming
    /// only what was staged — so probing an address near live data destroys that data. This
    /// command therefore works only inside an erase block it has confirmed is entirely
    /// erased, where there is nothing to lose. [d878uv-protocol.md §5.1/§5.4]
    /// </summary>
    private static Command BuildWriteTest()
    {
        var port = PortOption();
        var addrOpt = new Option<string?>("--address")
        { Description = "Probe address; must sit in an all-FF erase block (default 0x00880000)" };
        var yesOpt = new Option<bool>("--yes", "-y") { Description = "Skip the confirmation prompt" };
        var cmd = new Command("writetest",
            "Ladder step 4: prove a write lands, in scratch flash, verified in a separate session");
        cmd.Options.Add(port); cmd.Options.Add(addrOpt); cmd.Options.Add(yesOpt);
        cmd.SetAction(async (pr, ct) =>
        {
            var radio = Common.Resolve("d878uv");
            uint address = pr.GetValue(addrOpt) is { } t
                ? Convert.ToUInt32(t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t, 16)
                : Plugmatic.Radios.D878uv.Format.Layout.ChannelBanks
                  + 2 * Plugmatic.Radios.D878uv.Format.Layout.BetweenChannelBanks;
            if (address % 16 != 0) throw new CliError("--address must be 16-byte aligned.", 1);
            uint block = Plugmatic.Radios.D878uv.Format.Layout.BankOf(address);

            var runs = new RunManager();
            var run = runs.CreateRun(radio.Model, "dev");
            Console.WriteLine($"Run: {run.Directory}");
            var outcome = RunOutcome.Failed;
            // Every open waits: this command is inherently multi-session, and the radio drops
            // its USB device after each one — including the one before this command started.
            var timeout = TimeSpan.FromSeconds(60);

            async Task<T> InSessionAsync<T>(Func<Plugmatic.Radios.D878uv.Protocol.D878uvProtocol, RadioSession, Task<T>> body)
            {
                var session = await RadioSession.ReopenAsync(radio, pr.GetValue(port), run, timeout, ct);
                try
                {
                    await session.IdentifyAsync(ct);
                    var proto = (Plugmatic.Radios.D878uv.Protocol.D878uvProtocol)session.Protocol;
                    var result = await body(proto, session);
                    await proto.EndSessionAsync(session.Link, ct);      // the commit point
                    return result;
                }
                finally { await session.DisposeAsync(); }
            }

            Task<byte[]> ReadAsync(uint at, int len) =>
                InSessionAsync((proto, s) => proto.ReadRegionAsync(s.Link, at, len, null, "read", ct));

            Task<bool> WriteAsync(byte[] data) => InSessionAsync(async (proto, s) =>
            {
                await proto.WriteChunkAsync(s.Link, address, data, ct);
                return true;
            });

            try
            {
                Console.WriteLine($"Probe 0x{address:X8} sits in erase block 0x{block:X8}" +
                                  $"-0x{block + Plugmatic.Radios.D878uv.Format.Layout.BankStride - 1:X8}.");
                Console.WriteLine("Checking the whole block is erased (a write wipes all of it)...");
                var blockBytes = await ReadAsync(block,
                    (int)Plugmatic.Radios.D878uv.Format.Layout.BankStride);
                int used = blockBytes.Where((b, i) =>
                    b != 0xFF &&
                    !Plugmatic.Radios.D878uv.Format.Layout.IsFlashMarker(block + (uint)i)).Count();
                if (used > 0)
                {
                    throw new CliError(
                        $"Block 0x{block:X8} holds {used} non-FF byte(s), so it is in use. A 16-byte " +
                        "write erases the entire block and reprograms only what this command stages, " +
                        "which would destroy that data. Point --address at an unused block (the " +
                        "default, 0x00880000, is channel bank 2). [d878uv-protocol.md §5.4]", 1);
                }
                Console.WriteLine("Block holds no codeplug data — nothing here to lose.");

                var probe = Enumerable.Range(0, 16).Select(i => (byte)(0x5A ^ i)).ToArray();
                if (!pr.GetValue(yesOpt))
                {
                    Console.Write($"Type WRITE to stage 16 bytes at 0x{address:X8}: ");
                    if (Console.ReadLine()?.Trim() != "WRITE")
                    {
                        outcome = RunOutcome.Aborted;
                        Console.WriteLine("Aborted; nothing sent.");
                        return 1;
                    }
                }

                await WriteAsync(probe);
                var after = await ReadAsync(address, 16);      // new session: the only honest read-back
                bool landed = after.AsSpan().SequenceEqual(probe);
                Console.WriteLine($"After write:  {Convert.ToHexString(after)} — " +
                                  (landed ? "the write took effect" : "the radio IGNORED the write"));

                // Put the block back the way it was: writing FF erases the block and programs
                // FF, so the whole 256 KB returns to erased.
                await WriteAsync(Enumerable.Repeat((byte)0xFF, 16).ToArray());
                var cleaned = await ReadAsync(block,
                    (int)Plugmatic.Radios.D878uv.Format.Layout.BankStride);
                bool clean = !cleaned.Where((b, i) =>
                    b != 0xFF &&
                    !Plugmatic.Radios.D878uv.Format.Layout.IsFlashMarker(block + (uint)i)).Any();
                Console.WriteLine($"Cleanup:      block back to erased: {clean}");

                bool ok = landed && clean;
                Console.WriteLine(ok ? "PASS — write path validated, scratch block left erased."
                                     : "FAIL — see above.");
                run.Extra["writeTest"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["address"] = $"0x{address:X8}", ["eraseBlock"] = $"0x{block:X8}",
                    ["mutationLanded"] = landed, ["blockLeftErased"] = clean,
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
