using System.Windows.Forms;
using DPT.Kroshka.Shared;

namespace DPT.Kroshka.Client;

/// <summary>
/// Entry point. A named mutex keeps one instance per user session: a second launch
/// simply raises the window that is already running instead of starting a second
/// server on the same port.
/// </summary>
internal static class Program
{
    private const string MutexName = @"Local\Kroshka.Client.SingleInstance";
    private const string ShowEventName = @"Local\Kroshka.Client.Show";
    private const string EventWaitHandleName = ShowEventName;

    [STAThread]
    private static int Main(string[] args)
    {
        bool minimized = args.Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-m", StringComparison.OrdinalIgnoreCase));

        using Mutex mutex = new(true, MutexName, out bool isFirst);

        if (!isFirst)
        {
            SignalExistingInstance();
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Nothing may escape to the desktop: an unhandled exception in a tray app
        // would otherwise kill the icon with no explanation.
        Application.ThreadException += (_, e) =>
        {
            Log.Error("CLIENT", e.Exception);
            MessageBox.Show(
                "Произошла ошибка:\r\n\r\n" + e.Exception.Message,
                "Kroshka", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("CLIENT", e.ExceptionObject as Exception ?? new Exception("unknown"));
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("CLIENT", "unobserved: " + e.Exception);
            e.SetObserved();
        };

        using EventWaitHandle showEvent = new(false, EventResetMode.AutoReset, EventWaitHandleName);
        KroshkaContext context = new(minimized);

        // A second launch sets the event; this thread raises the existing window.
        Thread listener = new(() =>
        {
            while (true)
            {
                if (!showEvent.WaitOne()) continue;
                try { context.RequestShow(); } catch { }
            }
        })
        {
            IsBackground = true,
            Name = "single-instance-listener"
        };
        listener.Start();

        Application.Run(context);
        return 0;
    }

    /// <summary>
    /// Tells the running instance to show itself. A named event is enough here; there
    /// is no need to marshal data between processes for a single-window app.
    /// </summary>
    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out EventWaitHandle handle))
            {
                handle.Set();
                handle.Dispose();
                return;
            }
        }
        catch { }

        MessageBox.Show("Kroshka уже запущена. Ищите значок в трее.",
            "Kroshka", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
