using System.Text;

namespace DPT.Kroshka.Linux;

/// <summary>
/// Makes Kroshka appear in the application menu and optionally start with the
/// session.
///
/// Both are per-user, under $XDG_DATA_HOME and $XDG_CONFIG_HOME: a chat client
/// has no business asking for root, and a system-wide install makes an uninstall
/// that actually removes things much harder than it needs to be.
/// </summary>
public static class DesktopIntegration
{
    private const string DesktopFileName = "kroshka.desktop";
    private const string ServiceName = "kroshka.service";

    public static string ApplicationsDir =>
        Path.Combine(XdgDataHome(), "applications");

    public static string IconsDir =>
        Path.Combine(XdgDataHome(), "icons", "hicolor");

    public static string SystemdUserDir =>
        Path.Combine(LinuxPaths.ConfigDir, "..", "systemd", "user");

    /// <summary>
    /// Writes the .desktop entry. The Exec line quotes the binary path because an
    /// install under a directory with a space would otherwise be split into two
    /// arguments and silently fail to start.
    /// </summary>
    public static string InstallDesktopFile(string executable)
    {
        Directory.CreateDirectory(ApplicationsDir);

        string content =
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=Kroshka\n" +
            "GenericName=Family Messenger\n" +
            "Comment=Мессенджер для своих\n" +
            "Exec=\"" + executable + "\"\n" +
            "Icon=kroshka\n" +
            "Terminal=false\n" +
            "Categories=Network;Chat;InstantMessaging;\n" +
            "Keywords=chat;messenger;lan;\n" +
            "StartupNotify=true\n" +
            // WM_CLASS is what lets the dock group the browser window under our
            // icon instead of under the browser's.
            "StartupWMClass=Kroshka\n";

        string path = Path.Combine(ApplicationsDir, DesktopFileName);
        File.WriteAllText(path, content, new UTF8Encoding(false));

        TryRun("chmod", "+x", path);
        TryRun("update-desktop-database", ApplicationsDir);

        return path;
    }

    /// <summary>
    /// Writes a systemd --user unit so the server can run without a terminal.
    /// Restart=on-failure plus a delay is what keeps a LAN hub alive across a
    /// crashed port bind or a temporary network drop.
    /// </summary>
    public static string InstallService(string executable, int port)
    {
        string dir = Path.GetFullPath(SystemdUserDir);
        Directory.CreateDirectory(dir);

        string content =
            "[Unit]\n" +
            "Description=Kroshka messenger server\n" +
            "Documentation=https://github.com/dpteam/Kroshka\n" +
            "After=network-online.target\n" +
            "Wants=network-online.target\n" +
            "\n" +
            "[Service]\n" +
            "Type=simple\n" +
            "ExecStart=\"" + executable + "\" --headless --port " + port + "\n" +
            "Restart=on-failure\n" +
            "RestartSec=5\n" +
            "\n" +
            "# The chat data is the point; a hard kill should not lose it.\n" +
            "KillSignal=SIGTERM\n" +
            "TimeoutStopSec=20\n" +
            "\n" +
            "[Install]\n" +
            "WantedBy=default.target\n";

        string path = Path.Combine(dir, ServiceName);
        File.WriteAllText(path, content, new UTF8Encoding(false));

        TryRun("systemctl", "--user", "daemon-reload");

        return path;
    }

    /// <summary>Copies the shipped PNG icons into the hicolor theme.</summary>
    public static int InstallIcons(string sourceDir)
    {
        if (!Directory.Exists(sourceDir)) return 0;

        int copied = 0;

        foreach (string file in Directory.GetFiles(sourceDir, "*.png"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (!name.StartsWith("kroshka-", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(name["kroshka-".Length..], out int size)) continue;

            string target = Path.Combine(IconsDir, $"{size}x{size}", "apps");
            Directory.CreateDirectory(target);

            try
            {
                File.Copy(file, Path.Combine(target, "kroshka.png"), overwrite: true);
                copied++;
            }
            catch { }
        }

        if (copied > 0) TryRun("gtk-update-icon-cache", "-f", "-t", IconsDir);

        return copied;
    }

    public static bool Uninstall()
    {
        bool removed = false;

        foreach (string path in new[]
                 {
                     Path.Combine(ApplicationsDir, DesktopFileName),
                     Path.Combine(Path.GetFullPath(SystemdUserDir), ServiceName)
                 })
        {
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                removed = true;
            }
            catch { }
        }

        TryRun("systemctl", "--user", "daemon-reload");
        return removed;
    }

    private static string XdgDataHome()
    {
        string value = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

        if (!string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value))
            return value;

        return Path.Combine(LinuxPaths.Home, ".local", "share");
    }

    /// <summary>
    /// Runs an optional helper (chmod, update-desktop-database, systemctl). These
    /// are conveniences: a missing one must not fail the install.
    /// </summary>
    private static void TryRun(string command, params string[] args)
    {
        try
        {
            System.Diagnostics.ProcessStartInfo info = new()
            {
                FileName = command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string arg in args) info.ArgumentList.Add(arg);

            using System.Diagnostics.Process process = System.Diagnostics.Process.Start(info);
            process?.WaitForExit(3000);
        }
        catch { }
    }
}
