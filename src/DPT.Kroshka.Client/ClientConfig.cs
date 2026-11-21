using DPT.Kroshka.Shared;

namespace DPT.Kroshka.Client;

/// <summary>
/// Client-side preferences, kept in %APPDATA%\Kroshka\client.ini.
///
/// The important one is Mode:
///   Local  - this window starts its own server (the "family hub" case, no extra
///            machine needed) and points at 127.0.0.1.
///   Remote - it only connects to a server someone else runs, which is what a
///            phone or a second PC in the LAN does.
/// </summary>
public sealed class ClientConfig
{
    public string Mode = "local";          // local | remote
    public string ServerUrl = "";          // used when mode=remote
    public string ServerExe = "";          // optional explicit path to KroshkaServer.exe
    public int LocalPort = 6767;
    public bool StartMinimized = false;
    public bool CloseToTray = true;
    public bool StartWithWindows = false;
    public bool Notifications = true;
    public string WebViewDataDir = "";     // empty = %LOCALAPPDATA%\Kroshka\WebView2

    public static string Dir
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kroshka");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string ConfigPath => System.IO.Path.Combine(Dir, "client.ini");

    public static ClientConfig Load()
    {
        ClientConfig cfg = new();
        if (!File.Exists(ConfigPath)) return cfg;

        try
        {
            Ini ini = Ini.Parse(File.ReadAllText(ConfigPath));

            cfg.Mode = ini.Get("client", "mode", cfg.Mode).ToLowerInvariant();
            cfg.ServerUrl = ini.Get("client", "serverUrl", cfg.ServerUrl);
            cfg.ServerExe = ini.Get("client", "serverExe", cfg.ServerExe);
            cfg.LocalPort = ini.GetInt("client", "localPort", cfg.LocalPort);
            cfg.StartMinimized = ini.GetBool("client", "startMinimized", cfg.StartMinimized);
            cfg.CloseToTray = ini.GetBool("client", "closeToTray", cfg.CloseToTray);
            cfg.StartWithWindows = ini.GetBool("client", "startWithWindows", cfg.StartWithWindows);
            cfg.Notifications = ini.GetBool("client", "notifications", cfg.Notifications);
            cfg.WebViewDataDir = ini.Get("client", "webViewDataDir", cfg.WebViewDataDir);
        }
        catch (Exception ex)
        {
            Log.Warn("CLIENT", "cannot read client.ini: " + ex.Message);
        }

        if (cfg.Mode is not ("local" or "remote")) cfg.Mode = "local";
        if (cfg.LocalPort is < 1 or > 65535) cfg.LocalPort = 6767;

        return cfg;
    }

    public void Save()
    {
        try
        {
            string text =
                "; Kroshka client settings\r\n" +
                "; mode = local  -> this window runs its own server\r\n" +
                "; mode = remote -> connect to an existing server in the LAN\r\n" +
                "[client]\r\n" +
                $"mode={Mode}\r\n" +
                $"serverUrl={ServerUrl}\r\n" +
                $"serverExe={ServerExe}\r\n" +
                $"localPort={LocalPort}\r\n" +
                $"startMinimized={(StartMinimized ? "true" : "false")}\r\n" +
                $"closeToTray={(CloseToTray ? "true" : "false")}\r\n" +
                $"startWithWindows={(StartWithWindows ? "true" : "false")}\r\n" +
                $"notifications={(Notifications ? "true" : "false")}\r\n" +
                $"webViewDataDir={WebViewDataDir}\r\n";

            FileStore.WriteAtomic(ConfigPath, text);
        }
        catch (Exception ex)
        {
            Log.Warn("CLIENT", "cannot save client.ini: " + ex.Message);
        }
    }

    public string EffectiveWebViewDir
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(WebViewDataDir)) return WebViewDataDir;
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kroshka", "WebView2");
        }
    }
}
