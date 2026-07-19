using UProxy.Core.Exporting;
using UProxy.Core.Models;

namespace UProxy.UI;

public sealed class ExportForm : Form
{
    private readonly List<ProxyCheckResult> _results;
    private readonly CheckedListBox _countries = new() { CheckOnClick = true, IntegralHeight = false };
    private readonly CheckBox _filterCountry = new() { Text = "Filter countries", AutoSize = true };
    private readonly CheckBox _elite = new() { Text = "Elite", Checked = true, AutoSize = true };
    private readonly CheckBox _anon = new() { Text = "Anonymous", Checked = true, AutoSize = true };
    private readonly CheckBox _trans = new() { Text = "Transparent", Checked = true, AutoSize = true };
    private readonly CheckBox _http = new() { Text = "Http", Checked = true, AutoSize = true };
    private readonly CheckBox _https = new() { Text = "Https", Checked = true, AutoSize = true };
    private readonly CheckBox _socks4 = new() { Text = "Socks 4", Checked = true, AutoSize = true };
    private readonly CheckBox _socks5 = new() { Text = "Socks 5", Checked = true, AutoSize = true };
    private readonly CheckBox _socks45 = new() { Text = "Socks 4/5", Checked = true, AutoSize = true };

    public ExportForm(List<ProxyCheckResult> results)
    {
        _results = results;
        Text = "Export";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9f);
        MinimumSize = new Size(560, 0);
        Padding = new Padding(12);

        foreach (var c in results.Select(r => r.Country).Distinct().OrderBy(x => x))
            _countries.Items.Add(c, true);

        var columns = new TableLayoutPanel
        {
            ColumnCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(0, 4, 0, 8)
        };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33f));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33f));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34f));
        columns.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        columns.Controls.Add(BuildCheckGroup("Anonymity", [_elite, _anon, _trans]), 0, 0);
        columns.Controls.Add(BuildCheckGroup("Type", [_http, _https, _socks4, _socks5, _socks45]), 1, 0);
        columns.Controls.Add(BuildCountryGroup(), 2, 0);

        var exportBtn = new Button
        {
            Text = "&Export",
            AutoSize = true,
            Padding = new Padding(20, 8, 20, 8),
            MinimumSize = new Size(100, 36),
            Anchor = AnchorStyles.None
        };
        exportBtn.Click += async (_, _) => await DoExportAsync();

        var footer = new TableLayoutPanel
        {
            ColumnCount = 3,
            AutoSize = true,
            Dock = DockStyle.Bottom,
            Padding = new Padding(0, 8, 0, 4)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        footer.Controls.Add(exportBtn, 1, 0);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(columns, 0, 0);
        root.Controls.Add(footer, 0, 1);
        Controls.Add(root);

        _filterCountry.CheckedChanged += (_, _) => _countries.Enabled = _filterCountry.Checked;
        _countries.Enabled = false;
    }

    private static GroupBox BuildCheckGroup(string title, CheckBox[] boxes)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 4, 8, 8)
        };
        foreach (var box in boxes)
        {
            box.Margin = new Padding(0, 2, 0, 2);
            panel.Controls.Add(box);
        }

        return new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Margin = new Padding(4)
        }.Also(g => g.Controls.Add(panel));
    }

    private GroupBox BuildCountryGroup()
    {
        var panel = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(8, 4, 8, 8)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));
        _filterCountry.Margin = new Padding(0, 0, 0, 6);
        _countries.Dock = DockStyle.Fill;
        panel.Controls.Add(_filterCountry, 0, 0);
        panel.Controls.Add(_countries, 0, 1);

        var box = new GroupBox
        {
            Text = "Country",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Margin = new Padding(4),
            MinimumSize = new Size(180, 0)
        };
        box.Controls.Add(panel);
        return box;
    }

    private async Task DoExportAsync()
    {
        var filter = new ExportFilter
        {
            AliveOnly = true,
            FilterByCountry = _filterCountry.Checked,
            AnonymityLevels = BuildAnon(),
            Protocols = BuildProtocols(),
            Countries = _countries.CheckedItems.Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
        };

        using var dlg = new SaveFileDialog
        {
            Filter = "Text list|*.txt|CSV|*.csv|JSON|*.json",
            FileName = "proxies"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            int count;
            var path = dlg.FileName;
            if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                count = await ProxyExporter.WriteCsvAsync(path, _results, filter);
            else if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                count = await ProxyExporter.WriteJsonAsync(path, _results, filter);
            else
                count = await ProxyExporter.WritePlainAsync(path, _results, filter);

            MessageBox.Show($"{count} proxies saved.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private HashSet<AnonymityLevel> BuildAnon()
    {
        var set = new HashSet<AnonymityLevel>();
        if (_elite.Checked) set.Add(AnonymityLevel.Elite);
        if (_anon.Checked) set.Add(AnonymityLevel.Anonymous);
        if (_trans.Checked) set.Add(AnonymityLevel.Transparent);
        return set;
    }

    private HashSet<ProxyProtocol> BuildProtocols()
    {
        var set = new HashSet<ProxyProtocol>();
        if (_http.Checked) set.Add(ProxyProtocol.Http);
        if (_https.Checked) set.Add(ProxyProtocol.Https);
        if (_socks4.Checked) set.Add(ProxyProtocol.Socks4);
        if (_socks5.Checked) set.Add(ProxyProtocol.Socks5);
        if (_socks45.Checked) set.Add(ProxyProtocol.Socks4And5);
        return set;
    }
}

file static class ExportFormExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
