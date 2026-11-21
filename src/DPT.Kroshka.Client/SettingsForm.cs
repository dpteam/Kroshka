using System.Drawing;
using System.Windows.Forms;

namespace DPT.Kroshka.Client;

/// <summary>
/// The one dialog a family member ever needs: where the server lives and how the
/// window behaves. Built by hand instead of the designer so the source is reviewable.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly RadioButton _localMode;
    private readonly RadioButton _remoteMode;
    private readonly TextBox _serverUrl;
    private readonly NumericUpDown _port;
    private readonly CheckBox _closeToTray;
    private readonly CheckBox _notifications;
    private readonly CheckBox _startWithWindows;

    public string Mode => _remoteMode.Checked ? "remote" : "local";
    public string ServerUrl => _serverUrl.Text.Trim();
    public int LocalPort => (int)_port.Value;
    public bool CloseToTray => _closeToTray.Checked;
    public bool Notifications => _notifications.Checked;
    public bool StartWithWindows => _startWithWindows.Checked;

    public SettingsForm(ClientConfig cfg)
    {
        Text = "Настройки Kroshka";
        Size = new Size(460, 470);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Color.White;
        Padding = new Padding(18);

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 9,
            AutoSize = false
        };

        Label header = new()
        {
            Text = "Сервер",
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(11, 22, 34),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        };

        _localMode = new RadioButton
        {
            Text = "Этот компьютер — сервер (для семьи без отдельной машины)",
            AutoSize = true,
            Checked = cfg.Mode != "remote",
            Margin = new Padding(0, 2, 0, 2)
        };

        _remoteMode = new RadioButton
        {
            Text = "Подключаться к серверу в сети",
            AutoSize = true,
            Checked = cfg.Mode == "remote",
            Margin = new Padding(0, 2, 0, 6)
        };

        _serverUrl = new TextBox
        {
            Text = cfg.ServerUrl ?? "",
            Width = 400,
            PlaceholderText = "http://192.168.1.10:6767",
            Margin = new Padding(22, 0, 0, 10),
            Enabled = cfg.Mode == "remote"
        };

        TableLayoutPanel portRow = new()
        {
            ColumnCount = 3,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12)
        };

        portRow.Controls.Add(new Label { Text = "Порт локального сервера:", AutoSize = true, Margin = new Padding(0, 5, 8, 0) });

        _port = new NumericUpDown
        {
            Minimum = 1024,
            Maximum = 65535,
            Value = cfg.LocalPort is >= 1024 and <= 65535 ? cfg.LocalPort : 6767,
            Width = 90
        };
        portRow.Controls.Add(_port);

        Label hint = new()
        {
            Text = "6767 по умолчанию",
            AutoSize = true,
            ForeColor = Color.FromArgb(140, 154, 171),
            Margin = new Padding(8, 5, 0, 0)
        };
        portRow.Controls.Add(hint);

        Label behaviour = new()
        {
            Text = "Поведение",
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(11, 22, 34),
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 6)
        };

        _closeToTray = new CheckBox
        {
            Text = "Закрывать в трей, а не выходить",
            Checked = cfg.CloseToTray,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 2)
        };

        _notifications = new CheckBox
        {
            Text = "Показывать уведомления",
            Checked = cfg.Notifications,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 2)
        };

        _startWithWindows = new CheckBox
        {
            Text = "Запускать вместе с Windows",
            Checked = cfg.StartWithWindows,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 8)
        };

        layout.Controls.Add(header);
        layout.Controls.Add(_localMode);
        layout.Controls.Add(_remoteMode);
        layout.Controls.Add(_serverUrl);
        layout.Controls.Add(portRow);
        layout.Controls.Add(behaviour);
        layout.Controls.Add(_closeToTray);
        layout.Controls.Add(_notifications);
        layout.Controls.Add(_startWithWindows);

        FlowLayoutPanel buttons = new()
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(0, 10, 0, 0)
        };

        Button save = new()
        {
            Text = "Сохранить",
            DialogResult = DialogResult.OK,
            Width = 110,
            Height = 32,
            BackColor = Color.FromArgb(0, 136, 255),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        save.FlatAppearance.BorderSize = 0;

        Button cancel = new()
        {
            Text = "Отмена",
            DialogResult = DialogResult.Cancel,
            Width = 100,
            Height = 32,
            FlatStyle = FlatStyle.Flat
        };

        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);

        Controls.Add(layout);
        Controls.Add(buttons);

        AcceptButton = save;
        CancelButton = cancel;

        _remoteMode.CheckedChanged += (_, _) => _serverUrl.Enabled = _remoteMode.Checked;
        _localMode.CheckedChanged += (_, _) => _serverUrl.Enabled = _remoteMode.Checked;
    }
}
