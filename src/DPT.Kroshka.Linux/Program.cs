using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DPT.Kroshka.Linux;
using DPT.Kroshka.Server;
using DPT.Kroshka.Shared;

Console.OutputEncoding = Encoding.UTF8;

// ------------------------------------------------------------------ arguments

Options options;
try
{
    options = Options.Parse(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine("kroshka: " + ex.Message);
    Console.Error.WriteLine("Попробуйте: kroshka --help");
    return 2;
}

if (options.ShowHelp)
{
    PrintHelp();
    return 0;
}

if (options.ShowVersion)
{
    Console.WriteLine($"Kroshka {Protocol.Version} (Linux, .NET {Environment.Version})");
    return 0;
}

LinuxPaths.EnsureDirectories();

if (options.SelfTest)
{
    return DPT.Kroshka.Linux.SelfTest.Run();
}

// ------------------------------------------------------- desktop integration

if (options.InstallDesktop || options.InstallService || options.Uninstall)
{
    return RunIntegration(options);
}

// --------------------------------------------------------- single instance

// A second launch should not start a second server on the same port. Instead it
// reuses the running one and opens another window at it, which is what a person
// clicking the icon twice actually wants.
//
// An explicit --remote is the exception: the person asked for a specific server,
// so redirecting them to whatever is already running would ignore the request.
bool explicitTarget = options.Mode == "remote" || options.ServerUrl != null;

string runningUrl = ReadRunningUrl();
if (runningUrl != null && !options.Headless && !explicitTarget)
{
    Console.WriteLine("Kroshka уже запущена: " + runningUrl);

    if (options.OpenBrowser)
    {
        LinuxClientConfig cfg = LinuxClientConfig.Load();
        BrowserLauncher.Open(runningUrl, cfg, ProfileDir());
    }

    return 0;
}

// ------------------------------------------------------------- configuration

LinuxClientConfig config = LinuxClientConfig.Load();
LinuxClientConfig.EnsureExists();

if (options.Mode != null) config.Mode = options.Mode;
if (options.Port.HasValue) config.LocalPort = options.Port.Value;
if (options.ServerUrl != null) { config.Mode = "remote"; config.ServerUrl = options.ServerUrl; }
if (options.Headless) config.OpenBrowser = false;

// ------------------------------------------------------------------- logging

KroshkaConfig serverConfig = BuildServerConfig(config);
Log.Configure(serverConfig);

Log.Info("LINUX", $"starting: mode={config.Mode} port={config.LocalPort} headless={options.Headless}");

// --------------------------------------------------------------------- serve

App server = null;
string url;

try
{
    if (config.Mode == "remote")
    {
        if (string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            Console.Error.WriteLine("Режим remote, но адрес сервера не задан.");
            Console.Error.WriteLine("Укажите его: kroshka --remote 192.168.1.250:6767");
            return 1;
        }

        url = NormalizeUrl(config.ServerUrl);
        Log.Info("LINUX", "remote mode: " + url);

        if (!await PingAsync(url))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Сервер " + url + " не отвечает.");
            Console.Error.WriteLine("  Проверьте адрес и что сервер запущен.");
            Console.Error.WriteLine();
            return 1;
        }
    }
    else
    {
        server = new App(serverConfig);

        if (!await server.StartAsync())
        {
            Console.Error.WriteLine("Не удалось запустить сервер. Логи: " + LinuxPaths.LogDir);
            return 1;
        }

        url = $"http://127.0.0.1:{server.PublicPort}/";
        Log.Info("LINUX", "local server ready: " + url);
    }
}
catch (Exception ex)
{
    Log.Error("LINUX", ex);
    Console.Error.WriteLine("Ошибка запуска: " + ex.Message);
    return 1;
}

// ------------------------------------------------------------------ announce

Console.WriteLine();
Console.WriteLine("  Kroshka " + Protocol.Version);
Console.WriteLine("  " + url);

string lanUrl = LanAddress(server?.PublicPort ?? UriOf(url).Port);
if (lanUrl != null) Console.WriteLine("  для других в сети: " + lanUrl);

if (server != null)
    Console.WriteLine("  админка: http://127.0.0.1:" + server.AdminPort + "/admin");

Console.WriteLine();

if (config.OpenBrowser)
{
    BrowserLauncher.Result launched = BrowserLauncher.Open(url, config, ProfileDir());

    if (!launched.Started)
    {
        Console.WriteLine(BrowserLauncher.FallbackHint(url));
    }
}
else
{
    Console.WriteLine(BrowserLauncher.FallbackHint(url));
}

