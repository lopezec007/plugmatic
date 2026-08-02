using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Plugmatic.Core.Runs;

public enum RunOutcome { Success, Failed, Aborted }

public interface IRunManager
{
    RunContext CreateRun(string radioModel, string runType);
    void Finalize(RunContext run, RunOutcome outcome);
}

/// <summary>
/// One directory per run under ~/Plugmatic/radios/&lt;model&gt;/&lt;yyyyMMdd_HHmmss&gt;/ (UTC, D13).
/// Append-only while open (I6): artifacts are written atomically and never overwritten.
/// </summary>
public sealed class RunContext
{
    public required string Directory { get; init; }
    public required string RunType { get; init; }
    public required string RadioModel { get; init; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public List<string> Tags { get; } = [];
    public JsonObject Extra { get; } = [];   // free-form manifest fields (radio, port, inputs, ...)
    public Dictionary<string, string> ArtifactHashes { get; } = [];
    internal bool Finalized;

    public string PathFor(string artifactName) => Path.Combine(Directory, artifactName);

    /// <summary>Atomic write (temp + move). Refuses overwrite and writes after finalize (I6).</summary>
    public string WriteArtifact(string name, ReadOnlySpan<byte> content)
    {
        if (Finalized) throw new InvalidOperationException("Run is finalized; directory is immutable (I6).");
        var dest = PathFor(name);
        if (File.Exists(dest)) throw new InvalidOperationException($"Artifact '{name}' already exists in run (I6, append-only).");
        var tmp = dest + ".tmp";
        File.WriteAllBytes(tmp, content.ToArray());
        File.Move(tmp, dest);
        ArtifactHashes[name] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));
        return dest;
    }

    public string WriteArtifact(string name, string text) =>
        WriteArtifact(name, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Streaming log inside the run dir (e.g. transfer.log). Exempt from the single-shot artifact rule.</summary>
    public StreamWriter OpenLog(string name)
    {
        if (Finalized) throw new InvalidOperationException("Run is finalized (I6).");
        return new StreamWriter(File.Open(PathFor(name), FileMode.Append, FileAccess.Write, FileShare.Read));
    }
}

public sealed class RunManager : IRunManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToolVersion { get; init; } =
        typeof(RunManager).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public RunContext CreateRun(string radioModel, string runType)
    {
        var baseDir = PlugmaticPaths.RadioDir(radioModel);
        Directory.CreateDirectory(baseDir);
        // UTC timestamp dir; suffix -2, -3... on collision within the same second.
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var dir = Path.Combine(baseDir, stamp);
        for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(baseDir, $"{stamp}-{i}");
        Directory.CreateDirectory(dir);
        return new RunContext { Directory = dir, RunType = runType, RadioModel = radioModel };
    }

    public void Finalize(RunContext run, RunOutcome outcome)
    {
        if (run.Finalized) return;
        var manifest = new JsonObject
        {
            ["runType"] = run.RunType,
            ["tags"] = new JsonArray([.. run.Tags.Select(t => JsonValue.Create(t))]),
            ["startedUtc"] = run.StartedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["finishedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["outcome"] = outcome.ToString().ToLowerInvariant(),
            ["toolVersion"] = ToolVersion,
            ["radio"] = new JsonObject { ["model"] = run.RadioModel },
        };
        foreach (var (k, v) in run.Extra)
            manifest[k] = v?.DeepClone();
        manifest["artifacts"] = new JsonObject(
            run.ArtifactHashes.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value))));

        var tmp = run.PathFor("manifest.json.tmp");
        File.WriteAllText(tmp, manifest.ToJsonString(JsonOpts));
        File.Move(tmp, run.PathFor("manifest.json"), overwrite: false);
        run.Finalized = true;
    }
}
