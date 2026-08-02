namespace Plugmatic.Core;

/// <summary>Filesystem contract — implementation spec §5 (LOCKED D6).</summary>
public static class PlugmaticPaths
{
    public static string Root { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Plugmatic");

    public static string ConfigDir => Path.Combine(Root, "config");
    public static string ConfigFile => Path.Combine(ConfigDir, "config.yaml");
    public static string CredentialsFile => Path.Combine(ConfigDir, "credentials.dat");
    public static string CacheDir => Path.Combine(ConfigDir, "cache");
    public static string RepeaterCacheDb => Path.Combine(CacheDir, "repeaters.sqlite");
    public static string ProfilesDir => Path.Combine(ConfigDir, "profiles");
    public static string RadiosDir => Path.Combine(Root, "radios");
    public static string RadioDir(string model) => Path.Combine(RadiosDir, model);

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(RadiosDir);
    }
}
