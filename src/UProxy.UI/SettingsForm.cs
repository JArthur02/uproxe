using UProxy.Core.Config;

namespace UProxy.UI;

public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly NumericUpDown _concurrency = new() { Minimum = 1, Maximum = 256, Width = 80 };
    private readonly NumericUpDown _timeout = new() { Minimum = 1, Maximum = 120, Width = 80 };
    private readonly TextBox _judge = new() { Width = 360 };
    private readonly TextBox _httpSources = new() { Width = 360 };
    private readonly TextBox _socksSources = new() { Width = 360 };
    private readonly TextBox _geoIp = new() { Width = 360 };
    private readonly CheckBox _autoCheck = new() { Text = "Auto-check after scrape", AutoSize = true };
    private readonly CheckBox _autoSave = new() { Text = "Remember settings", AutoSize = true };

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        Text = "μProxy Tool Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 320);
        Font = new Font("Segoe UI", 9f);

        _concurrency.Value = settings.Concurrency;
        _timeout.Value = Math.Clamp(settings.TimeoutMs / 1000, 1, 120);
        _judge.Text = settings.JudgeUrl;
        _httpSources.Text = settings.HttpSourcesPath;
        _socksSources.Text = settings.SocksSourcesPath;
        _geoIp.Text = settings.GeoIpDatabasePath;
        _autoCheck.Checked = settings.AutoCheckAfterScrape;
        _autoSave.Checked = settings.AutoSaveResults;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(12),
            AutoSize = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control c)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, layout.RowCount);
            layout.Controls.Add(c, 1, layout.RowCount);
            layout.RowCount++;
        }

        Row("Workers", _concurrency);
        Row("Timeout (s)", _timeout);
        Row("Judge URL", _judge);
        Row("HTTP sources", _httpSources);
        Row("SOCKS sources", _socksSources);
        Row("GeoIP DB", _geoIp);
        layout.Controls.Add(_autoCheck, 1, layout.RowCount++);
        layout.Controls.Add(_autoSave, 1, layout.RowCount++);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 40,
            Padding = new Padding(8)
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
        ok.Click += (_, _) =>
        {
            _settings.Concurrency = (int)_concurrency.Value;
            _settings.TimeoutMs = (int)_timeout.Value * 1000;
            _settings.JudgeUrl = _judge.Text.Trim();
            _settings.HttpSourcesPath = _httpSources.Text.Trim();
            _settings.SocksSourcesPath = _socksSources.Text.Trim();
            _settings.GeoIpDatabasePath = _geoIp.Text.Trim();
            _settings.AutoCheckAfterScrape = _autoCheck.Checked;
            _settings.AutoSaveResults = _autoSave.Checked;
            _settings.Clamp();
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(layout);
        Controls.Add(buttons);
    }
}
