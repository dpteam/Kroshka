using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DPT.Kroshka.Client;

/// <summary>
/// The window is a thin shell around WebView2: the entire UI is the same HTML the
/// browser and the future Android client use. That is deliberate - one interface,
/// one set of bugs to fix.
///
/// A small JS bridge (window.desktopBridge) lets the page ask the shell for things a
/// web page cannot do: unread badge, flashing, native notifications.
/// </summary>
public sealed class MainForm : Form
{
    private readonly WebView2 _web;
    private readonly Action<string> _onStatus;
    private bool _ready;
    private bool _pageListening;
    private string _startUrl = "";
    private string _pendingRoute = "";
    private string _pendingConfigJson;

    public event EventHandler HiddenToTray;
    public event EventHandler ReallyClosing;

    public MainForm(Action<string> onStatus)
    {
        _onStatus = onStatus;

        Text = "Kroshka";
        MinimumSize = new Size(720, 520);
        Size = new Size(1180, 780);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(10, 15, 26);
        Icon = LoadIcon();
        DoubleBuffered = true;

        _web = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(10, 15, 26)
        };

        Controls.Add(_web);
    }

    private static Icon LoadIcon()
    {
        return IconLoader.Get();
    }

    // ------------------------------------------------------------------ startup

    /// <summary>
    /// Builds the Chromium arguments this app needs.
    ///
    /// The important one: getUserMedia (voice messages) is only available in a
    /// "secure context". Chromium treats https:// and http://localhost that way,
    /// but http://192.168.1.250 is not - so on a LAN the microphone would silently
    /// disappear even though the permission handler says yes.
    ///
    /// --unsafely-treat-insecure-origin-as-secure whitelists exactly the origins we
    /// actually talk to. Nothing else is relaxed: the flag is per-origin, not global.
    /// </summary>
    private static CoreWebView2EnvironmentOptions BuildEnvironmentOptions(string url)
    {
        List<string> origins = new();

        void Add(string candidate)
        {
            string origin = OriginOf(candidate);
            if (origin != null && !origins.Contains(origin)) origins.Add(origin);
        }

        Add(url);

        // The embedded server always lives on loopback; include it so switching
        // between "this PC" and a remote server does not need a restart.
        ClientConfig cfg = ClientConfig.Load();
        Add($"http://127.0.0.1:{cfg.LocalPort}");
        Add($"http://localhost:{cfg.LocalPort}");

        if (!string.IsNullOrWhiteSpace(cfg.ServerUrl)) Add(cfg.ServerUrl);

        List<string> args = new()
        {
            // Media capture must not be blocked by the autoplay/permission heuristics.
            "--autoplay-policy=no-user-gesture-required",
            "--enable-features=WebRTCPipeWireCapturer"
        };

        if (origins.Count > 0)
        {
            args.Add("--unsafely-treat-insecure-origin-as-secure=" + string.Join(",", origins));
            DPT.Kroshka.Shared.Log.Info("WEBVIEW", "trusted (secure-context) origins: " + string.Join(", ", origins));
        }

        return new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = string.Join(" ", args),
            // Keeps the WebView from phoning home for language packs or crash data.
            Language = "ru-RU",
            AllowSingleSignOnUsingOSPrimaryAccount = false
        };
    }

    /// <summary>Reduces any URL to scheme://host:port, or null when it is not http(s).</summary>
    private static string OriginOf(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        try
        {
            if (!value.Contains("://", StringComparison.Ordinal))
                value = "http://" + value;

            Uri uri = new(value);
            if (uri.Scheme is not ("http" or "https")) return null;

            int port = uri.IsDefaultPort
                ? (uri.Scheme == "https" ? 443 : 80)
                : uri.Port;

            return $"{uri.Scheme}://{uri.Host}:{port}";
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> InitialiseAsync(string url)
    {
        try
        {
            _startUrl = url;
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: ClientConfig.Load().EffectiveWebViewDir,
                options: BuildEnvironmentOptions(url));

            Environment = environment;

            await _web.EnsureCoreWebView2Async(environment);

            CoreWebView2 core = _web.CoreWebView2;

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
            core.Settings.IsSwipeNavigationEnabled = false;

            // WebView2 denies every permission by default, and a denial is silent
            // from the page's point of view: getUserMedia rejects and the UI can
            // only say "microphone unavailable". Grant the two this app actually
            // uses, and only for the origin we are talking to.
            core.PermissionRequested += OnPermissionRequested;

            // Links to the outside world open in the real browser, not inside the app.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };

            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;

                e.Cancel = true;
                OpenExternal(e.Uri);
            };

            core.DocumentTitleChanged += (_, _) =>
            {
                string title = core.DocumentTitle;
                if (!string.IsNullOrWhiteSpace(title) && Text != title) Text = title;
            };

            core.WebMessageReceived += OnWebMessage;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);

            core.NavigationCompleted += (_, e) =>
            {
                _ready = e.IsSuccess;
                _onStatus(e.IsSuccess ? "" : "Не удалось загрузить интерфейс");
                if (!e.IsSuccess) ShowLoadError(e.WebErrorStatus.ToString());

                // The page renders from its route, so nothing to replay here.
            };

            _web.Source = new Uri(WithRoute(url, _pendingRoute));
            return true;
        }
        catch (Exception ex)
        {
            ShowLoadError(ex.Message);
            return false;
        }
    }

    public void Navigate(string url)
    {
        try
        {
            if (_web.CoreWebView2 != null) _web.CoreWebView2.Navigate(url);
            else _web.Source = new Uri(url);
        }
        catch (Exception ex)
        {
            _onStatus("Навигация не удалась: " + ex.Message);
        }
    }

    /// <summary>
    /// Grants microphone access for the chat origin. Everything else stays denied,
    /// because a messenger has no business reading the camera or the clipboard
    /// without a deliberate feature behind it.
    ///
    /// The permission is per-origin and survives navigation within that origin, so
    /// recording keeps working after switching rooms or reconnecting.
    /// </summary>
    private void OnPermissionRequested(object sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        try
        {
            switch (e.PermissionKind)
            {
                case CoreWebView2PermissionKind.Microphone:
                    // Only for our own server: a page navigated elsewhere in the
                    // same window must not inherit the grant.
                    if (IsTrustedOrigin(e.Uri))
                    {
                        e.State = CoreWebView2PermissionState.Allow;
                        DPT.Kroshka.Shared.Log.Info("WEBVIEW", "microphone granted for " + SafeOrigin(e.Uri));
                    }
                    else
                    {
                        e.State = CoreWebView2PermissionState.Deny;
                        DPT.Kroshka.Shared.Log.Warn("WEBVIEW", "microphone denied for foreign origin " + SafeOrigin(e.Uri));
                    }
                    break;

                case CoreWebView2PermissionKind.ClipboardRead:
                    e.State = CoreWebView2PermissionState.Allow;
                    break;

                default:
                    e.State = CoreWebView2PermissionState.Deny;
                    break;
            }
        }
        catch (Exception ex)
        {
            DPT.Kroshka.Shared.Log.Warn("WEBVIEW", "permission handler: " + ex.Message);
        }
    }

    /// <summary>Loopback and private LAN ranges are the only places this client goes.</summary>
    private static bool IsTrustedOrigin(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return false;

        try
        {
            Uri parsed = new(uri);
            if (parsed.Scheme is not ("http" or "https")) return false;

            System.Net.IPAddress address;
            string host = parsed.Host;

            if (host is "localhost") return true;
            if (!System.Net.IPAddress.TryParse(host, out address)) return false;

            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (System.Net.IPAddress.IsLoopback(address)) return true;

            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length != 4) return true;     // IPv6 LAN: allow, it is not public DNS

            // 10/8, 172.16/12, 192.168/16, 169.254/16
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeOrigin(string uri)
    {
        try { return new Uri(uri).GetLeftPart(UriPartial.Authority); }
        catch { return uri ?? ""; }
    }

    private void ShowLoadError(string detail)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ShowLoadError(detail)));
            return;
        }

        using Form dialog = new()
        {
            Text = "Kroshka",
            Size = new Size(520, 300),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.White
        };

        Label label = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            Text = "Не удалось открыть интерфейс.\r\n\r\n" +
                   "Проверьте, что сервер запущен, и адрес в настройках.\r\n\r\n" + detail,
            Font = new Font("Segoe UI", 10f),
            ForeColor = Color.FromArgb(11, 22, 34)
        };

        Button close = new()
        {
            Text = "Закрыть",
            Dock = DockStyle.Bottom,
            Height = 44,
            FlatStyle = FlatStyle.Flat
        };
        close.Click += (_, _) => dialog.Close();

        dialog.Controls.Add(label);
        dialog.Controls.Add(close);
        dialog.ShowDialog(this);
    }

    // ------------------------------------------------------------- js bridge

    private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try { json = e.WebMessageAsJson; }
        catch { return; }

        // The page talks to us through window.chrome.webview.postMessage({cmd:...}).
        // We parse defensively: a malformed frame must never crash the shell.
        string cmd = ExtractString(json, "cmd");
        if (cmd == null) return;

        switch (cmd)
        {
            case "badge":
                int count = ExtractInt(json, "count");
                SetBadge(count);
                break;

            case "flash":
                FlashTaskbar();
                break;

            case "notify":
                string title = ExtractString(json, "title") ?? "Kroshka";
                string body = ExtractString(json, "body") ?? "";
                Notify(title, body);
                break;

            case "openExternal":
                string url = ExtractString(json, "url");
                if (!string.IsNullOrEmpty(url)) OpenExternal(url);
                break;

            case "setServer":
                // The page chose a server; remember it so the next launch connects
                // straight there instead of starting a local one first.
                string server = ExtractString(json, "url");
                if (!string.IsNullOrEmpty(server))
                {
                    ServerChosen?.Invoke(this, server);
                }
                break;

            case "needSettings":
                // The page entered the settings route and wants the current values.
                // Asking from the page removes every timing question: the answer
                // is only ever sent to a listener that already exists.
                PostConfig();
                break;

            case "scanLan":
                // The page asked for nearby servers. Scan in the background and
                // push the result back; a slow network must not freeze the UI.
                _ = ScanLanAsync();
                break;

            case "log":
                DPT.Kroshka.Shared.Log.Info("PAGE", ExtractString(json, "text") ?? "");
                break;

            case "uiReady":
                // The page's listener is live. Flush a settings request that arrived
                // before the document existed; the page cannot be told earlier.
                _pageListening = true;
                DPT.Kroshka.Shared.Log.Info("WEBVIEW", $"page ready, pending route='{_pendingRoute}'");
                if (!string.IsNullOrEmpty(_pendingRoute)) FlushRoute();
                break;

            case "saveSettings":
                // The page validated the values; the shell only persists them.
                SettingsSaved?.Invoke(this, ExtractObject(json, "config"));
                break;
        }
    }

    /// <summary>Raised when the page settles on a remote server URL.</summary>
    public event EventHandler<string> ServerChosen;

    /// <summary>Raised with the raw JSON of the settings the page wants saved.</summary>
    public event EventHandler<string> SettingsSaved;

    /// <summary>
    /// Opens the settings screen. It is a route (#settings), not a second window
    /// and not an overlay, so "open" is literally a navigation: no handshake with
    /// the page, no race with its startup, and Back/Escape work for free.
    /// </summary>
    public void ShowSettingsPage(string configJson)
    {
        _pendingConfigJson = configJson;
        _pendingRoute = "settings";

        FlushRoute();
    }

    /// <summary>
    /// Applies the pending route to the live page.
    ///
    /// Nothing happens until the page has announced itself (uiReady): an
    /// ExecuteScript against a document that is still loading is silently
    /// dropped, which is why the settings screen kept failing to appear.
    /// </summary>
    private void FlushRoute()
    {
        if (string.IsNullOrEmpty(_pendingRoute) || _web.CoreWebView2 == null) return;
        if (!_pageListening)
        {
            DPT.Kroshka.Shared.Log.Info("WEBVIEW", "route deferred until page is ready");
            return;
        }

        string script = "location.hash = " + JsonSerializer.Serialize(_pendingRoute);

        _web.CoreWebView2
            .ExecuteScriptAsync(script)
            .ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    DPT.Kroshka.Shared.Log.Warn("WEBVIEW", "route script failed: " + task.Exception?.GetBaseException().Message);
                    return;
                }

                DPT.Kroshka.Shared.Log.Info("WEBVIEW", "route applied: " + _pendingRoute);
                _pendingRoute = "";
            }, TaskScheduler.Default);
    }

    /// <summary>Appends (or replaces) the route fragment on a base URL.</summary>
    private static string WithRoute(string url, string route)
    {
        if (string.IsNullOrEmpty(url)) return url;

        string bare = url.Split('#')[0];
        return string.IsNullOrEmpty(route) ? bare : bare + "#" + route;
    }

    /// <summary>Sends the current settings values to the page on request.</summary>
    private void PostConfig()
    {
        if (_web.CoreWebView2 == null) return;

        string payload = "{\"type\":\"openSettings\",\"config\":" + (_pendingConfigJson ?? "{}") + "}";

        try
        {
            _web.CoreWebView2.PostWebMessageAsJson(payload);
            DPT.Kroshka.Shared.Log.Info("WEBVIEW", "settings config sent");
        }
        catch (Exception ex)
        {
            DPT.Kroshka.Shared.Log.Warn("WEBVIEW", "cannot send settings config: " + ex.Message);
        }
    }

    /// <summary>
    /// Broadcasts a discovery probe and hands the results to the page.
    /// Runs off the UI thread because a scan waits for network replies.
    /// </summary>
    private async Task ScanLanAsync()
    {
        int port = ClientConfig.Load().DiscoveryPort;

        List<FoundServer> servers;
        try
        {
            servers = await LanDiscovery.ScanAsync(TimeSpan.FromSeconds(2), port);
        }
        catch (Exception ex)
        {
            DPT.Kroshka.Shared.Log.Warn("DISCOVERY", "scan failed: " + ex.Message);
            servers = new List<FoundServer>();
        }

        DPT.Kroshka.Shared.Log.Info("DISCOVERY", $"found {servers.Count} server(s)");

        if (_web.CoreWebView2 == null) return;

        object payload = new
        {
            type = "lanServers",
            servers = servers.Select(s => new
            {
                name = s.Name,
                url = s.Url,
                host = s.Host,
                port = s.Port,
                version = s.Version,
                authMode = s.AuthMode
            }).ToList()
        };

        try
        {
            _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }
        catch (Exception ex)
        {
            DPT.Kroshka.Shared.Log.Warn("DISCOVERY", "cannot send results: " + ex.Message);
        }
    }

    /// <summary>Extracts a nested object as raw JSON text.</summary>
    private static string ExtractObject(string json, string key)
    {
        string needle = "\"" + key + "\"";
        int at = json.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) return null;

        int colon = json.IndexOf(':', at + needle.Length);
        if (colon < 0) return null;

        int start = colon + 1;
        while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
        if (start >= json.Length || json[start] != '{') return null;

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = start; i < json.Length; i++)
        {
            char c = json[i];

            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inString = false;
                continue;
            }

            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return json[start..(i + 1)];
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a top-level JSON string without a dependency. The bridge payloads are
    /// tiny and flat, so a full parser would be overkill.
    /// </summary>
    private static string ExtractString(string json, string key)
    {
        string needle = "\"" + key + "\"";
        int at = json.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) return null;

        int colon = json.IndexOf(':', at + needle.Length);
        if (colon < 0) return null;

        int start = json.IndexOf('"', colon + 1);
        if (start < 0) return null;

        int i = start + 1;
        System.Text.StringBuilder sb = new();
        while (i < json.Length)
        {
            char c = json[i];
            if (c == '\\' && i + 1 < json.Length)
            {
                char next = json[i + 1];
                sb.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => next
                });
                i += 2;
                continue;
            }
            if (c == '"') break;
            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static int ExtractInt(string json, string key)
    {
        string needle = "\"" + key + "\"";
        int at = json.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) return 0;

        int colon = json.IndexOf(':', at + needle.Length);
        if (colon < 0) return 0;

        int i = colon + 1;
        while (i < json.Length && !char.IsDigit(json[i]) && json[i] != '-') i++;

        int start = i;
        while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '-')) i++;

        return int.TryParse(json[start..i], out int value) ? value : 0;
    }

    private void SetBadge(int count)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetBadge(count)));
            return;
        }

        string name = count > 0 ? $"Kroshka — {count} новых" : "Kroshka";
        Text = name;
        TrayHost?.SetBadge(count);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.ComponentModel.Browsable(false)]
    public TrayHost TrayHost { get; set; }

    /// <summary>
    /// The WebView2 environment created at startup. Exposed so secondary windows
    /// (the settings dialog) reuse it instead of creating a second one, which
    /// WebView2 does not support while the first is live.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.ComponentModel.Browsable(false)]
    public CoreWebView2Environment Environment { get; private set; }

    private void FlashTaskbar()
    {
        if (!Focused) Native.FlashWindow(Handle, true);
    }

    private void Notify(string title, string body)
    {
        if (!ClientConfig.Load().Notifications) return;
        TrayHost?.ShowBalloon(title, body);
    }

    private static void OpenExternal(string url)
    {
        try
        {
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ closing

    /// <summary>Set when the user really wants the process gone, not hidden.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.ComponentModel.Browsable(false)]
    public bool ForceClose { get; set; }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ForceClose && e.CloseReason == CloseReason.UserClosing && ClientConfig.Load().CloseToTray)
        {
            e.Cancel = true;
            HiddenToTray?.Invoke(this, EventArgs.Empty);
            return;
        }

        ReallyClosing?.Invoke(this, EventArgs.Empty);
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _web.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}

/// <summary>Thin P/Invoke layer: only what the shell actually needs.</summary>
internal static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12;

    public static void FlashWindow(IntPtr handle, bool continuous)
    {
        try
        {
            FLASHWINFO info = new()
            {
                hwnd = handle,
                dwFlags = continuous ? FLASHW_ALL | FLASHW_TIMERNOFG : FLASHW_ALL,
                uCount = continuous ? uint.MaxValue : 3,
                dwTimeout = 0
            };
            info.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(info);
            FlashWindowEx(ref info);
        }
        catch { }
    }
}
