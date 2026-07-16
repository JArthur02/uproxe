using UProxy.Core.Config;

namespace UProxy.UI;

public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly NumericUpDown _concurrency = new() { Minimum = 1, Maximum = 256, Width = 80 };
    private readonly NumericUpDown _timeout = new() { Minimum = 1, Maximum = 120, Width = 80 };
    private readonly TextBox _judge = new() { Width = 360 };
    private readonly ComboBox _userAgent = new() { Width = 360, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _httpSources = new() { Width = 360 };
    private readonly TextBox _socksSources = new() { Width = 360 };
    private readonly TextBox _geoIp = new() { Width = 360 };
    private readonly CheckBox _autoCheck = new() { Text = "Auto-check after scrape", AutoSize = true };
    private readonly CheckBox _autoSave = new() { Text = "Remember settings", AutoSize = true };
    private readonly CheckBox _remoteDns = new() { Text = "Resolve hostnames through proxy (Fake-IP / remote DNS)", AutoSize = true };
    private readonly CheckBox _socks4a = new() { Text = "Use SOCKS 4A extension", AutoSize = true };
    private readonly CheckBox _fakeIp = new() { Text = "Allocate 127.8.x.x Fake-IP placeholders", AutoSize = true };

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        Text = "μProxy Tool Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(480, 400);
        Font = new Font("Segoe UI", 9f);

        _concurrency.Value = settings.Concurrency;
        _timeout.Value = Math.Clamp(settings.TimeoutMs / 1000, 1, 120);
        _judge.Text = settings.JudgeUrl;
        foreach (var (name, value) in UserAgents.Presets)
            _userAgent.Items.Add(new UserAgentItem(name, value));
        // Show the friendly preset name when the current UA matches one, otherwise the raw string.
        var matchedPreset = UserAgents.Presets.FirstOrDefault(p => p.Value == settings.UserAgent);
        _userAgent.Text = matchedPreset.Value is null ? settings.UserAgent : matchedPreset.Name;
        _httpSources.Text = settings.HttpSourcesPath;
        _socksSources.Text = settings.SocksSourcesPath;
        _geoIp.Text = settings.GeoIpDatabasePath;
        _autoCheck.Checked = settings.AutoCheckAfterScrape;
        _autoSave.Checked = settings.AutoSaveResults;
        _remoteDns.Checked = settings.ResolveHostnamesThroughProxy;
        _socks4a.Checked = settings.UseSocks4a;
        _fakeIp.Checked = settings.EnableFakeIpDns;

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
        Row("User-Agent", _userAgent);
        Row("HTTP sources", _httpSources);
        Row("SOCKS sources", _socksSources);
        Row("GeoIP DB", _geoIp);
        layout.SetColumnSpan(_autoCheck, 2);
        layout.Controls.Add(_autoCheck, 0, layout.RowCount++);
        layout.SetColumnSpan(_autoSave, 2);
        layout.Controls.Add(_autoSave, 0, layout.RowCount++);
        layout.SetColumnSpan(_remoteDns, 2);
        layout.Controls.Add(_remoteDns, 0, layout.RowCount++);
        layout.SetColumnSpan(_socks4a, 2);
        layout.Controls.Add(_socks4a, 0, layout.RowCount++);
        layout.SetColumnSpan(_fakeIp, 2);
        layout.Controls.Add(_fakeIp, 0, layout.RowCount++);

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
            _settings.UserAgent = ResolveUserAgent(_userAgent.Text.Trim());
            _settings.HttpSourcesPath = _httpSources.Text.Trim();
            _settings.SocksSourcesPath = _socksSources.Text.Trim();
            _settings.GeoIpDatabasePath = _geoIp.Text.Trim();
            _settings.AutoCheckAfterScrape = _autoCheck.Checked;
            _settings.AutoSaveResults = _autoSave.Checked;
            _settings.ResolveHostnamesThroughProxy = _remoteDns.Checked;
            _settings.UseSocks4a = _socks4a.Checked;
            _settings.EnableFakeIpDns = _fakeIp.Checked;
            _settings.Clamp();
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(layout);
        Controls.Add(buttons);
    }

    private static string ResolveUserAgent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return UserAgents.Default;
        var preset = UserAgents.Presets.FirstOrDefault(p => p.Name == text);
        return preset.Value ?? text;
    }

    private sealed record UserAgentItem(string Name, string Value)
    {
        public override string ToString() => Name;
    }
}
