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

    private readonly TextBox _target = new() { MinimumSize = new Size(200, 28) };
    private readonly Button _browseFile = new() { Text = "File…" };
    private readonly Button _browseFolder = new() { Text = "Folder…" };
    private readonly Button _scanTarget = new() { Text = "Scan" };
    private readonly Button _scanProxies = new() { Text = "Scan loaded proxies" };
    private readonly CheckBox _verify = new()
    {
        Text = "Verify findings (sends candidates to provider APIs)",
        AutoSize = true
    };
    private readonly DataGridView _grid = new();
    private readonly BindingList<FindingRow> _rows = [];
    private readonly Label _status = new()
    {
        AutoSize = true,
        ForeColor = Color.FromArgb(70, 70, 70),
        Dock = DockStyle.Fill,
        Margin = new Padding(0)
    };

    private CancellationTokenSource? _cts;

    public SecretScanForm(AppSettings settings, Func<string?> loadedProxiesText)
    {
        _settings = settings;
        _loadedProxiesText = loadedProxiesText;

        Text = "Secret Scanner — TruffleHog";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        MinimumSize = new Size(720, 420);
        ClientSize = new Size(860, 520);
        Font = new Font("Segoe UI", 9f);
        BackColor = Color.White;

        _verify.Checked = _settings.SecretScanVerify;

        StyleToolbarButton(_browseFile);
        StyleToolbarButton(_browseFolder);
        StyleToolbarButton(_scanTarget);
        StyleToolbarButton(_scanProxies);

        // Single root TableLayoutPanel avoids Dock.Top AutoSize height bugs that clip
        // toolbar buttons after a monitor/DPI change (the previous FlowLayout Height=40 issue).
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(8, 8, 8, 8)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // target row
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // options row
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // results
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // status

        var targetRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 5,
            Margin = new Padding(0, 0, 0, 6)
        };
        targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        targetRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var targetLabel = new Label
        {
            Text = "Target:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 8, 0)
        };
        _target.Dock = DockStyle.Fill;
        _target.Margin = new Padding(0, 4, 6, 4);
        _target.Anchor = AnchorStyles.Left | AnchorStyles.Right;

        targetRow.Controls.Add(targetLabel, 0, 0);
        targetRow.Controls.Add(_target, 1, 0);
        targetRow.Controls.Add(_browseFile, 2, 0);
        targetRow.Controls.Add(_browseFolder, 3, 0);
        targetRow.Controls.Add(_scanTarget, 4, 0);

        var optionsRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(0)
        };
        _scanProxies.Margin = new Padding(0, 2, 12, 2);
        _verify.Margin = new Padding(0, 8, 0, 2);
        optionsRow.Controls.Add(_scanProxies);
        optionsRow.Controls.Add(_verify);

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 246, 248);
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.Margin = new Padding(0, 0, 0, 8);
        _grid.Columns.AddRange(
        [
            Col(nameof(FindingRow.Detector), "Detector", 90, 70),
            Col(nameof(FindingRow.Verified), "Verified", 60, 56),
            Col(nameof(FindingRow.Secret), "Secret (redacted)", 140, 100),
            Col(nameof(FindingRow.Location), "Location", 200, 100),
            Col(nameof(FindingRow.Line), "Line", 50, 44)
        ]);
        _grid.DataSource = _rows;
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].DataBoundItem is FindingRow row && row.Verified == "yes")
                e.CellStyle.BackColor = Color.FromArgb(253, 236, 234);
        };

        var statusBar = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(2, 4, 2, 2),
            Margin = new Padding(0)
        };
        statusBar.Controls.Add(_status);

        root.Controls.Add(targetRow, 0, 0);
        root.Controls.Add(optionsRow, 0, 1);
        root.Controls.Add(_grid, 0, 2);
        root.Controls.Add(statusBar, 0, 3);
        Controls.Add(root);

        _browseFile.Click += (_, _) => BrowseFile();
        _browseFolder.Click += (_, _) => BrowseFolder();
        _scanTarget.Click += async (_, _) => await ScanTargetAsync();
        _scanProxies.Click += async (_, _) => await ScanLoadedProxiesAsync();
        FormClosing += (_, _) => _cts?.Cancel();

        // After DPI auto-scale, force toolbar row to prefer the tallest child so buttons
        // never end up half-clipped when the TextBox preferred height is smaller.
        Shown += (_, _) => EnsureToolbarFits(targetRow);

        SetStatus("Checking for TruffleHog…");
        _ = InitVersionAsync();
    }

    private static void StyleToolbarButton(Button btn)
    {
        btn.AutoSize = true;
        btn.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        btn.Padding = new Padding(12, 6, 12, 6);
        btn.Margin = new Padding(2, 2, 2, 2);
        // Floor height so DPI shrink / TableLayout measure never clips caption text.
        btn.MinimumSize = new Size(64, 32);
        btn.Anchor = AnchorStyles.None;
    }

    private static void EnsureToolbarFits(TableLayoutPanel targetRow)
    {
        var needed = 0;
        foreach (Control c in targetRow.Controls)
            needed = Math.Max(needed, c.PreferredSize.Height);
        if (needed <= 0)
            return;

        foreach (Control c in targetRow.Controls)
        {
            if (c is Button b)
                b.MinimumSize = new Size(Math.Max(b.MinimumSize.Width, 64), Math.Max(needed, 32));
        }

        targetRow.MinimumSize = new Size(0, needed + targetRow.Padding.Vertical);
        targetRow.PerformLayout();
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

    private static DataGridViewTextBoxColumn Col(string prop, string header, int weight, int minWidth) => new()
    {
        DataPropertyName = prop,
        HeaderText = header,
        FillWeight = weight,
        MinimumWidth = minWidth
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