// In remote mode this process hosts nothing: it only opened a window at somebody
// else's server, so it has no reason to stay alive and block the terminal.
if (server == null)
{
    Log.Info("LINUX", "remote mode: window opened, exiting");
    Log.Shutdown();
    return 0;
}

WriteRunningUrl(url);

// ------------------------------------------------------------------ shutdown

using CancellationTokenSource shutdown = new();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

// SIGTERM is how systemd and `kill` stop a process; without this the unit would
// need a SIGKILL and the chat could lose its last write.
using PosixSignalRegistration sigterm = RegisterSignal(PosixSignal.SIGTERM, shutdown);
using PosixSignalRegistration sigint = RegisterSignal(PosixSignal.SIGINT, shutdown);

Console.WriteLine("  Ctrl+C — остановить.");
Console.WriteLine();

try
{
    await Task.Delay(Timeout.Infinite, shutdown.Token);
}
catch (OperationCanceledException) { }

Log.Info("LINUX", "shutting down");

if (server != null)
{
    try { await server.StopAsync(); } catch (Exception ex) { Log.Warn("LINUX", "stop: " + ex.Message); }
    try { await server.DisposeAsync(); } catch { }
}

ClearRunningUrl();
Log.Shutdown();

Console.WriteLine("  остановлено.");
return 0;

// ==================================================================== helpers

static PosixSignalRegistration RegisterSignal(PosixSignal signal, CancellationTokenSource cts)
{
    try
    {
        return PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            try { cts.Cancel(); } catch { }
        });
    }
    catch
    {
        // Windows (for the self-test build) has no POSIX signals.
        return null;
    }
}

/// <summary>
/// The server config for the embedded case. Paths follow XDG so backups and logs
/// land where a Linux user expects, not inside the install directory.
/// </summary>
static KroshkaConfig BuildServerConfig(LinuxClientConfig client)
{
    KroshkaConfig cfg = KroshkaConfig.Load(LinuxPaths.ServerRoot);

    // Absolute paths win over BaseDir in KroshkaConfig's Combine, which is what
    // lets the web assets live next to the binary while data lives in XDG.
    cfg.WebRoot = Path.Combine(AppContext.BaseDirectory, "web");
    cfg.DataDir = LinuxPaths.ServerDataDir;
    cfg.BlobDir = LinuxPaths.ServerBlobDir;
    cfg.BackupDir = LinuxPaths.ServerBackupDir;
    cfg.LogDir = LinuxPaths.LogDir;

    cfg.Port = client.LocalPort;
    cfg.Discoverable = client.Discoverable;

    return cfg;
}

static string ProfileDir()
{
    string dir = Path.Combine(LinuxPaths.DataDir, "browser-profile");
    try { Directory.CreateDirectory(dir); } catch { }
    return dir;
}

static string NormalizeUrl(string input)
{
    string value = (input ?? "").Trim();

    if (!value.Contains("://", StringComparison.Ordinal))
        value = "http://" + value;

    // A bare host with no port is the default port, matching the Windows client
    // and the in-page parser so every entry point agrees.
    if (Uri.TryCreate(value, UriKind.Absolute, out Uri uri))
    {
        int port = uri.IsDefaultPort ? 6767 : uri.Port;
        return $"{uri.Scheme}://{uri.Host}:{port}";
    }

    return value;
}

static Uri UriOf(string url)
{
    return Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ? uri : new Uri("http://127.0.0.1:6767/");
}

/// <summary>
/// Best LAN address for this machine, so the console can print something a phone
/// can type. Skipped when the server is bound to loopback only.
/// </summary>
static string LanAddress(int port)
{
    try
    {
        foreach (System.Net.NetworkInformation.NetworkInterface nic in
                 System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

            foreach (System.Net.NetworkInformation.UnicastIPAddressInformation address in
                     nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;

                string ip = address.Address.ToString();
                if (ip.StartsWith("169.254.")) continue;

                return $"http://{ip}:{port}/";
            }
        }
    }
    catch { }

    return null;
}

static async Task<bool> PingAsync(string baseUrl)
{
    try
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(4) };
        using HttpResponseMessage response = await client.GetAsync(baseUrl.TrimEnd('/') + "/api/ping");
        return response.IsSuccessStatusCode;
    }
    catch
    {
        return false;
    }
}

static string ReadRunningUrl()
{
    try
    {
        if (!File.Exists(LinuxPaths.UrlFile)) return null;

        string url = File.ReadAllText(LinuxPaths.UrlFile).Trim();
        if (string.IsNullOrWhiteSpace(url)) return null;

        // The file outlives a crash; only trust it if the port still answers.
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(1) };
        HttpResponseMessage response = client.GetAsync(url.TrimEnd('/') + "/api/ping")
            .GetAwaiter().GetResult();

        return response.IsSuccessStatusCode ? url : null;
    }
    catch
    {
        return null;
    }
}

