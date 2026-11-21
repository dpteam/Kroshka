using System.Globalization;
using DPT.Kroshka.Shared;

namespace DPT.Kroshka.Server;

/// <summary>
/// A small console dashboard: status line, recent log lines and a handful of operator
/// commands. Deliberately not a TUI framework - it must never fight with the log sink
/// for the cursor, so it only redraws on demand.
/// </summary>
public sealed class ConsoleUi
{
    private readonly App _app;
    private readonly Action _requestShutdown;
    private readonly bool _enabled;
    private Thread _input;
    private volatile bool _stopped;

    public ConsoleUi(App app, Action requestShutdown, bool enabled)
    {
        _app = app;
        _requestShutdown = requestShutdown;
        _enabled = enabled && !Console.IsInputRedirected;
    }

    public void Start()
    {
        PrintBanner();

        if (!_enabled) return;

        _input = new Thread(InputLoop) { IsBackground = true, Name = "console-ui" };
        _input.Start();
    }

    public void Stop()
    {
        _stopped = true;
    }

    private void PrintBanner()
    {
        try
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  ██  Kroshka Server " + Protocol.Version);
            Console.ResetColor();
            Console.WriteLine($"  {_app.StatusText()}");
            Console.WriteLine($"  адрес: http://{LocalAddress()}:{_app.PublicPort}/");
            if (_app.AdminPort > 0)
                Console.WriteLine($"  админ: http://{LocalAddress()}:{_app.AdminPort}/admin");
            Console.WriteLine("  команды: help, status, users, rooms, backup, logs, quit");
            Console.WriteLine();
        }
        catch { }
    }

    private static string LocalAddress()
    {
        try
        {
            foreach (System.Net.NetworkInformation.NetworkInterface nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                foreach (System.Net.NetworkInformation.UnicastIPAddressInformation addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    string ip = addr.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;
                    return ip;
                }
            }
        }
        catch { }
        return "localhost";
    }

    private void InputLoop()
    {
        while (!_stopped)
        {
            string line;
            try { line = Console.ReadLine(); }
            catch { return; }

            if (line == null) return;               // stdin closed
            line = line.Trim();
            if (line.Length == 0) continue;

            try { Execute(line); }
            catch (Exception ex) { WriteLine("ошибка: " + ex.Message, ConsoleColor.Red); }
        }
    }

    private void Execute(string command)
    {
        string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = parts[0].ToLowerInvariant();

        switch (verb)
        {
            case "help":
            case "?":
                WriteLine("status           состояние сервера");
                WriteLine("users            список пользователей");
                WriteLine("rooms            список комнат");
                WriteLine("backup           резервная копия данных");
                WriteLine("logs [n]         последние n строк лога");
                WriteLine("quit / exit      остановить сервер");
                break;

            case "status":
                WriteLine(_app.StatusText());
                WriteLine("лог: " + Log.CurrentFile);
                break;

            case "users":
                foreach (User u in _app.Store.AllUsers().OrderBy(u => u.CreatedUtc))
                    WriteLine($"{u.Id}  {u.Nick,-20} {u.Role,-6} {(u.Banned ? "BANNED" : "")}");
                break;

            case "rooms":
                foreach (Room r in _app.Store.AllRooms())
                    WriteLine($"{r.Id}  {r.Name,-24} msg={r.LastSeq} members={r.Members.Count} {(r.IsPrivate ? "private" : "")}");
                break;

            case "backup":
                string path = _app.CreateBackup();
                WriteLine(path == null ? "не удалось" : "готово: " + path);
                break;

            case "logs":
                int count = parts.Length > 1 && int.TryParse(parts[1], out int n) ? n : 20;
                foreach (LogEntry entry in Log.Tail(0, count, null, null))
                    WriteLine(Log.Format(entry));
                break;

            case "quit":
            case "exit":
            case "q":
                _requestShutdown();
                break;

            default:
                WriteLine("неизвестная команда: " + verb + " (help)");
                break;
        }
    }

    private static void WriteLine(string text, ConsoleColor color = ConsoleColor.Gray)
    {
        try
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }
        catch { }
    }
}
