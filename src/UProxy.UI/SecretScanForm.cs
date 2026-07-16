using System.ComponentModel;
using UProxy.Core.Config;
using UProxy.Core.Security;

namespace UProxy.UI;

/// <summary>
/// Runs the TruffleHog secret scanner against a file/folder or the currently loaded proxies,
/// so leaked credentials can be caught before a list is exported or shared.
/// </summary>
public sealed class SecretScanForm : Form
{
    private readonly AppSettings _settings;
    private readonly Func<string?> _loadedProxiesText;

    private readonly TextBox _target = new() { Width = 360, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly Button _browseFile = new() { Text = "File…", Width = 70 };
    private readonly Button _browseFolder = new() { Text = "Folder…", Width = 80 };
    private readonly Button _scanTarget = new() { Text = "Scan target", Width = 100 };
    private readonly Button _scanProxies = new() { Text = "Scan loaded proxies", AutoSize = true };
    private readonly CheckBox _verify = new() { Text = "Verify findings (sends candidates to provider APIs)", AutoSize = true };
    private readonly DataGridView _grid = new();
    private readonly BindingList<FindingRow> _rows = [];
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.FromArgb(70, 70, 70) };

    private CancellationTokenSource? _cts;

    public SecretScanForm(AppSettings settings, Func<string?> loadedProxiesText)
    {
        _settings = settings;
        _loadedProxiesText = loadedProxiesText;

        Text = "Secret Scanner — TruffleHog";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 420);
        ClientSize = new Size(820, 480);
        Font = new Font("Segoe UI", 9f);
        BackColor = Color.White;

        _verify.Checked = _settings.SecretScanVerify;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 8, 8, 0), WrapContents = false };
        top.Controls.AddRange([new Label { Text = "Target:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) },
            _target, _browseFile, _browseFolder, _scanTarget]);

        var second = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 4, 8, 4), WrapContents = false };
        second.Controls.AddRange([_scanProxies, _verify]);

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.None;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 246, 248);
        _grid.Columns.AddRange(
        [
            Col(nameof(FindingRow.Detector), "Detector", 90),
            Col(nameof(FindingRow.Verified), "Verified", 60),
            Col(nameof(FindingRow.Secret), "Secret (redacted)", 140),
            Col(nameof(FindingRow.Location), "Location", 200),
            Col(nameof(FindingRow.Line), "Line", 50)
        ]);
        _grid.DataSource = _rows;
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].DataBoundItem is FindingRow row && row.Verified == "yes")
                e.CellStyle.BackColor = Color.FromArgb(253, 236, 234);
        };

        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(8, 6, 8, 0) };
        statusBar.Controls.Add(_status);

        Controls.Add(_grid);
        Controls.Add(statusBar);
        Controls.Add(second);
        Controls.Add(top);

        _browseFile.Click += (_, _) => BrowseFile();
        _browseFolder.Click += (_, _) => BrowseFolder();
        _scanTarget.Click += async (_, _) => await ScanTargetAsync();
        _scanProxies.Click += async (_, _) => await ScanLoadedProxiesAsync();
        FormClosing += (_, _) => _cts?.Cancel();

        SetStatus("Checking for TruffleHog…");
        _ = InitVersionAsync();
    }

    private async Task InitVersionAsync()
    {
        var version = await NewScanner().GetVersionAsync().ConfigureAwait(true);
        SetStatus(version is null
            ? $"TruffleHog not found ('{_settings.TruffleHogPath}'). Install it or set the path in Settings."
            : $"Ready — {version}. Choose a target or scan the loaded proxy list.");
        var available = version is not null;
        _scanTarget.Enabled = available;
        _scanProxies.Enabled = available;
    }

    private SecretScanner NewScanner() => new(new SecretScannerOptions
    {
        ExecutablePath = _settings.TruffleHogPath,
        Verify = _verify.Checked
    });

    private void BrowseFile()
    {
        using var dlg = new OpenFileDialog { Title = "Select a file to scan", Filter = "All files|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _target.Text = dlg.FileName;
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select a folder to scan" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _target.Text = dlg.SelectedPath;
    }

    private async Task ScanTargetAsync()
    {
        var path = _target.Text.Trim();
        if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            MessageBox.Show("Choose an existing file or folder to scan.", "Secret Scanner",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        await RunScanAsync(scanner => scanner.ScanFilesystemAsync(path, _cts!.Token), path);
    }

    private async Task ScanLoadedProxiesAsync()
    {
        var text = _loadedProxiesText();
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show("No proxies are loaded. Load or scrape proxies first.", "Secret Scanner",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        await RunScanAsync(scanner => scanner.ScanTextAsync(text!, "loaded-proxies", _cts!.Token), "loaded proxies");
    }

    private async Task RunScanAsync(Func<SecretScanner, Task<SecretScanResult>> run, string label)
    {
        _rows.Clear();
        SetBusy(true);
        _cts = new CancellationTokenSource();
        SetStatus($"Scanning {label}…");
        try
        {
            var result = await run(NewScanner()).ConfigureAwait(true);
            if (!result.Ran)
            {
                SetStatus(result.Error ?? "Scan could not run.");
                return;
            }

            foreach (var f in result.Findings.OrderByDescending(f => f.Verified))
            {
                _rows.Add(new FindingRow
                {
                    Detector = f.Detector,
                    Verified = f.Verified ? "yes" : "",
                    Secret = f.RedactedSecret,
                    Location = f.Location ?? "",
                    Line = f.Line?.ToString() ?? ""
                });
            }

            SetStatus(result.Findings.Count == 0
                ? $"No secrets found in {label} ({result.DurationMs} ms)."
                : $"{result.Findings.Count} finding(s), {result.VerifiedCount} verified, in {label} ({result.DurationMs} ms).");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Scan cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus("Scan failed: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _scanTarget.Enabled = !busy;
        _scanProxies.Enabled = !busy;
        _browseFile.Enabled = !busy;
        _browseFolder.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void SetStatus(string text) => _status.Text = text;

    private static DataGridViewTextBoxColumn Col(string prop, string header, int weight) => new()
    {
        DataPropertyName = prop,
        HeaderText = header,
        FillWeight = weight,
        MinimumWidth = 50
    };

    private sealed class FindingRow
    {
        public string Detector { get; set; } = "";
        public string Verified { get; set; } = "";
        public string Secret { get; set; } = "";
        public string Location { get; set; } = "";
        public string Line { get; set; } = "";
    }
}
