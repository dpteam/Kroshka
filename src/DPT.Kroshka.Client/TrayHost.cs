using System.Drawing;
using System.Windows.Forms;

namespace DPT.Kroshka.Client;

/// <summary>
/// The tray icon and its menu. Kept separate from the form so the tray keeps working
/// when the window is hidden, which is the normal state for a family messenger.
/// </summary>
public sealed class TrayHost : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _autostartItem;
    private int _badge;
    private bool _disposed;

    public event EventHandler OpenRequested;
    public event EventHandler SettingsRequested;
    public event EventHandler RestartServerRequested;
    public event EventHandler ExitRequested;

    public TrayHost()
    {
        Icon icon = LoadIcon();

        _statusItem = new ToolStripMenuItem("Состояние: запуск…") { Enabled = false };
        _openItem = new ToolStripMenuItem("Открыть Kroshka", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty))
        {
            Font = new Font(SystemFonts.MenuFont, FontStyle.Bold)
        };

        _autostartItem = new ToolStripMenuItem("Запускать с Windows", null, (_, _) => ToggleAutostart())
        {
            CheckOnClick = false
        };

        ContextMenuStrip menu = new();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_openItem);
        menu.Items.Add(new ToolStripMenuItem("Сервер в локальной сети…", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripMenuItem("Перезапустить сервер", null, (_, _) => RestartServerRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));

        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = "Kroshka",
            Visible = true,
            ContextMenuStrip = menu
        };

        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        SyncAutostart();
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

    public void SetStatus(string text)
    {
        if (_disposed) return;
        _statusItem.Text = "Состояние: " + text;

        // NotifyIcon.Text is capped at 63 characters by the shell.
        string tip = text.Length > 60 ? text[..60] : text;
        _icon.Text = _badge > 0 ? $"Kroshka — {_badge}" : "Kroshka\n" + tip;
    }

    public void SetBadge(int count)
    {
        _badge = count;
        if (_disposed) return;
        _icon.Text = count > 0 ? $"Kroshka — {count} новых" : "Kroshka";
    }

    public void ShowBalloon(string title, string body)
    {
        if (_disposed) return;
        try
        {
            _icon.ShowBalloonTip(4000, title, body, ToolTipIcon.None);
        }
        catch { }
    }

    private void ToggleAutostart()
    {
        ClientConfig cfg = ClientConfig.Load();
        cfg.StartWithWindows = !cfg.StartWithWindows;
        cfg.Save();
        Autostart.Apply(cfg.StartWithWindows);
        SyncAutostart();
    }

    private void SyncAutostart()
    {
        _autostartItem.Checked = Autostart.IsEnabled();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch { }
    }
}

/// <summary>
/// Per-user autostart through the Run key. No scheduled task, no admin rights: this
/// is a chat client, not a service.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Kroshka";

    public static bool IsEnabled()
    {
        try
        {
            using Microsoft.Win32.RegistryKey key =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using Microsoft.Win32.RegistryKey key =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;

            if (enabled)
            {
                string exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch (Exception ex)
        {
            DPT.Kroshka.Shared.Log.Warn("CLIENT", "autostart: " + ex.Message);
        }
    }
}
