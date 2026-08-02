using Plugmatic.Core;
using Plugmatic.Core.Runs;

namespace Plugmatic.Tests;

public class RunManagerTests : IDisposable
{
    private readonly string _tmpRoot;
    private readonly string _savedRoot;

    public RunManagerTests()
    {
        _savedRoot = PlugmaticPaths.Root;
        _tmpRoot = Path.Combine(Path.GetTempPath(), "plugmatic-test-" + Guid.NewGuid().ToString("N"));
        PlugmaticPaths.Root = _tmpRoot;
    }

    public void Dispose()
    {
        PlugmaticPaths.Root = _savedRoot;
        try { Directory.Delete(_tmpRoot, recursive: true); } catch { }
    }

    [Fact]
    public void Run_dir_uses_utc_stamp_under_radio_model()
    {
        var rm = new RunManager();
        var run = rm.CreateRun("dm32uv", "read");
        Assert.StartsWith(Path.Combine(_tmpRoot, "radios", "dm32uv"), run.Directory);
        var name = Path.GetFileName(run.Directory);
        Assert.Matches(@"^\d{8}_\d{6}(-\d+)?$", name);
    }

    [Fact]
    public void Artifacts_are_append_only_and_hashed()
    {
        var rm = new RunManager();
        var run = rm.CreateRun("dm32uv", "read");
        run.WriteArtifact("read.bin", new byte[] { 1, 2, 3 });
        // I6: no overwrite while open
        Assert.Throws<InvalidOperationException>(() => run.WriteArtifact("read.bin", new byte[] { 9 }));
        rm.Finalize(run, RunOutcome.Success);
        // I6: immutable after finalize
        Assert.Throws<InvalidOperationException>(() => run.WriteArtifact("other.bin", new byte[] { 9 }));

        var manifest = File.ReadAllText(Path.Combine(run.Directory, "manifest.json"));
        Assert.Contains("\"outcome\": \"success\"", manifest);
        Assert.Contains("sha256:039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81", manifest);
        Assert.Contains("\"runType\": \"read\"", manifest);
    }

    [Fact]
    public void Failed_runs_finalize_with_outcome()
    {
        var rm = new RunManager();
        var run = rm.CreateRun("dm32uv", "write");
        rm.Finalize(run, RunOutcome.Failed);
        Assert.Contains("\"outcome\": \"failed\"", File.ReadAllText(Path.Combine(run.Directory, "manifest.json")));
    }

    [Fact]
    public void Tags_land_in_manifest()
    {
        var rm = new RunManager();
        var run = rm.CreateRun("dm32uv", "read");
        run.Tags.Add("factory-golden");
        rm.Finalize(run, RunOutcome.Success);
        Assert.Contains("factory-golden", File.ReadAllText(Path.Combine(run.Directory, "manifest.json")));
    }
}
