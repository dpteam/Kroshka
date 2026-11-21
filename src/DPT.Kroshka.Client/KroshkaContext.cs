using System.Drawing;
using System.Windows.Forms;
using DPT.Kroshka.Shared;

namespace DPT.Kroshka.Client;

/// <summary>
/// Owns the app lifetime: client settings, the optional embedded server, the window
/// and the tray icon. Everything is wired here so Program.cs stays a stub.
/// </summary>
public sealed class KroshkaContext : ApplicationContext
{
    private readonly ClientConfig _client;
    private readonly TrayHost _tray;
    private readonly MainForm _form;

    private DPT.Kroshka.Server.App _server;
    private Task _serverTask;
    private bool _exiting;
    private string _url;

    public KroshkaContext(bool startMinimized)
    {
        _client = ClientConfig.Load();

        _tray = new TrayHost();
        _tray.OpenRequested += (_, _) => ShowWindow();
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.RestartServerRequested += (_, _) => RestartServer();
        _tray.ExitRequested += (_, _) => Exit();

        _form = new MainForm(SetStatus) { TrayHost = _tray };
        _form.HiddenToTray += (_, _) => HideToTray();
        _form.ReallyClosing += (_, _) => Exit();

        _tray.SetStatus("запуск…");

        // Start the shell, then bring up whatever backend the config asks for.
        // Deliberately fire-and-forget: the constructor cannot be async, and
        // StartAsync contains its own failure handling.
        _ = StartAsync(startMinimized);
    }

    // ------------------------------------------------------------------- startup

    private async Task StartAsync(bool startMinimized)
    {
        try
        {
            _url = await ResolveUrlAsync();

            bool loaded = await _form.InitialiseAsync(_url);

            if (!loaded)
            {
                _tray.SetStatus("ошибка запуска");
            }
            else
            {
                _tray.SetStatus(_client.Mode == "local"
                    ? $"локальный сервер, порт {PortOf(_url)}"
                    : "подключено к " + _url);
            }

            if (startMinimized || _client.StartMinimized) HideToTray();
            else ShowWindow();
        }
        catch (Exception ex)
        {
            // A startup failure must leave a visible tray icon and an explanation,
            // never a silent process with no window.
            Log.Error("CLIENT", ex);
            _tray.SetStatus("ошибка запуска");
            _tray.ShowBalloon("Kroshka", "Не удалось запустить: " + ex.Message);
            ShowWindow();
        }
    }

    /// <summary>
    /// In local mode the client is also the server: it boots Kestrel in-process, so a
    /// family can use the app on one PC without installing anything else. In remote
    /// mode it simply points at a URL.
    /// </summary>
    private async Task<string> ResolveUrlAsync()
    {
        if (_client.Mode == "remote" && !string.IsNullOrWhiteSpace(_client.ServerUrl))
        {
            string url = _client.ServerUrl.Trim().TrimEnd('/');
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "http://" + url;
            return url;
        }

        string local = await StartEmbeddedServerAsync();
        return local;
    }

    private async Task<string> StartEmbeddedServerAsync()
    {
        try
        {
            KroshkaConfig cfg = BuildServerConfig();
            Log.Configure(cfg);

            _server = new DPT.Kroshka.Server.App(cfg);
            _serverTask = Task.Run(async () => await _server.StartAsync());

            // Wait for the listener to bind, then confirm with a health probe.
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(120);
                if (_server.PublicPort > 0) break;
            }

            if (_server.PublicPort <= 0)
            {
                Log.Error("CLIENT", "embedded server did not start");
                return $"http://127.0.0.1:{_client.LocalPort}/";
            }

            string url = $"http://127.0.0.1:{_server.PublicPort}/";
            Log.Info("CLIENT", "embedded server ready at " + url);
            return url;
        }
        catch (Exception ex)
        {
            Log.Error("CLIENT", ex);
            return $"http://127.0.0.1:{_client.LocalPort}/";
        }
    }

    private KroshkaConfig BuildServerConfig()
    {
        string baseDir = AppContext.BaseDirectory;
        KroshkaConfig cfg = KroshkaConfig.Load(baseDir);

        cfg.Port = _client.LocalPort;

        // Data must live in a writable per-user folder: the install directory may be
        // under Program Files where a non-admin cannot write.
        string dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kroshka", "server");

        Directory.CreateDirectory(dataRoot);

        cfg.DataDir = dataRoot;
        cfg.BlobDir = Path.Combine(dataRoot, "blobs");
        cfg.BackupDir = Path.Combine(dataRoot, "backups");
        cfg.LogDir = Path.Combine(dataRoot, "logs");

        return cfg;
    }

    private static string PortOf(string url)
    {
        try { return new Uri(url).Port.ToString(); }
        catch { return "?"; }
    }

    // ------------------------------------------------------------------ window

    private void ShowWindow()
    {
        if (_form.IsDisposed) return;

        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.BringToFront();
    }

    /// <summary>Thread-safe "show yourself" used by the single-instance listener.</summary>
    public void RequestShow()
    {
        if (_form.IsDisposed) return;

        if (_form.InvokeRequired)
        {
            try { _form.BeginInvoke(new Action(ShowWindow)); }
            catch { }
            return;
        }

        ShowWindow();
    }

    private void HideToTray()
    {
        if (_form.IsDisposed) return;
        _form.Hide();
        _tray.SetStatus(_client.Mode == "local"
            ? $"локальный сервер, порт {PortOf(_url)}"
            : "подключено");
    }

    private void SetStatus(string text)
    {
        _tray.SetStatus(string.IsNullOrWhiteSpace(text) ? "работает" : text);
    }

    // ----------------------------------------------------------------- settings

    private void ShowSettings()
    {
        using SettingsForm dialog = new(_client);
        if (dialog.ShowDialog() != DialogResult.OK) return;

        _client.Mode = dialog.Mode;
        _client.ServerUrl = dialog.ServerUrl;
        _client.LocalPort = dialog.LocalPort;
        _client.CloseToTray = dialog.CloseToTray;
        _client.Notifications = dialog.Notifications;
        _client.StartWithWindows = dialog.StartWithWindows;
        _client.Save();

        Autostart.Apply(_client.StartWithWindows);

        toast("Настройки сохранены. Перезапускаю сервер…");
        _ = RestartServerAsync();
    }

    private void RestartServer()
    {
        _ = RestartServerAsync();
    }

    private async Task RestartServerAsync()
    {
        try
        {
            if (_server != null)
            {
                await _server.StopAsync();
                await _server.DisposeAsync();
                _server = null;
            }

            _tray.SetStatus("перезапуск…");
            _url = await ResolveUrlAsync();
            _form.Navigate(_url);
            _tray.SetStatus(_client.Mode == "local" ? $"локальный сервер, порт {PortOf(_url)}" : "подключено");
        }
        catch (Exception ex)
        {
            Log.Error("CLIENT", ex);
            _tray.SetStatus("ошибка перезапуска");
        }
    }

    private void toast(string message)
    {
        _tray.ShowBalloon("Kroshka", message);
    }

    // -------------------------------------------------------------------- exit

    private void Exit()
    {
        if (_exiting) return;
        _exiting = true;

        try
        {
            if (_server != null)
            {
                _server.StopAsync().GetAwaiter().GetResult();
                _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _server = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("CLIENT", "server shutdown: " + ex.Message);
        }

        try { _tray.Dispose(); } catch { }

        if (!_form.IsDisposed)
        {
            _form.ForceClose = true;
            _form.Close();
        }

        Log.Shutdown();
        ExitThread();
    }
}
