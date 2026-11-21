namespace DPT.Kroshka.Linux;

/// <summary>
/// Where things live on a Linux desktop, following the XDG Base Directory spec.
///
/// Getting this wrong is the most common way a .NET app feels foreign on Linux:
/// writing to ~/.kroshka when the user expects ~/.config/Kroshka, or putting
/// caches in the data directory where backups would copy them forever.
///
///   config  $XDG_CONFIG_HOME/Kroshka    ~/.config/Kroshka
///   data    $XDG_DATA_HOME/Kroshka      ~/.local/share/Kroshka
///   state   $XDG_STATE_HOME/Kroshka     ~/.local/state/Kroshka
///   cache   $XDG_CACHE_HOME/Kroshka     ~/.cache/Kroshka
/// </summary>
public static class LinuxPaths
{
    public static string Home =>
        FirstNonEmpty(Environment.GetEnvironmentVariable("HOME"), SafeFolder(Environment.SpecialFolder.UserProfile), "/tmp");

    public static string ConfigDir => Resolve("XDG_CONFIG_HOME", ".config");

    public static string DataDir => Resolve("XDG_DATA_HOME", Path.Combine(".local", "share"));

    public static string StateDir => Resolve("XDG_STATE_HOME", Path.Combine(".local", "state"));

    public static string CacheDir => Resolve("XDG_CACHE_HOME", ".cache");

    /// <summary>The client's own settings file, mirroring the Windows layout.</summary>
    public static string ClientConfigFile => Path.Combine(ConfigDir, "client.ini");

    /// <summary>Embedded server root: config, data, logs, web all live under here.</summary>
    public static string ServerRoot => Path.Combine(DataDir, "server");

    public static string ServerDataDir => Path.Combine(ServerRoot, "data");

    public static string ServerBlobDir => Path.Combine(ServerDataDir, "blobs");

    public static string ServerBackupDir => Path.Combine(ServerDataDir, "backups");

    public static string LogDir => Path.Combine(StateDir, "logs");

    /// <summary>Single-instance lock, kept in state rather than cache: it is not disposable.</summary>
    public static string LockFile => Path.Combine(StateDir, "kroshka.lock");

    /// <summary>Where a running instance records the URL it serves, for a second launch.</summary>
    public static string UrlFile => Path.Combine(StateDir, "url");

    /// <summary>
    /// Returns the XDG directory for a variable, falling back to the spec's default
    /// under $HOME. An absolute path is required by the spec, so a relative value
    /// is ignored rather than creating a directory somewhere unexpected.
    /// </summary>
    private static string Resolve(string variable, string fallbackRelative)
    {
        string value = Environment.GetEnvironmentVariable(variable);

        if (!string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value))
            return Path.Combine(value, "Kroshka");

        return Path.Combine(Home, fallbackRelative, "Kroshka");
    }

    public static void EnsureDirectories()
    {
        foreach (string dir in new[] { ConfigDir, DataDir, StateDir, CacheDir, LogDir, ServerDataDir })
        {
            try { Directory.CreateDirectory(dir); }
            catch { /* a read-only home is the user's problem, not a crash */ }
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value;

        return "/tmp";
    }

    private static string SafeFolder(Environment.SpecialFolder folder)
    {
        try { return Environment.GetFolderPath(folder); }
        catch { return null; }
    }
}
