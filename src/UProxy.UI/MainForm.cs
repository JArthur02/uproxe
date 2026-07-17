using System.ComponentModel;
using UProxy.Core.Checking;
using UProxy.Core.Config;
using UProxy.Core.Exporting;
using UProxy.Core.GeoIp;
using UProxy.Core.Models;
using UProxy.Core.Scraping;
using UProxy.Core.Sessions;
using UProxy.Core.Windows;

namespace UProxy.UI;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly string _settingsPath;
    private readonly string _appDirectory;
    private readonly SortableBindingList<ResultRow> _rows = [];
    private WorkSession? _session;
    private IGeoIpResolver _geoIp = NullGeoIpResolver.Instance;
    private readonly WindowsProxyManager _proxyManager = new();
    private readonly SynchronizationContext _ui;

    private readonly BufferedDataGridView _grid = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripStatusLabel _countsLabel = new();
    private readonly ToolStripProgressBar _progress = new();
    private readonly ToolStripStatusLabel _percentLabel = new();
    private readonly RadioButton _httpRadio = new() { Text = "HTTP(S)", Checked = true, AutoSize = true };
    private readonly RadioButton _socksRadio = new() { Text = "SOCKS", AutoSize = true };
    private readonly NumericUpDown _concurrency = new() { Minimum = 1, Maximum = 256, Value = 48, Width = 60 };
    private readonly NumericUpDown _timeoutSec = new() { Minimum = 1, Maximum = 120, Value = 10, Width = 60 };
    private readonly TextBox _judgeBox = new() { MinimumSize = new Size(160, 0), Width = 220 };
    private readonly Button _btnLoad = new() { Text = "Load", AutoSize = true };
    private readonly Button _btnScrape = new() { Text = "Scrape", AutoSize = true };
    private readonly Button _btnCheck = new() { Text = "Check", AutoSize = true };
    private readonly Button _btnStop = new() { Text = "Stop", AutoSize = true, Enabled = false };
    private readonly Button _btnExport = new() { Text = "Export", AutoSize = true };
    private readonly Button _btnSettings = new() { Text = "Settings", AutoSize = true };
    private readonly Button _btnSetProxy = new() { Text = "Set System Proxy…", AutoSize = true };
    private readonly Button _btnResetProxy = new() { Text = "Emergency Reset", AutoSize = true };
    private FlowLayoutPanel? _topBar;
    private FlowLayoutPanel? _actionBar;

    public MainForm()
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _appDirectory = ResolveAppDirectory();
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "uProxyTool",
            "settings.json");

        // Design for 96 DPI; WinForms scales controls when PerMonitorV2 kicks in.
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        _settings = AppSettings.Load(_settingsPath);
        ResolvePaths();
        ApplySettingsToUi();
        InitGeoIp();
        BuildUi();
        RestoreWindowPlacement();
        WireEvents();
        UpdateTitle();
        ReportGeoIpStatus();
    }

    /// <summary>
    /// Directory that holds the shipped <c>Data/</c> folder. For single-file publish this is the
    /// folder containing the .exe (not a temp extract dir), so GeoIP/sources stay findable after moves.
    /// </summary>
    private static string ResolveAppDirectory()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(Path.Combine(dir, "Data")))
                    return dir;
            }
        }
        catch { /* fall through */ }

        return AppContext.BaseDirectory;
    }

    private void ResolvePaths()
    {
        _settings.HttpSourcesPath = AppDataPaths.ResolveExistingOrDefault(
            _settings.HttpSourcesPath,
            Path.Combine("Data", "Source", "HttpSource.txt"),
            _appDirectory,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory);
        _settings.SocksSourcesPath = AppDataPaths.ResolveExistingOrDefault(
            _settings.SocksSourcesPath,
            Path.Combine("Data", "Source", "SocksSource.txt"),
            _appDirectory,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory);
        _settings.GeoIpDatabasePath = AppDataPaths.ResolveExistingOrDefault(
            _settings.GeoIpDatabasePath,
            Path.Combine("Data", "Country.mmdb"),
            _appDirectory,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory);
    }

    private void InitGeoIp()
    {
        _geoIp.Dispose();
        var path = AppDataPaths.ResolveExistingOrDefault(
            _settings.GeoIpDatabasePath,
            Path.Combine("Data", "Country.mmdb"),
            _appDirectory,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory);
        _settings.GeoIpDatabasePath = path;
        _geoIp = File.Exists(path)
            ? new MaxMindGeoIpResolver(path)
            : NullGeoIpResolver.Instance;
    }

    private void ReportGeoIpStatus()
    {
        if (_geoIp is NullGeoIpResolver)
        {
            SetStatus(
                "GeoIP database not found — Country will show Unknown. " +
                $"Expected: {Path.Combine(_appDirectory, "Data", "Country.mmdb")}");
        }
    }

    private void ApplySettingsToUi()
    {
        _concurrency.Value = _settings.Concurrency;
        _timeoutSec.Value = Math.Clamp(_settings.TimeoutMs / 1000, 1, 120);
        _judgeBox.Text = _settings.JudgeUrl;
        _httpRadio.Checked = _settings.ProxyTypeMode == 0;
        _socksRadio.Checked = _settings.ProxyTypeMode == 1;
    }

    private void PersistSettingsFromUi()
    {
        _settings.Concurrency = (int)_concurrency.Value;
        _settings.TimeoutMs = (int)_timeoutSec.Value * 1000;
        _settings.JudgeUrl = _judgeBox.Text.Trim();
        _settings.ProxyTypeMode = _socksRadio.Checked ? 1 : 0;
        // Store portable relative paths when possible so moving the app folder doesn't break GeoIP.
        _settings.HttpSourcesPath = AppDataPaths.ToPortable(_settings.HttpSourcesPath, _appDirectory);
        _settings.SocksSourcesPath = AppDataPaths.ToPortable(_settings.SocksSourcesPath, _appDirectory);
        _settings.GeoIpDatabasePath = AppDataPaths.ToPortable(_settings.GeoIpDatabasePath, _appDirectory);
        _settings.Save(_settingsPath);
        ResolvePaths();
    }

    private void BuildUi()
    {
        Text = "μProxy Tool 2.0";
        Width = 1100;
        Height = 680;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 480);
        Font = new Font("Segoe UI", 9f);
        BackColor = Color.White;

        _topBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(8, 8, 8, 4),
            FlowDirection = FlowDirection.LeftToRight
        };
        _topBar.Controls.AddRange(
        [
            Lbl("Mode:"), _httpRadio, _socksRadio,
            Lbl("Workers:"), _concurrency,
            Lbl("Timeout(s):"), _timeoutSec,
            Lbl("Judge:"), _judgeBox
        ]);

        _actionBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(8, 4, 8, 4),
            FlowDirection = FlowDirection.LeftToRight
        };
        foreach (var btn in new[]
                 {
                     _btnLoad, _btnScrape, _btnCheck, _btnStop, _btnExport, _btnSettings,
                     _btnSetProxy, _btnResetProxy
                 })
        {
            btn.Margin = new Padding(2);
            btn.Padding = new Padding(8, 2, 8, 2);
            _actionBar.Controls.Add(btn);
        }

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = true;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.RowHeadersVisible = false;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.None;
        _grid.AutoGenerateColumns = false;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.GridColor = Color.FromArgb(232, 232, 232);
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 246, 248);
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9f);
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.RowTemplate.Height = 24;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 120, 215);
        _grid.DefaultCellStyle.SelectionForeColor = Color.White;
        _grid.Columns.AddRange(
        [
            GridColumn(nameof(ResultRow.Proxy), "Proxy", 160, minWidth: 110),
            GridColumn(nameof(ResultRow.Country), "Country", 110, minWidth: 100),
            GridColumn(nameof(ResultRow.Anonymity), "Anonymity", 80, minWidth: 72),
            GridColumn(nameof(ResultRow.Protocol), "Type", 70, minWidth: 60),
            GridColumn(nameof(ResultRow.ConnectMs), "Connect", 70, rightAlign: true, minWidth: 60),
            GridColumn(nameof(ResultRow.LatencyMs), "Latency", 70, rightAlign: true, minWidth: 60),
            GridColumn(nameof(ResultRow.Auth), "Auth", 70, minWidth: 56),
            GridColumn(nameof(ResultRow.Detail), "Detail", 160, minWidth: 80)
        ]);
        _grid.DataSource = _rows;
        _grid.CellDoubleClick += (_, _) => CopySelected();
        _grid.CellFormatting += Grid_CellFormatting;
        _grid.ContextMenuStrip = BuildGridMenu();

        _statusLabel.Spring = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Text = "Ready.";
        _countsLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _countsLabel.BorderStyle = Border3DStyle.Etched;
        _progress.AutoSize = false;
        _progress.Width = 140;
        _percentLabel.Text = "0 %";
        _status.Items.AddRange([_statusLabel, _countsLabel, _progress, _percentLabel]);
        _status.Dock = DockStyle.Bottom;

        Controls.Add(_grid);
        Controls.Add(_actionBar);
        Controls.Add(_topBar);
        Controls.Add(_status);

        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("File");
        file.DropDownItems.Add("Load proxies…", null, (_, _) => LoadProxies());
        file.DropDownItems.Add("Export…", null, (_, _) => ExportResults());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("Exit", null, (_, _) => Close());
        var tools = new ToolStripMenuItem("Tools");
        tools.DropDownItems.Add("Settings…", null, (_, _) => OpenSettings());
        tools.DropDownItems.Add("Scan for secrets (TruffleHog)…", null, (_, _) => OpenSecretScanner());
        tools.DropDownItems.Add("Emergency Reset System Proxy", null, (_, _) => EmergencyReset());
        var help = new ToolStripMenuItem("Help");
        help.DropDownItems.Add("About μProxy Tool 2.0", null, (_, _) =>
            MessageBox.Show(
                "μProxy Tool 2.0\n\nProxy scraper & checker.\n" +
                "No update phone-home. GeoIP is local-only.\n" +
                "System proxy changes are opt-in with restore.\n\n" +
                "Based on the μProxy Tool 1.81 feature set.",
                "About", MessageBoxButtons.OK, MessageBoxIcon.Information));
        menu.Items.AddRange([file, tools, help]);
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    private void RestoreWindowPlacement()
    {
        var screens = Screen.AllScreens.Select(s =>
        {
            var b = s.WorkingArea;
            return (b.X, b.Y, b.Width, b.Height);
        });

        var restored = WindowPlacement.TryRestore(
            _settings.WindowLeft, _settings.WindowTop,
            _settings.WindowWidth, _settings.WindowHeight,
            _settings.WindowMaximized, screens);

        if (restored is null)
            return;

        Width = restored.Value.Width;
        Height = restored.Value.Height;

        if (_settings.WindowLeft is int left && _settings.WindowTop is int top)
        {
            var point = new Point(left, top);
            // Require a 40px title-bar sliver on some screen.
            if (Screen.AllScreens.Any(s => s.WorkingArea.Contains(point.X + 40, point.Y + 40)))
            {
                StartPosition = FormStartPosition.Manual;
                Location = point;
            }
            // else keep CenterScreen from BuildUi
        }

        if (restored.Value.Maximized)
            WindowState = FormWindowState.Maximized;
    }

    private void SaveWindowPlacement()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.WindowLeft = bounds.Left;
        _settings.WindowTop = bounds.Top;
        _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
    }

    private static DataGridViewTextBoxColumn GridColumn(
        string property, string header, int fillWeight, bool rightAlign = false, int minWidth = 60)
    {
        var column = new DataGridViewTextBoxColumn
        {
            DataPropertyName = property,
            HeaderText = header,
            FillWeight = fillWeight,
            MinimumWidth = minWidth,
            SortMode = DataGridViewColumnSortMode.Automatic,
            Resizable = DataGridViewTriState.True
        };
        if (rightAlign)
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        return column;
    }

    private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count)
            return;
        if (_grid.Rows[e.RowIndex].DataBoundItem is not ResultRow row)
            return;

        e.CellStyle.BackColor = row.AnonLevel switch
        {
            AnonymityLevel.Elite => Color.FromArgb(230, 244, 234),
            AnonymityLevel.Anonymous => Color.FromArgb(255, 244, 229),
            AnonymityLevel.Transparent => Color.FromArgb(253, 236, 234),
            _ => Color.White
        };
    }

    private ContextMenuStrip BuildGridMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy selected proxies", null, (_, _) => CopySelected());
        menu.Items.Add("Select all", null, (_, _) => _grid.SelectAll());
        menu.Items.Add("Export results…", null, (_, _) => ExportResults());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Clear results", null, (_, _) =>
        {
            EnsureSession();
            _session!.ClearResults();
            _rows.Clear();
            _btnExport.Enabled = false;
            UpdateTitle();
            SetStatus("Results cleared.");
        });
        return menu;
    }

    private static Label Lbl(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(6, 6, 0, 0)
    };

    private void WireEvents()
    {
        _btnLoad.Click += (_, _) => LoadProxies();
        _btnScrape.Click += async (_, _) => await RunScrapeAsync();
        _btnCheck.Click += async (_, _) => await RunCheckAsync();
        _btnStop.Click += async (_, _) =>
        {
            if (_session is not null)
                await _session.StopAsync();
        };
        _btnExport.Click += (_, _) => ExportResults();
        _btnSettings.Click += (_, _) => OpenSettings();
        _btnSetProxy.Click += (_, _) => SetSystemProxyOptIn();
        _btnResetProxy.Click += (_, _) => EmergencyReset();
        FormClosing += async (_, e) =>
        {
            if (_session is not null)
                await _session.DisposeAsync();
            _geoIp.Dispose();
            SaveWindowPlacement();
            PersistSettingsFromUi();
        };
        KeyDown += MainForm_KeyDown;
        KeyPreview = true;
    }

    private void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.V)
        {
            var text = Clipboard.GetText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                EnsureSession();
                var n = _session!.LoadProxiesFromText(text);
                SetStatus($"Added {n} unique proxy(s) from clipboard. Total: {_session.Proxies.Count}");
                e.Handled = true;
            }
        }
        else if (e.Control && e.KeyCode == Keys.C)
        {
            CopySelected();
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode == Keys.X)
        {
            if (MessageBox.Show("Clear proxy list and results?", "μProxy Tool",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                EnsureSession();
                _session!.ClearProxies();
                _session.ClearResults();
                _rows.Clear();
                _btnExport.Enabled = false;
                SetStatus("Cleared.");
            }
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode == Keys.O)
        {
            LoadProxies();
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode == Keys.A)
        {
            if (_grid.Focused)
            {
                _grid.SelectAll();
                e.Handled = true;
            }
        }
    }

    private void EnsureSession()
    {
        if (_session is not null)
            return;
        _session = new WorkSession(_settings, _geoIp);
        _session.ProgressChanged += snap => _ui.Post(_ => ApplyProgress(snap), null);
        _session.ResultAdded += result => _ui.Post(_ => AddResult(result), null);
    }

    private void AddResult(ProxyCheckResult result)
    {
        _rows.Add(new ResultRow
        {
            Proxy = result.Proxy.Endpoint,
            Country = result.Country,
            Anonymity = result.Anonymity.ToString(),
            Protocol = FormatProtocol(result.ConfirmedProtocol),
            ConnectMs = result.ConnectMs ?? 0,
            LatencyMs = result.LatencyMs,
            Auth = FormatAuthShort(result.AuthMethod),
            Detail = result.IsAlive ? "" : FailureMessages.Describe(result.Failure, result.ErrorMessage),
            AnonLevel = result.Anonymity
        });
        _btnExport.Enabled = true;
    }

    private static string FormatProtocol(ProxyProtocol p) => p switch
    {
        ProxyProtocol.Http => "Http",
        ProxyProtocol.Https => "Https",
        ProxyProtocol.Socks4 => "Socks 4",
        ProxyProtocol.Socks5 => "Socks 5",
        ProxyProtocol.Socks4And5 => "Socks 4/5",
        _ => p.ToString()
    };

    private static string FormatAuthShort(ProxyAuthMethod method) => method switch
    {
        ProxyAuthMethod.None => "No",
        ProxyAuthMethod.Basic => "Basic",
        ProxyAuthMethod.SocksUserPass => "Yes",
        ProxyAuthMethod.Socks4UserId => "UserID",
        ProxyAuthMethod.NtlmRequired => "NTLM",
        _ => method.ToString()
    };

    private void ApplyProgress(ProgressSnapshot snap)
    {
        _statusLabel.Text = snap.Message;
        _countsLabel.Text = FormatCounts(snap);
        _btnExport.Enabled = snap.Status is not (SessionStatus.Running or SessionStatus.Stopping) && _rows.Count > 0;
        if (snap.Total > 0)
        {
            var pct = (int)Math.Round(100.0 * snap.Completed / snap.Total);
            _progress.Value = Math.Clamp(pct, 0, 100);
            _percentLabel.Text = pct + " %";
        }
        var busy = snap.Status is SessionStatus.Running or SessionStatus.Stopping;
        SetBusy(busy);
        UpdateTitle(snap.UniqueProxies, snap.Alive);
    }

    private string FormatCounts(ProgressSnapshot snap)
    {
        if (snap.Kind == SessionKind.Scraping)
            return $"Unique: {snap.UniqueProxies}";

        if (_socksRadio.Checked)
        {
            var alive = snap.Socks4 + snap.Socks5 + snap.Socks4And5;
            return $"Alive {alive}  •  S4 {snap.Socks4}  •  S5 {snap.Socks5}  •  S4/5 {snap.Socks4And5}";
        }

        return $"Alive {snap.Alive}  •  Elite {snap.Elite}  •  Anon {snap.Anonymous}  •  Transp {snap.Transparent}";
    }

    private void SetBusy(bool busy)
    {
        _btnLoad.Enabled = !busy;
        _btnScrape.Enabled = !busy;
        _btnCheck.Enabled = !busy;
        _btnExport.Enabled = !busy && _rows.Count > 0;
        _btnSettings.Enabled = !busy;
        _btnStop.Enabled = busy;
        _httpRadio.Enabled = !busy;
        _socksRadio.Enabled = !busy;
        _concurrency.Enabled = !busy;
        _timeoutSec.Enabled = !busy;
        _judgeBox.Enabled = !busy;
    }

    private void SetStatus(string text) => _statusLabel.Text = text;

    private void UpdateTitle(int? proxies = null, int? alive = null)
    {
        var p = proxies ?? _session?.Proxies.Count ?? 0;
        var a = alive ?? _rows.Count;
        Text = $"μProxy Tool 2.0 [{p} loaded / {a} alive]";
    }

    private void LoadProxies()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Text files|*.txt;*.csv;*.list|All files|*.*",
            Title = "Load proxy list"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        PersistSettingsFromUi();
        EnsureSession();
        var text = File.ReadAllText(dlg.FileName);
        var n = _session!.LoadProxiesFromText(text);
        SetStatus($"Loaded {n} unique proxies from file. Total: {_session.Proxies.Count}");
        UpdateTitle();
    }

    private async Task RunScrapeAsync()
    {
        PersistSettingsFromUi();
        InitGeoIp();
        EnsureSession();

        var loader = new SourceLoader();
        var path = _socksRadio.Checked ? _settings.SocksSourcesPath : _settings.HttpSourcesPath;
        var sources = loader.LoadUrls(path);
        if (sources.Count == 0)
        {
            MessageBox.Show(
                $"No source URLs found.\nAdd HTTP(S) URLs to:\n{path}",
                "μProxy Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            await _session!.StartScrapeAsync(sources, _socksRadio.Checked);
            SetStatus($"Scrape {_session.Status}. Proxies: {_session.Proxies.Count}");
            if (_settings.AutoCheckAfterScrape && _session.Proxies.Count > 0 &&
                _session.Status == SessionStatus.Completed)
            {
                await RunCheckAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Scrape error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            UpdateTitle();
        }
    }

    private async Task RunCheckAsync()
    {
        PersistSettingsFromUi();
        InitGeoIp();
        // Recreate session so it picks up a freshly resolved GeoIP database.
        RecreateSessionPreservingState();
        EnsureSession();

        if (_session!.Proxies.Count == 0)
        {
            MessageBox.Show("Please load or scrape proxies first.", "μProxy Tool",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_rows.Count > 0)
        {
            if (MessageBox.Show("Clear current results before checking?", "μProxy Tool",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
        }

        _rows.Clear();
        _btnExport.Enabled = false;
        SetBusy(true);
        try
        {
            await _session.StartCheckAsync(_socksRadio.Checked);
            MessageBox.Show($"Proxy checking {_session.Status}!", "μProxy Tool",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Check error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            UpdateTitle();
        }
    }

    private void RecreateSessionPreservingState()
    {
        if (_session is null)
            return;
        var proxies = _session.Proxies.ToList();
        _ = _session.DisposeAsync();
        _session = null;
        EnsureSession();
        _session!.ClearProxies();
        foreach (var p in proxies)
            _session.Proxies.Add(p);
    }

    private void ExportResults()
    {
        EnsureSession();
        var results = _session!.Results.ToList();
        if (results.Count == 0)
        {
            MessageBox.Show("No checked proxies to export.", "Export",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var form = new ExportForm(results);
        form.ShowDialog(this);
    }

    private void OpenSettings()
    {
        PersistSettingsFromUi();
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            ApplySettingsToUi();
            ResolvePaths();
            InitGeoIp();
            ReportGeoIpStatus();
            _settings.Save(_settingsPath);
            if (_session is not null)
            {
                var proxies = _session.Proxies.ToList();
                var results = _session.Results.ToList();
                _ = _session.DisposeAsync();
                _session = null;
                EnsureSession();
                _session!.ClearProxies();
                foreach (var p in proxies)
                    _session.Proxies.Add(p);
                foreach (var r in results)
                    _session.Results.Add(r);
            }
        }
    }

    private void OpenSecretScanner()
    {
        PersistSettingsFromUi();
        using var form = new SecretScanForm(_settings, GetLoadedProxiesText);
        form.ShowDialog(this);
    }

    private string? GetLoadedProxiesText()
    {
        if (_session is null || _session.Proxies.Count == 0)
            return null;
        return string.Join(Environment.NewLine,
            _session.Proxies.Select(p => p.ToExportString(includeCredentials: true)));
    }

    private void SetSystemProxyOptIn()
    {
        if (!_proxyManager.IsSupported)
        {
            MessageBox.Show("System proxy is only available on Windows.", "μProxy Tool",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_grid.CurrentRow?.DataBoundItem is not ResultRow row)
        {
            MessageBox.Show("Select an alive proxy in the list first.", "μProxy Tool",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var warn = MessageBox.Show(
            "WARNING — privacy & connectivity\n\n" +
            $"This will set your Windows (WinINET) system proxy to:\n  {row.Proxy}\n\n" +
            "• Your browser/apps using system proxy will send traffic through this public proxy.\n" +
            "• The proxy operator may see your destinations.\n" +
            "• Your previous settings will be saved and can be restored with Emergency Reset.\n\n" +
            "Continue?",
            "Set System Proxy — Opt-in",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (warn != DialogResult.Yes)
            return;

        try
        {
            _proxyManager.SetProxyOptIn(row.Proxy);
            Text = $"μProxy Tool 2.0 [{row.Proxy} — system proxy ACTIVE]";
            SetStatus($"System proxy set to {row.Proxy}. Use Emergency Reset to restore.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to set proxy: " + ex.Message, "μProxy Tool",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void EmergencyReset()
    {
        if (!_proxyManager.IsSupported)
            return;

        try
        {
            if (_proxyManager.HasPendingRestore)
            {
                _proxyManager.Restore();
                MessageBox.Show("Original Windows proxy settings restored.", "μProxy Tool",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                var snap = _proxyManager.Capture();
                snap.ProxyEnable = 0;
                snap.ProxyServer = "";
                _proxyManager.SaveBackup(snap);
                _proxyManager.Restore(snap);
                _proxyManager.ClearBackup();
                MessageBox.Show("System proxy disabled (no prior backup found).", "μProxy Tool",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            UpdateTitle();
            SetStatus("System proxy reset.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Reset failed: " + ex.Message, "μProxy Tool",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CopySelected()
    {
        var lines = _grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(r => r.DataBoundItem as ResultRow)
            .Where(r => r is not null)
            .Select(r => r!.Proxy)
            .ToList();
        if (lines.Count > 0)
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
    }

    private sealed class ResultRow
    {
        public string Proxy { get; set; } = "";
        public string Country { get; set; } = "";
        public string Anonymity { get; set; } = "";
        public string Protocol { get; set; } = "";
        public int ConnectMs { get; set; }
        public int LatencyMs { get; set; }
        public string Auth { get; set; } = "";
        public string Detail { get; set; } = "";
        public AnonymityLevel AnonLevel { get; set; } = AnonymityLevel.Unknown;
    }
}
