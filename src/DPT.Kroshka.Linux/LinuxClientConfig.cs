using DPT.Kroshka.Shared;

namespace DPT.Kroshka.Linux;

/// <summary>
/// Client settings on Linux, stored in the same INI shape as the Windows client
/// so a config can be copied between the two without translation.
///
///   mode        local | remote
///   serverUrl   used when mode = remote
///   localPort   port for the embedded server
///   browser     chromium | firefox | default | (empty = auto)
///   windowSize  WxH for the app window, e.g. 1200x800
/// </summary>
public sealed class LinuxClientConfig
{
    public string Mode = "local";
    public string ServerUrl = "";
    public int LocalPort = 6767;
    public string Browser = "";
    public string WindowSize = "1200x820";
    public bool OpenBrowser = true;
    public bool Discoverable = true;

    public static LinuxClientConfig Load()
    {
        LinuxClientConfig cfg = new();

        try
        {
            if (!File.Exists(LinuxPaths.ClientConfigFile)) return cfg;

            Ini ini = Ini.Parse(File.ReadAllText(LinuxPaths.ClientConfigFile));

            cfg.Mode = ini.Get("client", "mode", cfg.Mode).Trim().ToLowerInvariant();
            cfg.ServerUrl = ini.Get("client", "serverUrl", cfg.ServerUrl).Trim();
            cfg.LocalPort = ini.GetInt("client", "localPort", cfg.LocalPort);
            cfg.Browser = ini.Get("client", "browser", cfg.Browser).Trim().ToLowerInvariant();
            cfg.WindowSize = ini.Get("client", "windowSize", cfg.WindowSize).Trim();
            cfg.OpenBrowser = ini.GetBool("client", "openBrowser", cfg.OpenBrowser);
            cfg.Discoverable = ini.GetBool("server", "discoverable", cfg.Discoverable);
        }
        catch (Exception ex)
        {
            Log.Warn("LINUX", "cannot read client.ini: " + ex.Message);
        }

        if (cfg.Mode is not ("local" or "remote")) cfg.Mode = "local";
        if (cfg.LocalPort is < 1024 or > 65535) cfg.LocalPort = 6767;

        return cfg;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(LinuxPaths.ConfigDir);

            string text =
                "; Kroshka — клиентские настройки (Linux)\r\n" +
                "; Формат совпадает с Windows-версией, файл можно переносить.\r\n" +
                "[client]\r\n" +
                "; local  — этот компьютер запускает сервер сам\r\n" +
                "; remote — подключение к серверу в сети\r\n" +
                $"mode={Mode}\r\n" +
                $"serverUrl={ServerUrl}\r\n" +
                $"localPort={LocalPort}\r\n" +
                "; auto | chromium | firefox | default\r\n" +
                $"browser={Browser}\r\n" +
                $"windowSize={WindowSize}\r\n" +
                $"openBrowser={(OpenBrowser ? "true" : "false")}\r\n" +
                "\r\n" +
                "[server]\r\n" +
                "; Отвечать на автопоиск из локальной сети\r\n" +
                $"discoverable={(Discoverable ? "true" : "false")}\r\n";

            FileStore.WriteAtomic(LinuxPaths.ClientConfigFile, text);
        }
        catch (Exception ex)
        {
            Log.Warn("LINUX", "cannot write client.ini: " + ex.Message);
        }
    }

    /// <summary>Writes a commented default file the first time the app runs.</summary>
    public static void EnsureExists()
    {
        try
        {
            if (File.Exists(LinuxPaths.ClientConfigFile)) return;
            new LinuxClientConfig().Save();
        }
        catch { }
    }
}
