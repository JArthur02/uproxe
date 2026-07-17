using UProxy.Core.Config;

namespace UProxy.UI;

public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly NumericUpDown _concurrency = new() { Minimum = 1, Maximum = 256, MinimumSize = new Size(80, 0) };
    private readonly NumericUpDown _timeout = new() { Minimum = 1, Maximum = 120, MinimumSize = new Size(80, 0) };
    private readonly TextBox _judge = new();
    private readonly ComboBox _userAgent = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _httpSources = new();
    private readonly TextBox _socksSources = new();
    private readonly TextBox _geoIp = new();
    private readonly TextBox _truffleHog = new();
    private readonly CheckBox _secretVerify = new() { Text = "Verify secrets online (sends candidates to provider APIs)", AutoSize = true };
    private readonly NumericUpDown _chainHttpPort = new() { Minimum = 1, Maximum = 65535, MinimumSize = new Size(80, 0) };
    private readonly NumericUpDown _chainSocksPort = new() { Minimum = 1, Maximum = 65535, MinimumSize = new Size(80, 0) };
    private readonly TextBox _exitIpUrl = new();
    private readonly CheckBox _chainSystemProxy = new()
    {
        Text = "Enable Windows system proxy when gateway starts (local HTTP gateway)",
        AutoSize = true
    };
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
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9f);
        Padding = new Padding(0);
        MinimumSize = new Size(560, 0);

        _concurrency.Value = settings.Concurrency;
        _timeout.Value = Math.Clamp(settings.TimeoutMs / 1000, 1, 120);
        _judge.Text = settings.JudgeUrl;
        foreach (var (name, value) in UserAgents.Presets)
            _userAgent.Items.Add(new UserAgentItem(name, value));
        var matchedPreset = UserAgents.Presets.FirstOrDefault(p => p.Value == settings.UserAgent);
        _userAgent.Text = matchedPreset.Value is null ? settings.UserAgent : matchedPreset.Name;
        _httpSources.Text = settings.HttpSourcesPath;
        _socksSources.Text = settings.SocksSourcesPath;
        _geoIp.Text = settings.GeoIpDatabasePath;
        _truffleHog.Text = settings.TruffleHogPath;
        _secretVerify.Checked = settings.SecretScanVerify;
        _autoCheck.Checked = settings.AutoCheckAfterScrape;
        _autoSave.Checked = settings.AutoSaveResults;
        _remoteDns.Checked = settings.ResolveHostnamesThroughProxy;
        _socks4a.Checked = settings.UseSocks4a;
        _fakeIp.Checked = settings.EnableFakeIpDns;
        _chainHttpPort.Value = Math.Clamp(settings.ChainHttpPort, 1, 65535);
        _chainSocksPort.Value = Math.Clamp(settings.ChainSocksPort, 1, 65535);
        _exitIpUrl.Text = settings.ExitIpCheckUrl;
        _chainSystemProxy.Checked = settings.ChainEnableSystemProxy;

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 12, 12, 4),
            Dock = DockStyle.Top
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        void Row(string label, Control c)
        {
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(0, 3, 0, 3);
            var lbl = new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 8, 0)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(lbl, 0, layout.RowCount);
            layout.Controls.Add(c, 1, layout.RowCount);
            layout.RowCount++;
        }

        void CheckRow(CheckBox box)
        {
            box.Margin = new Padding(0, 4, 0, 2);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.SetColumnSpan(box, 2);
            layout.Controls.Add(box, 0, layout.RowCount);
            layout.RowCount++;
        }

        Row("Workers", _concurrency);
        Row("Timeout (s)", _timeout);
        Row("Judge URL", _judge);
        Row("User-Agent", _userAgent);
        Row("HTTP sources", _httpSources);
        Row("SOCKS sources", _socksSources);
        Row("GeoIP DB", _geoIp);
        Row("TruffleHog path", _truffleHog);
        Row("Chain HTTP port", _chainHttpPort);
        Row("Chain SOCKS port", _chainSocksPort);
        Row("Exit IP check URL", _exitIpUrl);
        CheckRow(_secretVerify);
        CheckRow(_autoCheck);
        CheckRow(_autoSave);
        CheckRow(_remoteDns);
        CheckRow(_socks4a);
        CheckRow(_fakeIp);
        CheckRow(_chainSystemProxy);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Bottom,
            Padding = new Padding(12, 8, 12, 12),
            WrapContents = false
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, Padding = new Padding(16, 6, 16, 6), MinimumSize = new Size(88, 32) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(16, 6, 16, 6), MinimumSize = new Size(88, 32), Margin = new Padding(0, 0, 8, 0) };
        ok.Click += (_, _) =>
        {
            _settings.Concurrency = (int)_concurrency.Value;
            _settings.TimeoutMs = (int)_timeout.Value * 1000;
            _settings.JudgeUrl = _judge.Text.Trim();
            _settings.UserAgent = ResolveUserAgent(_userAgent.Text.Trim());
            _settings.HttpSourcesPath = _httpSources.Text.Trim();
            _settings.SocksSourcesPath = _socksSources.Text.Trim();
            _settings.GeoIpDatabasePath = _geoIp.Text.Trim();
            _settings.TruffleHogPath = string.IsNullOrWhiteSpace(_truffleHog.Text) ? "trufflehog" : _truffleHog.Text.Trim();
            _settings.SecretScanVerify = _secretVerify.Checked;
            _settings.AutoCheckAfterScrape = _autoCheck.Checked;
            _settings.AutoSaveResults = _autoSave.Checked;
            _settings.ResolveHostnamesThroughProxy = _remoteDns.Checked;
            _settings.UseSocks4a = _socks4a.Checked;
            _settings.EnableFakeIpDns = _fakeIp.Checked;
            _settings.ChainHttpPort = (int)_chainHttpPort.Value;
            _settings.ChainSocksPort = (int)_chainSocksPort.Value;
            _settings.ExitIpCheckUrl = string.IsNullOrWhiteSpace(_exitIpUrl.Text)
                ? "https://api.ipify.org"
                : _exitIpUrl.Text.Trim();
            _settings.ChainEnableSystemProxy = _chainSystemProxy.Checked;
            _settings.Clamp();
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(layout, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);
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
