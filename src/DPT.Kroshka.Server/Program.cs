using System.Globalization;
using System.Text;
using DPT.Kroshka.Server;
using DPT.Kroshka.Server.Storage;
using DPT.Kroshka.Shared;

Console.OutputEncoding = Encoding.UTF8;
Console.Title = "Kroshka Server";

string baseDir = AppContext.BaseDirectory;
string configPath = null;
bool noConsole = false;

// --------------------------------------------------------------- argument scan
for (int i = 0; i < args.Length; i++)
{
    string a = args[i];

    switch (a.ToLowerInvariant())
    {
        case "--help":
        case "-h":
        case "/?":
            PrintHelp();
            return 0;

        case "--version":
            Console.WriteLine($"Kroshka Server {Protocol.Version} (.NET {Environment.Version})");
            return 0;

        case "--config" when i + 1 < args.Length:
            configPath = args[++i];
            break;

        case "--data" when i + 1 < args.Length:
            baseDir = Path.GetFullPath(args[++i]);
            break;

        case "--no-console":
            noConsole = true;
            break;

        case "--selftest":
            return SelfTest.Run();
    }
}

if (configPath != null)
{
    try { baseDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!; }
    catch { }
}

// ------------------------------------------------------------- config + logging
KroshkaConfig cfg = KroshkaConfig.Load(baseDir, configPath);

// Command line beats the file for the values an operator changes most often.
for (int i = 0; i < args.Length; i++)
{
    string a = args[i].ToLowerInvariant();

    if (a == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int port) && port is > 0 and <= 65535)
    {
        cfg.Port = port;
        i++;
    }
    else if (a == "--admin-port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int adminPort) && adminPort is > 0 and <= 65535)
    {
        cfg.AdminPort = adminPort;
        i++;
    }
    else if (a == "--bind" && i + 1 < args.Length)
    {
        cfg.Bind = args[i + 1];
        i++;
    }
    else if (a == "--no-admin")
    {
        cfg.AdminEnabled = false;
    }
    else if (a == "--verbose")
    {
        cfg.LogLevel = DPT.Kroshka.Shared.LogLevel.Debug;
    }
    else if (a == "--backup")
    {
        Log.Configure(cfg);
        KroshkaStore store = new(cfg);
        store.Load();
        Directory.CreateDirectory(cfg.BackupDirPath);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string target = Path.Combine(cfg.BackupDirPath, stamp);
        FileStore.CopyDirectory(cfg.DataDirPath, target);
        Console.WriteLine("backup: " + target);
        Log.Shutdown();
        return 0;
    }
    else if (a == "--reset-admin" && i + 1 < args.Length)
    {
        Log.Configure(cfg);
        string nick = args[++i];
        KroshkaStore store = new(cfg);
        store.Load();
        User user = store.FindByNick(nick);
        if (user == null)
        {
            Console.WriteLine("Пользователь не найден: " + nick);
            Log.Shutdown();
            return 1;
        }
        store.UpdateUser(user.Id, u => { u.Role = UserRole.Owner; u.Banned = false; });
        Console.WriteLine($"{user.Nick} теперь владелец.");
        Log.Shutdown();
        return 0;
    }
}

Log.Configure(cfg);
Log.Info("APP", $"base={cfg.BaseDir} config={(string.IsNullOrEmpty(cfg.ConfigPath) ? "(defaults)" : cfg.ConfigPath)}");

// --------------------------------------------------------- crash containment
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    try
    {
        Exception ex = e.ExceptionObject as Exception;
        Log.Error("CRASH", ex ?? new Exception("unknown unhandled exception"));
        WriteCrashLog(cfg, ex);
    }
    catch { }
};

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    try
    {
        Log.Error("CRASH", "unobserved task exception: " + e.Exception);
        e.SetObserved();
    }
    catch { }
};

App app = new(cfg);

if (!await app.StartAsync())
{
    Log.Error("APP", "не удалось запустить сервер, смотрите логи в " + cfg.LogDirPath);
    Log.Shutdown();
    return 1;
}

using CancellationTokenSource shutdown = new();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    try { shutdown.Cancel(); } catch { }
};

ConsoleUi ui = new(app, () => shutdown.Cancel(), !noConsole);
ui.Start();

try
{
    await Task.Delay(Timeout.Infinite, shutdown.Token);
}
catch (OperationCanceledException) { }

ui.Stop();

try { await app.StopAsync(); }
catch (Exception ex) { Log.Warn("APP", "shutdown: " + ex.Message); }

Log.Info("APP", "остановлено");
Log.Shutdown();
return 0;

// ------------------------------------------------------------------ helpers

static void WriteCrashLog(KroshkaConfig cfg, Exception ex)
{
    try
    {
        string dir = cfg.LogDirPath;
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
        File.WriteAllText(file,
            "=== CRASH " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " ===\n" +
            (ex?.ToString() ?? "unknown"), new UTF8Encoding(false));

        try
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FATAL: " + (ex?.Message ?? "unknown"));
            Console.WriteLine("crash log: " + file);
            Console.ResetColor();
        }
        catch { }
    }
    catch { }
}

static void PrintHelp()
{
    Console.WriteLine($"Kroshka Server {Protocol.Version} - семейный мессенджер для локальной сети");
    Console.WriteLine();
    Console.WriteLine("  --port <n>            порт сервера (по умолчанию 6767)");
    Console.WriteLine("  --admin-port <n>      порт админки (по умолчанию 6768)");
    Console.WriteLine("  --bind <ip>           адрес прослушивания (0.0.0.0 = вся сеть)");
    Console.WriteLine("  --no-admin            не поднимать админку");
    Console.WriteLine("  --config <path>       другой config.ini (его папка становится базовой)");
    Console.WriteLine("  --data <path>         базовая папка для config/data/logs/web");
    Console.WriteLine("  --backup              сделать резервную копию и выйти");
    Console.WriteLine("  --reset-admin <ник>   сделать пользователя владельцем (сервер должен быть остановлен)");
    Console.WriteLine("  --selftest            прогнать внутренние тесты хранилища и выйти");
    Console.WriteLine("  --verbose             уровень логирования debug");
    Console.WriteLine("  --no-console          без интерактивной консоли (служба, CI)");
    Console.WriteLine("  --version             версия");
    Console.WriteLine("  --help                эта справка");
    Console.WriteLine();
    Console.WriteLine("Команды в консоли: help, status, users, rooms, backup, logs, quit");
}
