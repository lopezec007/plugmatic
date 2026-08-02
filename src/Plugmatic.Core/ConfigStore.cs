using YamlDotNet.Serialization;

namespace Plugmatic.Core;

/// <summary>Non-secret settings in ~/Plugmatic/config/config.yaml (flat key/value). [spec §7.1]</summary>
public static class ConfigStore
{
    public static Dictionary<string, string> Load()
    {
        if (!File.Exists(PlugmaticPaths.ConfigFile)) return [];
        var text = File.ReadAllText(PlugmaticPaths.ConfigFile);
        if (string.IsNullOrWhiteSpace(text)) return [];
        return new DeserializerBuilder().Build().Deserialize<Dictionary<string, string>>(text) ?? [];
    }

    public static void Set(string key, string value)
    {
        var all = Load();
        all[key] = value;
        Save(all);
    }

    public static void Remove(string key)
    {
        var all = Load();
        all.Remove(key);
        Save(all);
    }

    public static string? Get(string key) => Load().GetValueOrDefault(key);

    private static void Save(Dictionary<string, string> all)
    {
        PlugmaticPaths.EnsureCreated();
        var tmp = PlugmaticPaths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, new SerializerBuilder().Build().Serialize(all));
        File.Move(tmp, PlugmaticPaths.ConfigFile, overwrite: true);
    }
}

/// <summary>GMRS TX acknowledgment state (D8): stored decision + timestamp, no per-run override.</summary>
public static class GmrsPolicyStore
{
    public const string EnabledKey = "gmrs.txEnabled";
    public const string AckKey = "gmrs.acknowledgedUtc";

    public static (bool TxEnabled, string? AcknowledgedUtc) Get()
    {
        var cfg = ConfigStore.Load();
        return (cfg.GetValueOrDefault(EnabledKey) == "true", cfg.GetValueOrDefault(AckKey));
    }

    public static void Enable(DateTime utcNow)
    {
        ConfigStore.Set(EnabledKey, "true");
        ConfigStore.Set(AckKey, utcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
    }

    public static void Disable()
    {
        ConfigStore.Set(EnabledKey, "false");
        ConfigStore.Remove(AckKey);
    }
}
