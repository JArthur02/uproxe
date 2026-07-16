using UProxy.Core.Exporting;
using UProxy.Core.Models;

namespace UProxy.UI;

public sealed class ExportForm : Form
{
    private readonly List<ProxyCheckResult> _results;
    private readonly CheckedListBox _countries = new() { CheckOnClick = true, Width = 180, Height = 160 };
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
        ClientSize = new Size(420, 280);
        Font = new Font("Segoe UI", 9f);

        foreach (var c in results.Select(r => r.Country).Distinct().OrderBy(x => x))
            _countries.Items.Add(c, true);

        var anonBox = new GroupBox { Text = "Anonymity", Location = new Point(12, 12), Size = new Size(140, 100) };
        _elite.Location = new Point(12, 22);
        _anon.Location = new Point(12, 44);
        _trans.Location = new Point(12, 66);
        anonBox.Controls.AddRange([_elite, _anon, _trans]);

        var typeBox = new GroupBox { Text = "Type", Location = new Point(160, 12), Size = new Size(140, 140) };
        _http.Location = new Point(12, 22);
        _https.Location = new Point(12, 44);
        _socks4.Location = new Point(12, 66);
        _socks5.Location = new Point(12, 88);
        _socks45.Location = new Point(12, 110);
        typeBox.Controls.AddRange([_http, _https, _socks4, _socks5, _socks45]);

        var countryBox = new GroupBox { Text = "Country", Location = new Point(310, 12), Size = new Size(100, 200) };
        // widen form
        ClientSize = new Size(520, 300);
        countryBox.Size = new Size(190, 200);
        _filterCountry.Location = new Point(10, 20);
        _countries.Location = new Point(10, 45);
        _countries.Enabled = false;
        _filterCountry.CheckedChanged += (_, _) => _countries.Enabled = _filterCountry.Checked;
        countryBox.Controls.Add(_filterCountry);
        countryBox.Controls.Add(_countries);

        var exportBtn = new Button
        {
            Text = "&Export",
            Width = 100,
            Height = 28,
            Location = new Point(200, 230)
        };
        exportBtn.Click += async (_, _) => await DoExportAsync();

        Controls.Add(anonBox);
        Controls.Add(typeBox);
        Controls.Add(countryBox);
        Controls.Add(exportBtn);
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
