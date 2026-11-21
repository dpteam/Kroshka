using System.Text;
using DPT.Kroshka.Shared;

namespace DPT.Kroshka.Linux;

/// <summary>
/// Checks the parts of the Linux client that can be verified without a Linux
/// desktop: path resolution, URL normalisation, window sizes, browser selection
/// and the generated desktop/systemd files.
///
/// This exists because the GUI itself cannot be exercised from a build machine.
/// Everything that can be wrong without a screen being visible is checked here.
/// </summary>
public static class SelfTest
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> _failures = new();

    public static int Run()
    {
        Console.WriteLine("Kroshka Linux self-test");
        Console.WriteLine();

        TestXdgPaths();
        TestUrlNormalization();
        TestWindowSize();
        TestBrowserResolution();
        TestConfigRoundTrip();
        TestDesktopFile();
        TestServiceFile();

        Console.WriteLine();
        foreach (string failure in _failures) Console.WriteLine("  FAIL " + failure);
        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");

        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ checks

    private static void Check(string name, bool condition, string detail = "")
    {
        detail ??= "";

        if (condition)
        {
            _passed++;
            Console.WriteLine("  ok   " + name);
        }
        else
        {
            _failed++;
            _failures.Add(name + (detail.Length > 0 ? ": " + detail : ""));
            Console.WriteLine("  FAIL " + name + (detail.Length > 0 ? ": " + detail : ""));
        }
    }

    private static void Section(string name)
    {
        Console.WriteLine(name);
    }

    // ------------------------------------------------------------------- tests

    private static void TestXdgPaths()
    {
        Section("xdg paths");

        string original = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string originalData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "/tmp/xdg-test/config");
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", "/tmp/xdg-test/data");

            // LinuxPaths caches nothing, so the new values apply immediately.
            Check("config honours XDG_CONFIG_HOME",
                LinuxPaths.ConfigDir.StartsWith("/tmp/xdg-test/config", StringComparison.Ordinal),
                LinuxPaths.ConfigDir);

            Check("config file named client.ini",
                LinuxPaths.ClientConfigFile.EndsWith("client.ini", StringComparison.Ordinal),
                LinuxPaths.ClientConfigFile);

            Check("data honours XDG_DATA_HOME",
                LinuxPaths.DataDir.StartsWith("/tmp/xdg-test/data", StringComparison.Ordinal),
                LinuxPaths.DataDir);

            Check("server root under data",
                LinuxPaths.ServerRoot.Contains("server", StringComparison.Ordinal),
                LinuxPaths.ServerRoot);

            // A relative XDG value is invalid per spec and must be ignored, not
            // silently turned into a directory next to the working directory.
            // The check is "rooted", not "starts with /": the self-test also runs
            // on Windows where a rooted path starts with a drive letter.
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "relative/path");
            Check("relative XDG value rejected",
                Path.IsPathRooted(LinuxPaths.ConfigDir),
                LinuxPaths.ConfigDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", original);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", originalData);
        }
    }

    private static void TestUrlNormalization()
    {
        Section("url normalization");

        // Mirrors the launcher's NormalizeUrl, which is private to Program.
        static string Normalize(string input)
        {
            string value = (input ?? "").Trim();
            if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;

            if (Uri.TryCreate(value, UriKind.Absolute, out Uri uri))
            {
                int port = uri.IsDefaultPort ? 6767 : uri.Port;
                return $"{uri.Scheme}://{uri.Host}:{port}";
            }

            return value;
        }

        Check("bare ip", Normalize("192.168.1.250") == "http://192.168.1.250:6767", Normalize("192.168.1.250"));
        Check("ip with port", Normalize("192.168.1.250:6767") == "http://192.168.1.250:6767");
        Check("http scheme", Normalize("http://192.168.1.250") == "http://192.168.1.250:6767");
        Check("http with port", Normalize("http://192.168.1.250:6767") == "http://192.168.1.250:6767");
        Check("trailing slash dropped", Normalize("http://192.168.1.250:6767/") == "http://192.168.1.250:6767");
        Check("https kept", Normalize("https://chat.example.com") == "https://chat.example.com:6767");
        Check("localhost", Normalize("localhost:7000") == "http://localhost:7000");
        Check("whitespace trimmed", Normalize("  192.168.1.250  ") == "http://192.168.1.250:6767");
    }

    private static void TestWindowSize()
    {
        Section("window size");

        (int w, int h) = BrowserLauncher.ParseSize("1200x820");
        Check("parses WxH", w == 1200 && h == 820, $"{w}x{h}");

        (w, h) = BrowserLauncher.ParseSize("1200X820");
        Check("case insensitive", w == 1200 && h == 820);

        (w, h) = BrowserLauncher.ParseSize("garbage");
        Check("garbage rejected", w == 0 && h == 0);

        (w, h) = BrowserLauncher.ParseSize("10x10");
        Check("tiny size rejected", w == 0 && h == 0);

        (w, h) = BrowserLauncher.ParseSize("");
        Check("empty rejected", w == 0 && h == 0);
    }

    private static void TestBrowserResolution()
    {
        Section("browser resolution");

        string originalPath = Environment.GetEnvironmentVariable("PATH");
        string fakeDir = Path.Combine(Path.GetTempPath(), "kroshka-fake-bin-" + Crypto.NewId(4));

        try
        {
            Directory.CreateDirectory(fakeDir);

            // Nothing on PATH: the launcher must say so instead of throwing.
            Environment.SetEnvironmentVariable("PATH", fakeDir);
            (BrowserLauncher.Kind none, string noExe) = BrowserLauncher.Resolve("auto");
            Check("no browser yields None", none == BrowserLauncher.Kind.None, noExe ?? "null");

            // Only firefox present.
            File.WriteAllText(Path.Combine(fakeDir, "firefox"), "#!/bin/sh\n");
            (BrowserLauncher.Kind ff, string ffExe) = BrowserLauncher.Resolve("auto");
            Check("firefox detected", ff == BrowserLauncher.Kind.Firefox, ffExe);

            // Chromium wins over firefox when both exist.
            File.WriteAllText(Path.Combine(fakeDir, "chromium"), "#!/bin/sh\n");
            (BrowserLauncher.Kind chrome, string chromeExe) = BrowserLauncher.Resolve("auto");
            Check("chromium preferred", chrome == BrowserLauncher.Kind.Chromium, chromeExe);

            // An explicit firefox preference is respected.
            (BrowserLauncher.Kind forced, _) = BrowserLauncher.Resolve("firefox");
            Check("explicit firefox wins", forced == BrowserLauncher.Kind.Firefox);

            // A known Chromium-family name that is not installed falls back to the
            // rest of the family rather than refusing to start.
            (BrowserLauncher.Kind vivaldi, _) = BrowserLauncher.Resolve("vivaldi");
            Check("missing family member falls back to chromium",
                vivaldi == BrowserLauncher.Kind.Chromium, vivaldi.ToString());

            // A completely unknown value that IS a real binary is used directly.
            File.WriteAllText(Path.Combine(fakeDir, "my-chromium-fork"), "#!/bin/sh\n");
            (BrowserLauncher.Kind literal, string literalExe) = BrowserLauncher.Resolve("my-chromium-fork");
            Check("unknown name used as literal binary",
                literal == BrowserLauncher.Kind.Chromium && literalExe != null,
                literalExe ?? "null");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            try { Directory.Delete(fakeDir, true); } catch { }
        }
    }

    private static void TestConfigRoundTrip()
    {
        Section("client config");

        string originalConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string temp = Path.Combine(Path.GetTempPath(), "kroshka-linux-cfg-" + Crypto.NewId(4));

        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", temp);

            LinuxClientConfig original = new()
            {
                Mode = "remote",
                ServerUrl = "http://192.168.1.250:6767",
                LocalPort = 7000,
                Browser = "firefox",
                WindowSize = "1024x768",
                OpenBrowser = false
            };

            original.Save();
            Check("config file written", File.Exists(LinuxPaths.ClientConfigFile));

            LinuxClientConfig loaded = LinuxClientConfig.Load();
            Check("mode round-trips", loaded.Mode == "remote", loaded.Mode);
            Check("url round-trips", loaded.ServerUrl == "http://192.168.1.250:6767", loaded.ServerUrl);
            Check("port round-trips", loaded.LocalPort == 7000, loaded.LocalPort.ToString());
            Check("browser round-trips", loaded.Browser == "firefox", loaded.Browser);
            Check("window round-trips", loaded.WindowSize == "1024x768", loaded.WindowSize);
            Check("openBrowser round-trips", loaded.OpenBrowser == false);

            // A bad port in the file must not reach Kestrel.
            File.WriteAllText(LinuxPaths.ClientConfigFile, "[client]\r\nlocalPort=99999\r\n");
            LinuxClientConfig clamped = LinuxClientConfig.Load();
            Check("bad port clamped", clamped.LocalPort == 6767, clamped.LocalPort.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", originalConfig);
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    private static void TestDesktopFile()
    {
        Section("desktop entry");

        string originalData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string temp = Path.Combine(Path.GetTempPath(), "kroshka-linux-desktop-" + Crypto.NewId(4));

        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", temp);

            string path = DesktopIntegration.InstallDesktopFile("/opt/Kroshka/kroshka");
            Check("desktop file written", File.Exists(path), path);

            string content = File.ReadAllText(path);

            Check("has Type=Application", content.Contains("Type=Application"));
            Check("has Name", content.Contains("Name=Kroshka"));
            Check("exec is quoted", content.Contains("Exec=\"/opt/Kroshka/kroshka\""), content);
            Check("icon set", content.Contains("Icon=kroshka"));
            Check("StartupWMClass matches --class", content.Contains("StartupWMClass=Kroshka"));
            Check("category is chat", content.Contains("Network;Chat"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", originalData);
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    private static void TestServiceFile()
    {
        Section("systemd unit");

        string originalConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string temp = Path.Combine(Path.GetTempPath(), "kroshka-linux-service-" + Crypto.NewId(4));

        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", temp);

            string path = DesktopIntegration.InstallService("/opt/Kroshka/kroshka", 6767);
            Check("unit written", File.Exists(path), path);

            string content = File.ReadAllText(path);

            Check("runs headless", content.Contains("--headless"));
            Check("has port", content.Contains("--port 6767"));
            Check("restarts on failure", content.Contains("Restart=on-failure"));
            Check("stops on SIGTERM", content.Contains("KillSignal=SIGTERM"));
            Check("enabled by default", content.Contains("WantedBy=default.target"));
            Check("waits for network", content.Contains("network-online.target"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", originalConfig);
            try { Directory.Delete(temp, true); } catch { }
        }
    }
}
