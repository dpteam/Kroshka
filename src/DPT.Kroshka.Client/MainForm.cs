using System.Drawing;
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
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "assets", "kroshka.ico");
            if (File.Exists(path)) return new Icon(path);
        }
        catch { }
        return SystemIcons.Application;
    }

    // ------------------------------------------------------------------ startup

    public async Task<bool> InitialiseAsync(string url)
    {
        try
        {
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: ClientConfig.Load().EffectiveWebViewDir);

            await _web.EnsureCoreWebView2Async(environment);

            CoreWebView2 core = _web.CoreWebView2;

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
            core.Settings.IsSwipeNavigationEnabled = false;

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
            };

            _web.Source = new Uri(url);
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
        }
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