static void WriteRunningUrl(string url)
{
    try { File.WriteAllText(LinuxPaths.UrlFile, url, new UTF8Encoding(false)); } catch { }
}

static void ClearRunningUrl()
{
    try { if (File.Exists(LinuxPaths.UrlFile)) File.Delete(LinuxPaths.UrlFile); } catch { }
}

static int RunIntegration(Options options)
{
    string exe = Environment.ProcessPath ?? "kroshka";

    if (options.Uninstall)
    {
        bool removed = DesktopIntegration.Uninstall();
        Console.WriteLine(removed ? "Ярлыки удалены." : "Нечего удалять.");
        return 0;
    }

    if (options.InstallDesktop)
    {
        string desktop = DesktopIntegration.InstallDesktopFile(exe);
        Console.WriteLine("Ярлык: " + desktop);

        string iconSource = Path.Combine(AppContext.BaseDirectory, "icons");
        int icons = DesktopIntegration.InstallIcons(iconSource);
        if (icons > 0) Console.WriteLine($"Иконок установлено: {icons}");
    }

    if (options.InstallService)
    {
        LinuxClientConfig cfg = LinuxClientConfig.Load();
        int port = options.Port ?? cfg.LocalPort;

        string service = DesktopIntegration.InstallService(exe, port);
        Console.WriteLine("Служба: " + service);
        Console.WriteLine();
        Console.WriteLine("  Включить и запустить:");
        Console.WriteLine("    systemctl --user enable --now kroshka");
        Console.WriteLine();
        Console.WriteLine("  Логи:");
        Console.WriteLine("    journalctl --user -u kroshka -f");
    }

    return 0;
}

static void PrintHelp()
{
    Console.WriteLine($"Kroshka {Protocol.Version} — семейный мессенджер (Linux)");
    Console.WriteLine();
    Console.WriteLine("  kroshka                        запустить сервер и открыть окно");
    Console.WriteLine("  kroshka --remote <адрес>       подключиться к серверу в сети");
    Console.WriteLine("  kroshka --headless             только сервер, без окна");
    Console.WriteLine("  kroshka --port <n>             порт локального сервера");
    Console.WriteLine();
    Console.WriteLine("  kroshka --install-desktop      ярлык в меню приложений");
    Console.WriteLine("  kroshka --install-service      служба systemd --user");
    Console.WriteLine("  kroshka --uninstall            убрать ярлыки");
    Console.WriteLine();
    Console.WriteLine("  kroshka --selftest             внутренние проверки");
    Console.WriteLine("  kroshka --version              версия");
    Console.WriteLine("  kroshka --help                 эта справка");
    Console.WriteLine();
    Console.WriteLine("Настройки: " + LinuxPaths.ClientConfigFile);
    Console.WriteLine("Данные:    " + LinuxPaths.DataDir);
    Console.WriteLine("Логи:      " + LinuxPaths.LogDir);
}

// ===================================================================== types

/// <summary>Parsed command line. Kept tiny and allocation-free of surprises.</summary>
internal sealed class Options
{
    public string Mode;
    public string ServerUrl;
    public int? Port;
    public bool Headless;
    public bool OpenBrowser = true;
    public bool ShowHelp;
    public bool ShowVersion;
    public bool SelfTest;
    public bool InstallDesktop;
    public bool InstallService;
    public bool Uninstall;

    public static Options Parse(string[] args)
    {
        Options options = new();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    break;

                case "-v":
                case "--version":
                    options.ShowVersion = true;
                    break;

                case "--local":
                    options.Mode = "local";
                    break;

                case "--remote":
                    if (i + 1 >= args.Length) throw new ArgumentException("--remote требует адрес");
                    options.ServerUrl = args[++i];
                    options.Mode = "remote";
                    break;

                case "--port":
                    if (i + 1 >= args.Length) throw new ArgumentException("--port требует число");
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) ||
                        port is < 1 or > 65535)
                        throw new ArgumentException("порт должен быть от 1 до 65535");
                    options.Port = port;
                    break;

                case "--headless":
                    options.Headless = true;
                    options.OpenBrowser = false;
                    break;

                case "--no-browser":
                    options.OpenBrowser = false;
                    break;

                case "--selftest":
                    options.SelfTest = true;
                    break;

                case "--install-desktop":
                    options.InstallDesktop = true;
                    break;

                case "--install-service":
                    options.InstallService = true;
                    break;

                case "--uninstall":
                    options.Uninstall = true;
                    break;

                default:
                    throw new ArgumentException("неизвестный параметр: " + arg);
            }
        }

        return options;
    }
}
