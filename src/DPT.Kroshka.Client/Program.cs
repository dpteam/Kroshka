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
    private const string SettingsEventName = @"Local\Kroshka.Client.Settings";

    [STAThread]
    private static int Main(string[] args)
    {
        bool minimized = args.Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-m", StringComparison.OrdinalIgnoreCase));

        // Opens the settings dialog immediately. Handy for a shortcut, and the
        // only way to reach the dialog on a machine where the tray is hidden.
        bool openSettings = args.Any(a =>
            a.Equals("--settings", StringComparison.OrdinalIgnoreCase));

        using Mutex mutex = new(true, MutexName, out bool isFirst);

        if (!isFirst)
        {
            SignalExistingInstance(openSettings);
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
        using EventWaitHandle settingsEvent = new(false, EventResetMode.AutoReset, SettingsEventName);
        KroshkaContext context = new(minimized, openSettings);

        // A second launch raises one of these; the running instance reacts. Two
        // events instead of one plus a payload keeps the inter-process contract
        // to a single bit each, which is all this needs.
        Thread listener = new(() =>
        {
            while (true)
            {
                int signaled = WaitHandle.WaitAny(new WaitHandle[] { showEvent, settingsEvent });

                try
                {
                    if (signaled == 1) context.RequestSettings();
                    else context.RequestShow();
                }
                catch { }
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
    private static void SignalExistingInstance(bool settings)
    {
        string name = settings ? SettingsEventName : ShowEventName;

        try
        {
            if (EventWaitHandle.TryOpenExisting(name, out EventWaitHandle handle))
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
