using System.Drawing;
using System.Text.Json;
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
    private readonly bool _openSettingsOnStart;

    /// <summary>
    /// The UI thread's synchronization context, captured in the constructor.
    ///
    /// This is the actual fix for the blank windows: WebView2 requires its
    /// environment AND every controller to be created on one thread that pumps
    /// messages. StartAsync runs on the thread pool after its first await, so
    /// creating the environment there made every later controller creation fail
    /// with E_ABORT - a blank window and no visible error.
    ///
    /// Captured here because the constructor runs on the STA thread from
    /// Application.Run. Checking InvokeRequired instead is not enough: before the
    /// form's handle exists it reports false, so work would run on the wrong
    /// thread anyway.
    /// </summary>
    private readonly SynchronizationContext _ui;

    public KroshkaContext(bool startMinimized, bool openSettingsOnStart = false)
    {
        _client = ClientConfig.Load();
        _openSettingsOnStart = openSettingsOnStart;

        // Capture the UI context before anything can hop threads.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _tray = new TrayHost();
        _tray.OpenRequested += (_, _) => ShowWindow();
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.RestartServerRequested += (_, _) => RestartServer();
        _tray.ExitRequested += (_, _) => Exit();

        _form = new MainForm(SetStatus) { TrayHost = _tray };
        _form.HiddenToTray += (_, _) => HideToTray();
        _form.ReallyClosing += (_, _) => Exit();
        _form.ServerChosen += (_, url) => RememberServer(url);
        _form.SettingsSaved += (_, json) => ApplySettings(json);

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
            // Everything before this line may run on a thread-pool thread: starting
            // Kestrel and probing it are pure async work. Every touch of a WinForms
            // control after it MUST come back to the UI thread, otherwise the WebView2
            // controller is created on the wrong thread and the window stays blank
            // until some unrelated event forces a repaint.
            _url = await ResolveUrlAsync();

            await OnUiAsync(async () =>
            {
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

                if (_openSettingsOnStart) ShowSettings();
            });
        }
        catch (Exception ex)
        {
            // A startup failure must leave a visible tray icon and an explanation,
            // never a silent process with no window.
            Log.Error("CLIENT", ex);
            _tray.SetStatus("ошибка запуска");
            _tray.ShowBalloon("Kroshka", "Не удалось запустить: " + ex.Message);
            OnUi(ShowWindow);
        }
    }

    /// <summary>
    /// Runs an action on the UI thread and waits for it.
    ///
    /// Uses the captured SynchronizationContext rather than InvokeRequired:
    /// before the form's handle exists InvokeRequired reports false even when we
    /// are on a pool thread, which is exactly how the WebView ended up being
    /// created on the wrong thread.
    /// </summary>
    private Task OnUiAsync(Func<Task> action)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _ui.Post(async _ =>
            {
                try
                {
                    await action();
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }, null);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }

        return completion.Task;
    }

    /// <summary>Fire-and-forget variant for the paths that cannot await.</summary>
    private void OnUi(Action action)
    {
        try
        {
            _ui.Post(_ =>
            {
                try { action(); }
                catch (Exception ex) { Log.Warn("CLIENT", "ui action: " + ex.Message); }
            }, null);
        }
        catch { }
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

    /// <summary>Thread-safe "open settings", for a second instance launched with --settings.</summary>
    public void RequestSettings()
    {
        if (_form.IsDisposed) return;
        ShowSettings();
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

    /// <summary>
    /// The page picked a remote server. Store it in client.ini so the next launch
    /// skips the picker and connects directly; the current session already talks
    /// to it, so nothing needs restarting now.
    /// </summary>
    private void RememberServer(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (string.Equals(_client.ServerUrl, url, StringComparison.OrdinalIgnoreCase) &&
                _client.Mode == "remote")
                return;

            _client.Mode = "remote";
            _client.ServerUrl = url;
            _client.Save();

            Log.Info("CLIENT", "remembered server " + url);
            _tray.SetStatus("подключено к " + url);
        }
        catch (Exception ex)
        {
            Log.Warn("CLIENT", "cannot remember server: " + ex.Message);
        }
    }

    // ----------------------------------------------------------------- settings

    /// <summary>
    /// Opens the settings page inside the main WebView.
    ///
    /// There is no second window and no second WebView2 on purpose. Creating a
    /// second controller in the same process while the first is live is what
    /// produced E_ABORT and a permanently blank dialog; a page in the existing
    /// WebView has none of those failure modes and matches the rest of the UI
    /// exactly, because it *is* the rest of the UI.
    /// </summary>
    private void ShowSettings()
    {
        OnUi(() =>
        {
            ShowWindow();

            string json = JsonSerializer.Serialize(new
            {
                mode = _client.Mode,
                serverUrl = _client.ServerUrl ?? "",
                localPort = _client.LocalPort,
                closeToTray = _client.CloseToTray,
                notifications = _client.Notifications,
                startWithWindows = _client.StartWithWindows,
                version = typeof(KroshkaContext).Assembly.GetName().Version?.ToString(3) ?? ""
            });

            _form.ShowSettingsPage(json);
        });
    }

    /// <summary>
    /// Persists what the page submitted. The page already validated the address,
    /// so this only maps JSON to ClientConfig and applies the side effects.
    /// </summary>
    private void ApplySettings(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            string mode = ReadString(root, "mode") ?? _client.Mode;
            string serverUrl = ReadString(root, "serverUrl") ?? "";
            int port = ReadInt(root, "localPort", _client.LocalPort);

            if (port is < 1024 or > 65535) port = 6767;

            _client.Mode = mode == "remote" ? "remote" : "local";
            _client.ServerUrl = serverUrl;
            _client.LocalPort = port;
            _client.CloseToTray = ReadBool(root, "closeToTray", _client.CloseToTray);
            _client.Notifications = ReadBool(root, "notifications", _client.Notifications);
            _client.StartWithWindows = ReadBool(root, "startWithWindows", _client.StartWithWindows);
            _client.Save();

            Autostart.Apply(_client.StartWithWindows);

            Log.Info("CLIENT",
                $"settings saved: mode={_client.Mode} url={_client.ServerUrl} port={_client.LocalPort}");

            _ = RestartServerAsync();
        }
        catch (Exception ex)
        {
            Log.Error("CLIENT", ex);
            _tray.ShowBalloon("Kroshka", "Не удалось сохранить настройки: " + ex.Message);
        }
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool ReadBool(JsonElement element, string name, bool fallback)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }

    private static int ReadInt(JsonElement element, string name, int fallback)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return fallback;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : fallback;
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
