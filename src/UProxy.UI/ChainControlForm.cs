using System.ComponentModel;
using System.Net.Http;
using UProxy.Core.Chaining;
using UProxy.Core.Config;
using UProxy.Core.Gateway;
using UProxy.Core.Models;
using UProxy.Core.Parsing;
using UProxy.Core.Persistence;
using UProxy.Core.Windows;

namespace UProxy.UI;

/// <summary>
/// Proxy Chains control: local HTTP/SOCKS gateway status, Fixed Chain, and Smart Pool.
/// System proxy (WinINET) is gateway-only — never points at remote public proxies.
/// </summary>
public sealed class ChainControlForm : Form
{
    private readonly AppSettings _settings;
    private readonly string _settingsPath;
    private readonly Func<IReadOnlyList<ProxyCheckResult>> _getAliveResults;
    private readonly ChainGatewayHost _gateway;
    private readonly ChainManager _manager;
    private readonly RoutingUiState _routing;
    private readonly AppDataLayout _layout;
    private readonly ProtectedCredentialStore _credentials;
    private readonly ChainProfileStore _profiles;
    private readonly PoolStore _pools;
    private readonly WindowsProxyManager _winProxy = new();
    private bool _busy;

    private readonly Label _lblMode = new() { AutoSize = true };
    private readonly Label _lblHttp = new() { AutoSize = true };
    private readonly Label _lblSocks = new() { AutoSize = true };
    private readonly Label _lblWinInet = new() { AutoSize = true };
    private readonly Label _lblRouting = new() { AutoSize = true, MaximumSize = new Size(720, 0) };
    private readonly Label _lblState = new() { AutoSize = true };
    private readonly Label _lblHops = new() { AutoSize = true, MaximumSize = new Size(680, 0) };
    private readonly Label _lblHint = new()
    {
        AutoSize = true,
        ForeColor = Color.FromArgb(120, 70, 20),
        MaximumSize = new Size(720, 0)
    };
    private readonly Label _lblBanner = new()
    {
        AutoSize = true,
        MaximumSize = new Size(720, 0),
        Visible = false,
        Margin = new Padding(0, 4, 0, 0)
    };
    private readonly Button _btnStart = new() { Text = "Start", AutoSize = true };
    private readonly Button _btnStop = new() { Text = "Stop", AutoSize = true };
    private readonly Button _btnRestore = new() { Text = "Restore Windows Proxy", AutoSize = true };
    private readonly Button _btnExitIp = new() { Text = "Check Exit IP", AutoSize = true };
    private readonly Button _btnCopyHttp = new() { Text = "Copy HTTP", AutoSize = true };
    private readonly Button _btnCopySocks = new() { Text = "Copy SOCKS", AutoSize = true };

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ListBox _fixedHops = new() { IntegralHeight = false, Dock = DockStyle.Fill };
    private readonly TextBox _fixedName = new() { Dock = DockStyle.Fill };
    private readonly ListBox _poolCandidates = new() { IntegralHeight = false, Dock = DockStyle.Fill };
    private readonly TextBox _poolName = new() { Dock = DockStyle.Fill, Text = "default-pool" };
    private readonly ComboBox _poolEligibility = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill,
        IntegralHeight = false
    };
    private readonly ComboBox _poolMode = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill,
        IntegralHeight = false
    };

    private Button? _btnStartStrict;
    private System.Windows.Forms.Timer? _liveTimer;

    private readonly BindingList<HopItem> _fixedItems = [];
    private readonly BindingList<PoolItem> _poolItems = [];

    public ChainControlForm(
        AppSettings settings,
        Func<IReadOnlyList<ProxyCheckResult>> getAliveResults,
        ChainGatewayHost gateway,
        ChainManager manager,
        RoutingUiState routing,
        AppDataLayout? layout = null,
        ChainProfileStore? profiles = null,
        PoolStore? pools = null,
        ProtectedCredentialStore? credentials = null,
        string? settingsPath = null,
        IReadOnlyList<ProxyCheckResult>? seedFixed = null,
        IReadOnlyList<ProxyCheckResult>? seedPool = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _getAliveResults = getAliveResults ?? throw new ArgumentNullException(nameof(getAliveResults));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));

        _layout = layout ?? new AppDataLayout();
        _layout.EnsureDirectories();
        _credentials = credentials ?? new ProtectedCredentialStore(_layout);
        _profiles = profiles ?? new ChainProfileStore(_layout, _credentials);
        _pools = pools ?? new PoolStore(_layout, _credentials);
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "uProxyTool",
            "settings.json");

        Text = "Proxy Chains";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        MinimumSize = new Size(820, 520);
        ClientSize = new Size(880, 600);
        Font = new Font("Segoe UI", 9f);
        BackColor = Color.White;
        ShowInTaskbar = false;

        _fixedHops.DataSource = _fixedItems;
        _poolCandidates.DataSource = _poolItems;
        _fixedName.Text = "strict-chain";

        BuildUi();
        WireEvents();

        var seededFixed = seedFixed is { Count: > 0 };
        var seededPool = seedPool is { Count: > 0 };

        if (seededFixed)
        {
            foreach (var r in seedFixed!)
                TryAddFixed(ResultToHop(r));
        }

        if (seededPool)
        {
            foreach (var r in seedPool!.Where(IsEliteAlive))
                TryAddPool(ResultToCandidate(r));
        }

        // Do not wipe context-menu seeds with the last saved profile.
        if (!seededFixed && !seededPool)
            TryLoadActiveProfile();

        RefreshStatus();
    }

    /// <summary>
    /// Compatibility overload until MainForm passes a shared <see cref="RoutingUiState"/>.
    /// </summary>
    public ChainControlForm(
        AppSettings settings,
        Func<IReadOnlyList<ProxyCheckResult>> getAliveResults,
        ChainGatewayHost gateway,
        ChainManager manager,
        AppDataLayout? layout = null,
        ChainProfileStore? profiles = null,
        PoolStore? pools = null,
        ProtectedCredentialStore? credentials = null,
        string? settingsPath = null,
        IReadOnlyList<ProxyCheckResult>? seedFixed = null,
        IReadOnlyList<ProxyCheckResult>? seedPool = null)
        : this(
            settings,
            getAliveResults,
            gateway,
            manager,
            new RoutingUiState(),
            layout,
            profiles,
            pools,
            credentials,
            settingsPath,
            seedFixed,
            seedPool)
    {
    }

    private void BuildUi()
    {
        StyleButton(_btnStart);
        StyleButton(_btnStop);
        StyleButton(_btnRestore);
        StyleButton(_btnExitIp);
        StyleButton(_btnCopyHttp);
        StyleButton(_btnCopySocks);
        _btnStart.Font = new Font(Font, FontStyle.Bold);
        _btnCopyHttp.MinimumSize = new Size(72, 28);
        _btnCopySocks.MinimumSize = new Size(72, 28);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = new Padding(0, 0, 0, 8)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        header.Controls.Add(_lblMode, 0, header.RowCount++);
        header.Controls.Add(_lblHttp, 0, header.RowCount++);
        header.Controls.Add(_lblSocks, 0, header.RowCount++);
        header.Controls.Add(_lblWinInet, 0, header.RowCount++);
        header.Controls.Add(_lblRouting, 0, header.RowCount++);
        header.Controls.Add(_lblState, 0, header.RowCount++);
        header.Controls.Add(_lblHops, 0, header.RowCount++);
        header.Controls.Add(_lblHint, 0, header.RowCount++);
        header.Controls.Add(_lblBanner, 0, header.RowCount++);

        var headerBtns = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 6, 0, 0),
            Padding = new Padding(0)
        };
        headerBtns.Controls.Add(_btnStart);
        headerBtns.Controls.Add(_btnStop);
        headerBtns.Controls.Add(_btnCopyHttp);
        headerBtns.Controls.Add(_btnCopySocks);
        headerBtns.Controls.Add(_btnRestore);
        headerBtns.Controls.Add(_btnExitIp);
        header.Controls.Add(headerBtns, 0, header.RowCount++);

        _tabs.TabPages.Add(BuildFixedTab());
        _tabs.TabPages.Add(BuildPoolTab());

        var note = new Label
        {
            Text = "Does not route every Windows app and does not route UDP.",
            AutoSize = true,
            ForeColor = Color.FromArgb(120, 70, 20),
            Margin = new Padding(0, 8, 0, 0)
        };

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_tabs, 0, 1);
        root.Controls.Add(note, 0, 2);
        Controls.Add(root);
    }

    private TabPage BuildFixedTab()
    {
        var page = new TabPage("Fixed Chain");
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(8)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SideColumnWidth));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var side = BuildSidePanel();
        var addSession = MakeSideButton("Add from session");
        var paste = MakeSideButton("Paste ordered list");
        var remove = MakeSideButton("Remove");
        var up = MakeSideButton("Up");
        var down = MakeSideButton("Down");
        var save = MakeSideButton("Save");
        var test = MakeSideButton("Test chain");
        _btnStartStrict = MakeSideButton("Start Strict");

        addSession.Click += (_, _) => AddFixedFromSession();
        paste.Click += (_, _) => PasteFixedOrdered();
        remove.Click += (_, _) => RemoveSelectedFixed();
        up.Click += (_, _) => MoveFixed(-1);
        down.Click += (_, _) => MoveFixed(1);
        save.Click += (_, _) => SaveFixedProfile();
        test.Click += async (_, _) => await TestFixedAsync();
        _btnStartStrict.Click += async (_, _) => await StartStrictAsync();

        side.Controls.AddRange([addSession, paste, remove, up, down, save, test, _btnStartStrict]);

        var nameRow = BuildNameRow(_fixedName);

        root.Controls.Add(_fixedHops, 0, 0);
        root.Controls.Add(side, 1, 0);
        root.SetRowSpan(side, 2);
        root.Controls.Add(nameRow, 0, 1);

        page.Controls.Add(root);
        return page;
    }

    private TabPage BuildPoolTab()
    {
        var page = new TabPage("Smart Pool");
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(8)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SideColumnWidth));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var side = BuildSidePanel();
        var importAlive = MakeSideButton("Import from session");
        var paste = MakeSideButton("Paste candidates");
        var remove = MakeSideButton("Remove");
        var refreshBadges = MakeSideButton("Refresh badges");
        var removeFailed = MakeSideButton("Remove failed");
        var save = MakeSideButton("Save pool");

        importAlive.Click += (_, _) => ImportPoolFromSession();
        paste.Click += (_, _) => PastePool();
        remove.Click += (_, _) => RemoveSelectedPool();
        refreshBadges.Click += (_, _) => RefreshPoolBadges();
        removeFailed.Click += (_, _) => RemoveFailedPoolItems();
        save.Click += (_, _) => SavePool();

        side.Controls.AddRange([importAlive, paste, remove, refreshBadges, removeFailed, save]);

        _poolEligibility.Items.AddRange([
            "Elite only (default)",
            "Elite + Anonymous",
            "Any alive"
        ]);
        _poolEligibility.SelectedIndex = 0;

        _poolMode.Items.AddRange([
            "Fast failover",
            "Auto 2-hop"
        ]);
        _poolMode.SelectedIndex = 0;

        var options = BuildPoolOptionsPanel();
        var nameRow = BuildNameRow(_poolName);

        root.Controls.Add(_poolCandidates, 0, 0);
        root.Controls.Add(side, 1, 0);
        root.SetRowSpan(side, 3);
        root.Controls.Add(options, 0, 1);
        root.Controls.Add(nameRow, 0, 2);

        page.Controls.Add(root);
        return page;
    }

    private TableLayoutPanel BuildPoolOptionsPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, 8, 0, 0),
            Padding = new Padding(0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var eligibilityLabel = new Label
        {
            Text = "Eligibility:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 6, 10, 0),
            UseMnemonic = false
        };
        var modeLabel = new Label
        {
            Text = "Mode:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 6, 10, 0),
            UseMnemonic = false
        };

        _poolEligibility.Margin = new Padding(0, 2, 0, 2);
        _poolMode.Margin = new Padding(0, 2, 0, 2);

        panel.Controls.Add(eligibilityLabel, 0, 0);
        panel.Controls.Add(_poolEligibility, 1, 0);
        panel.Controls.Add(modeLabel, 0, 1);
        panel.Controls.Add(_poolMode, 1, 1);
        return panel;
    }

    /// <summary>Fixed side-rail width so long action buttons are never clipped.</summary>
    private const int SideColumnWidth = 200;

    private static FlowLayoutPanel BuildSidePanel() =>
        new()
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 0, 0, 0),
            Margin = new Padding(0)
        };

    private static TableLayoutPanel BuildNameRow(TextBox nameBox)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 8, 0, 0),
            Padding = new Padding(0)
        };
        // AutoSize label column — Absolute widths wrap "Name:" into "Na"/"me:" under DPI.
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = "Name:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 6, 10, 0),
            UseMnemonic = false
        };
        nameBox.Dock = DockStyle.Fill;
        nameBox.Margin = new Padding(0, 2, 0, 2);
        nameBox.MinimumSize = new Size(120, 0);

        row.Controls.Add(label, 0, 0);
        row.Controls.Add(nameBox, 1, 0);
        return row;
    }

    private void WireEvents()
    {
        _btnStart.Click += async (_, _) => await StartFromHeaderAsync();
        _btnStop.Click += async (_, _) => await StopGatewayAsync();
        _btnRestore.Click += (_, _) => RestoreWindowsProxy();
        _btnExitIp.Click += async (_, _) => await CheckExitIpAsync();
        _btnCopyHttp.Click += (_, _) => CopyGatewayEndpoint(_gateway.HttpPort, "HTTP");
        _btnCopySocks.Click += (_, _) => CopyGatewayEndpoint(_gateway.SocksPort, "SOCKS");
        _tabs.SelectedIndexChanged += (_, _) => RefreshStatus();
        _fixedItems.ListChanged += (_, _) => RefreshStatus();
        _poolItems.ListChanged += (_, _) => RefreshStatus();
        _poolEligibility.SelectedIndexChanged += (_, _) => RefreshStatus();
        _poolMode.SelectedIndexChanged += (_, _) => RefreshStatus();
        _routing.Changed += () =>
        {
            if (IsHandleCreated && !IsDisposed)
            {
                try { BeginInvoke(RefreshStatus); }
                catch (ObjectDisposedException) { /* closing */ }
            }
        };
        Shown += (_, _) =>
        {
            EnsureLiveTimer();
            _liveTimer!.Start();
            RefreshStatus();
        };
        FormClosed += (_, _) =>
        {
            if (_liveTimer is not null)
            {
                _liveTimer.Stop();
                _liveTimer.Tick -= OnLiveTimerTick;
                _liveTimer.Dispose();
                _liveTimer = null;
            }
        };
        FormClosing += OnFormClosing;
    }

    private void EnsureLiveTimer()
    {
        if (_liveTimer is not null)
            return;
        _liveTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _liveTimer.Tick += OnLiveTimerTick;
    }

    private void OnLiveTimerTick(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        // Always tick; heavy refresh only while the gateway is running.
        if (!_gateway.IsRunning)
            return;

        RefreshStatus();
        RefreshPoolBadges();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_gateway.IsRunning)
            return;

        var result = MessageBox.Show(this,
            "The local gateway is still running.\n\n" +
            "Yes — Keep running and close this window\n" +
            "No — Stop the gateway and close\n" +
            "Cancel — Stay on this window",
            "Proxy Chains",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);

        if (result == DialogResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (result == DialogResult.No)
        {
            try
            {
                _gateway.StopAsync().GetAwaiter().GetResult();
                _routing.SetOff();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Stop failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                e.Cancel = true;
            }
        }
        // Yes = keep gateway running while closing the dialog.
    }

    private void CopyGatewayEndpoint(int port, string label)
    {
        var text = $"127.0.0.1:{port}";
        try
        {
            Clipboard.SetText(text);
            ShowBanner($"Copied {label} {text}", success: true);
        }
        catch (Exception ex)
        {
            ShowBanner($"Copy failed: {ex.Message}", success: false);
        }
    }

    private void ShowBanner(string message, bool success)
    {
        _lblBanner.Text = message;
        _lblBanner.ForeColor = success
            ? Color.FromArgb(20, 110, 50)
            : Color.FromArgb(160, 40, 40);
        _lblBanner.Visible = true;
    }

    private void ClearBanner()
    {
        _lblBanner.Visible = false;
        _lblBanner.Text = "";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        if (busy)
        {
            _btnStart.Enabled = false;
            _btnStop.Enabled = false;
            if (_btnStartStrict is not null)
                _btnStartStrict.Enabled = false;
        }
        else
        {
            RefreshStatus();
        }
    }

    private static Button MakeSideButton(string text)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = false,
            Size = new Size(SideColumnWidth - 16, 34),
            Margin = new Padding(0, 0, 0, 6),
            Padding = new Padding(8, 4, 8, 4),
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        };
        return b;
    }

    private static void StyleButton(Button b)
    {
        b.AutoSize = true;
        b.Padding = new Padding(10, 5, 10, 5);
        b.MinimumSize = new Size(88, 32);
        b.Margin = new Padding(0, 0, 8, 0);
    }

    private void RefreshStatus()
    {
        var running = _gateway.IsRunning;
        var profile = running ? _manager.ActiveProfile : null;
        var mode = profile?.Mode switch
        {
            ChainMode.StrictMultiHop => "Strict multi-hop",
            ChainMode.FastFailover => "Fast failover",
            _ => "—"
        };
        if (!running)
            mode = "Stopped";

        var httpPort = running ? _gateway.HttpPort : _settings.ChainHttpPort;
        var socksPort = running ? _gateway.SocksPort : _settings.ChainSocksPort;
        var autoRouting = running && _gateway.SystemProxyActive;

        _lblMode.Text = $"Mode: {mode}";
        _lblHttp.Text = $"HTTP gateway: 127.0.0.1:{httpPort}";
        _lblSocks.Text = $"SOCKS gateway: 127.0.0.1:{socksPort}";
        _lblWinInet.Text = autoRouting
            ? $"Automatic Windows routing: ON — system proxy → 127.0.0.1:{httpPort} (HTTP apps)"
            : "Automatic Windows routing: OFF — point apps at the ports above, or enable system proxy on Start";
        _lblRouting.Text = _routing.Summary;
        _lblState.Text = running
            ? $"Chain state: {_manager.State}"
            : "Chain state: Stopped";

        var hops = running ? _manager.GetActiveHops() : Array.Empty<ProxyHop>();
        _lblHops.Text = FormatActiveHops(hops, running);

        EnsureLiveTimer();
        if (_liveTimer is { Enabled: false } && IsHandleCreated)
            _liveTimer.Start();

        var canStrict = _fixedItems.Count > 0;
        var canPoolStart = PreferTwoHopMode()
            ? _poolItems.Count >= 2
            : _poolItems.Count > 0;
        var canHeaderStart = PreferPoolTab() ? canPoolStart : canStrict;

        if (!_busy)
        {
            if (running)
            {
                _btnStart.Text = "Apply changes";
            }
            else if (PreferPoolTab())
            {
                _btnStart.Text = PreferTwoHopMode()
                    ? "Start auto 2-hop"
                    : "Start fast failover";
            }
            else
            {
                _btnStart.Text = $"Start {_fixedItems.Count}-hop chain";
            }

            _btnStart.Enabled = canHeaderStart;
            _btnStop.Enabled = running;
            _btnExitIp.Enabled = running && hops.Count > 0;

            if (_btnStartStrict is not null)
            {
                _btnStartStrict.Text = running ? "Apply Strict" : "Start Strict";
                _btnStartStrict.Enabled = canStrict;
            }
        }

        if (!running && canHeaderStart)
        {
            _lblHint.Text = PreferPoolTab()
                ? PreferTwoHopMode()
                    ? "Pool loaded — Start searches an entry→exit pair from candidates matching the eligibility policy."
                    : "Pool loaded — Start runs Fast Failover using candidates matching the eligibility policy."
                : "Hops loaded — click Start to run the N-hop chain.";
            _lblHint.Visible = true;
        }
        else if (running)
        {
            _lblHint.Text = "Gateway running. Closing this window can keep it active — use Stop to shut it down.";
            _lblHint.Visible = true;
        }
        else
        {
            _lblHint.Text = PreferPoolTab()
                ? "Smart Pool keeps all candidates; Start filters by eligibility policy. Choose mode (Fast failover / Auto 2-hop), then Start. Paste adds unchecked entries."
                : "Add hops on Fixed Chain, then click Start.";
            _lblHint.Visible = true;
        }
    }

    private string FormatActiveHops(IReadOnlyList<ProxyHop> hops, bool running)
    {
        if (hops.Count == 0)
            return "Active hops: (none)";

        if (!running)
            return "Active hops: " + string.Join(" → ", hops.Select(h => $"{h.Kind} {h.Endpoint}"));

        var parts = new List<string>(hops.Count);
        for (var i = 0; i < hops.Count; i++)
        {
            var h = hops[i];
            var health = _manager.Health.IsInCooldown(h)
                ? "cooldown"
                : _manager.Health.IsHealthy(h)
                    ? "healthy"
                    : "degraded";
            parts.Add($"{i + 1}. {h.Kind} {h.Endpoint} [{health}]");
        }

        return "Active hops: " + string.Join(" → ", parts);
    }

    private bool PreferPoolTab() => _tabs.SelectedIndex == 1;

    private bool PreferTwoHopMode() => _poolMode.SelectedIndex == 1;

    private async Task StartFromHeaderAsync()
    {
        if (PreferPoolTab())
        {
            if (_poolItems.Count == 0)
            {
                MessageBox.Show(this,
                    "Add at least one Smart Pool candidate, then click Start.",
                    "Proxy Chains",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (PreferTwoHopMode())
                await StartPrivacyTwoHopAsync().ConfigureAwait(true);
            else
                await StartFastFailoverAsync().ConfigureAwait(true);
            return;
        }

        if (_fixedItems.Count == 0)
        {
            MessageBox.Show(this,
                "Add at least one Fixed Chain hop, then click Start.",
                "Proxy Chains",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        await StartStrictAsync().ConfigureAwait(true);
    }

    private void TryLoadActiveProfile()
    {
        var id = _settings.ActiveChainProfileId;
        if (string.IsNullOrWhiteSpace(id))
            return;

        foreach (var name in _profiles.ListNames())
        {
            try
            {
                var p = _profiles.Load(name);
                if (p is null)
                    continue;
                var match = string.Equals(p.Id.ToString("N"), id, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(p.Id.ToString(), id, StringComparison.OrdinalIgnoreCase);
                if (!match)
                    continue;

                _fixedName.Text = p.Name;
                _fixedItems.Clear();
                foreach (var h in p.Hops)
                    _fixedItems.Add(new HopItem(h));

                if (!string.IsNullOrWhiteSpace(p.CandidatePoolId))
                {
                    _poolName.Text = p.CandidatePoolId;
                    var pool = _pools.Load(p.CandidatePoolId);
                    if (pool is not null)
                    {
                        _poolItems.Clear();
                        foreach (var c in pool)
                            TryAddPool(c);
                    }
                }

                break;
            }
            catch
            {
                // ignore corrupt profile
            }
        }
    }

    private void AddFixedFromSession()
    {
        var alive = _getAliveResults().Where(r => r.IsAlive).ToList();
        if (alive.Count == 0)
        {
            MessageBox.Show(this, "No alive proxies in the current session.", "Proxy Chains",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new PickProxiesDialog(alive, "Add to Fixed Chain");
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        foreach (var r in dlg.Selected)
            TryAddFixed(ResultToHop(r));
    }

    private void PasteFixedOrdered()
    {
        var text = Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(text))
            return;

        // Preserve paste order (line-oriented).
        var added = 0;
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ProxyParser.TryParse(line.Trim(), out var proxy) || proxy is null)
                continue;
            TryAddFixed(ProxyHop.FromParsed(proxy));
            added++;
        }

        if (added == 0)
            MessageBox.Show(this, "No proxies found on the clipboard.", "Proxy Chains",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void RemoveSelectedFixed()
    {
        if (_fixedHops.SelectedItem is HopItem item)
            _fixedItems.Remove(item);
    }

    private void MoveFixed(int delta)
    {
        var idx = _fixedHops.SelectedIndex;
        if (idx < 0)
            return;
        var target = idx + delta;
        if (target < 0 || target >= _fixedItems.Count)
            return;
        var item = _fixedItems[idx];
        _fixedItems.RemoveAt(idx);
        _fixedItems.Insert(target, item);
        _fixedHops.SelectedIndex = target;
    }

    private void TryAddFixed(ProxyHop hop)
    {
        if (_fixedItems.Any(h => SameEndpoint(h.Hop, hop)))
            return;
        if (_fixedItems.Count >= ChainDialer.MaxHops)
        {
            MessageBox.Show(this,
                $"Fixed chains support at most {ChainDialer.MaxHops} hops.",
                "Proxy Chains",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _fixedItems.Add(new HopItem(hop));
    }

    private void ImportPoolFromSession()
    {
        var matching = _getAliveResults().Where(MatchesEligibilityPolicy).ToList();
        if (matching.Count == 0)
        {
            MessageBox.Show(this,
                $"No proxies in the current session match the eligibility policy ({CurrentPolicyLabel()}).",
                "Proxy Chains",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        using var dlg = new PickProxiesDialog(matching, "Add proxies to Smart Pool");
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        foreach (var r in dlg.Selected.Where(MatchesEligibilityPolicy))
            TryAddPool(ResultToCandidate(r));
    }

    private void PastePool()
    {
        var text = Clipboard.GetText();
        var parsed = ProxyParser.ExtractFromText(text);
        if (parsed.Count == 0)
        {
            MessageBox.Show(this, "No proxies found on the clipboard.", "Proxy Chains",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var sessionByEndpoint = SessionResultsByEndpoint();
        var added = 0;
        var skipped = 0;
        foreach (var p in parsed)
        {
            var hop = ProxyHop.FromParsed(p);
            var key = EndpointKey(hop);
            PoolCandidate candidate;
            if (sessionByEndpoint.TryGetValue(key, out var matched) && matched.IsAlive)
                candidate = ResultToCandidate(matched);
            else if (TryFindSessionResult(hop, out var loose) && loose.IsAlive)
                candidate = ResultToCandidate(loose);
            else
                candidate = new PoolCandidate(hop);

            var before = _poolItems.Count;
            TryAddPool(candidate);
            if (_poolItems.Count > before)
                added++;
            else
                skipped++;
        }

        if (added == 0)
        {
            MessageBox.Show(this,
                skipped > 0
                    ? "No new proxies added (duplicates or empty paste)."
                    : "No proxies found to add.",
                "Proxy Chains",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        else if (skipped > 0)
        {
            ShowBanner($"Added {added} candidate(s). Skipped {skipped} duplicate(s).", success: true);
        }
        else
        {
            ShowBanner($"Added {added} candidate(s) (unchecked if not in session).", success: true);
        }
    }

    private void RemoveSelectedPool()
    {
        if (_poolCandidates.SelectedItem is PoolItem item)
            _poolItems.Remove(item);
    }

    private void RefreshPoolBadges()
    {
        _poolItems.ResetBindings();
        _poolCandidates.Invalidate();
        _poolCandidates.Refresh();
    }

    private void RemoveFailedPoolItems()
    {
        var sessionByEndpoint = SessionResultsByEndpoint();
        var removed = 0;
        for (var i = _poolItems.Count - 1; i >= 0; i--)
        {
            var item = _poolItems[i];
            var hop = item.Candidate.Hop;
            var failed = false;

            if (sessionByEndpoint.TryGetValue(EndpointKey(hop), out var exact) && !exact.IsAlive)
                failed = true;
            else if (TryFindSessionResult(hop, out var loose) && !loose.IsAlive)
                failed = true;
            else if (ResolvePoolBadge(item.Candidate) == "[Failed]")
                failed = true;
            else if (_manager.Health.IsInCooldown(hop))
                failed = true;

            if (!failed)
                continue;

            _poolItems.RemoveAt(i);
            removed++;
        }

        if (removed == 0)
            ShowBanner("No failed or cooldown candidates to remove.", success: true);
        else
            ShowBanner($"Removed {removed} failed/cooldown candidate(s).", success: true);
    }

    private void TryAddPool(PoolCandidate candidate)
    {
        if (_poolItems.Any(c => SameEndpoint(c.Candidate.Hop, candidate.Hop)))
            return;
        _poolItems.Add(new PoolItem(candidate, ResolvePoolBadge));
    }

    private string ResolvePoolBadge(PoolCandidate candidate)
    {
        var hop = candidate.Hop;

        if (_manager.Health.IsInCooldown(hop))
            return "[Cooldown]";

        if (TryFindSessionResult(hop, out var matched))
        {
            if (!matched.IsAlive)
                return "[Failed]";
            if (matched.Anonymity == AnonymityLevel.Elite)
                return "[Elite]";
            if (matched.Anonymity == AnonymityLevel.Anonymous)
                return "[Anon]";
            return "[Alive]";
        }

        // Not in the current session set — prior check metadata means stale.
        if (candidate.LastChecked is not null)
            return "[Stale]";
        return "[Unchecked]";
    }

    private List<PoolCandidate> EligibleCandidates()
    {
        var list = new List<PoolCandidate>();
        foreach (var item in _poolItems)
        {
            if (!TryFindSessionResult(item.Candidate.Hop, out var matched))
                continue;
            if (!MatchesEligibilityPolicy(matched))
                continue;
            list.Add(ResultToCandidate(matched));
        }

        return list;
    }

    private bool MatchesEligibilityPolicy(ProxyCheckResult r)
    {
        if (!r.IsAlive)
            return false;

        return _poolEligibility.SelectedIndex switch
        {
            1 => r.Anonymity is AnonymityLevel.Elite or AnonymityLevel.Anonymous,
            2 => true,
            _ => r.Anonymity == AnonymityLevel.Elite
        };
    }

    private string CurrentPolicyLabel() =>
        _poolEligibility.SelectedIndex switch
        {
            1 => "Elite + Anonymous",
            2 => "Any alive",
            _ => "Elite only"
        };

    private static bool IsEliteAlive(ProxyCheckResult r) =>
        r.IsAlive && r.Anonymity == AnonymityLevel.Elite;

    private Dictionary<string, ProxyCheckResult> SessionResultsByEndpoint()
    {
        var map = new Dictionary<string, ProxyCheckResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _getAliveResults())
        {
            var key = EndpointKey(ResultToHop(r));
            map.TryAdd(key, r);
        }

        return map;
    }

    private bool TryFindSessionResult(ProxyHop hop, out ProxyCheckResult matched)
    {
        var exactKey = EndpointKey(hop);
        foreach (var r in _getAliveResults())
        {
            var resultHop = ResultToHop(r);
            if (string.Equals(EndpointKey(resultHop), exactKey, StringComparison.OrdinalIgnoreCase))
            {
                matched = r;
                return true;
            }
        }

        // Prefer kind/protocol match above; fall back to host:port.
        var hostPort = HostPortKey(hop);
        ProxyCheckResult? fallback = null;
        foreach (var r in _getAliveResults())
        {
            var resultHop = ResultToHop(r);
            if (!string.Equals(HostPortKey(resultHop), hostPort, StringComparison.OrdinalIgnoreCase))
                continue;

            if (resultHop.Kind == hop.Kind)
            {
                matched = r;
                return true;
            }

            fallback ??= r;
        }

        if (fallback is not null)
        {
            matched = fallback;
            return true;
        }

        matched = null!;
        return false;
    }

    private static string EndpointKey(ProxyHop hop)
    {
        var host = hop.Proxy.Host.Trim().TrimStart('[').TrimEnd(']').ToLowerInvariant();
        return $"{host}:{hop.Proxy.Port}:{hop.Kind}";
    }

    private static string HostPortKey(ProxyHop hop)
    {
        var host = hop.Proxy.Host.Trim().TrimStart('[').TrimEnd(']').ToLowerInvariant();
        return $"{host}:{hop.Proxy.Port}";
    }
    private void SaveFixedProfile()
    {
        var name = _fixedName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Enter a chain name.", "Proxy Chains",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_fixedItems.Count == 0)
        {
            MessageBox.Show(this, "Add at least one hop.", "Proxy Chains",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var profile = BuildFixedProfile(name);
            _profiles.Save(profile);
            PersistActiveProfileId(profile.Id);
            MessageBox.Show(this, $"Saved chain \"{name}\".", "Proxy Chains",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SavePool()
    {
        var name = _poolName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Enter a pool name.", "Proxy Chains",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            _pools.Save(name, _poolItems.Select(i => i.Candidate).ToList());
            MessageBox.Show(this, $"Saved pool \"{name}\".", "Proxy Chains",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task TestFixedAsync()
    {
        if (_fixedItems.Count == 0)
        {
            MessageBox.Show(this, "Add at least one hop to test.", "Proxy Chains",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_fixedItems.Count > ChainDialer.MaxHops)
        {
            MessageBox.Show(this,
                $"Fixed chains support at most {ChainDialer.MaxHops} hops.",
                "Proxy Chains",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        // Use a temporary manager so Test never mutates the live gateway chain.
        var profile = BuildFixedProfile(_fixedName.Text.Trim().Length > 0 ? _fixedName.Text.Trim() : "test");
        var tester = new ChainManager(new ChainDialer(TimeSpan.FromSeconds(20)), new ChainHealthTracker())
        {
            VerificationDestination = TestDestination()
        };
        tester.SwitchProfile(profile);

        UseWaitCursor = true;
        try
        {
            var dest = TestDestination();
            var ok = await tester.ValidateChainAsync(dest, CancellationToken.None).ConfigureAwait(true);
            MessageBox.Show(this,
                ok ? $"Chain OK → {dest}" : $"Chain failed → {dest}",
                "Test chain",
                MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Test chain", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            RefreshStatus();
        }
    }

    private async Task StartStrictAsync()
    {
        if (_fixedItems.Count == 0)
        {
            MessageBox.Show(this, "Add at least one hop.", "Proxy Chains",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_fixedItems.Count > ChainDialer.MaxHops)
        {
            MessageBox.Show(this,
                $"Fixed chains support at most {ChainDialer.MaxHops} hops.",
                "Proxy Chains",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var name = string.IsNullOrWhiteSpace(_fixedName.Text) ? "strict-chain" : _fixedName.Text.Trim();
        var profile = BuildFixedProfile(name);
        try
        {
            _profiles.Save(profile);
        }
        catch
        {
            // Start even if persistence fails.
        }

        await ApplyAndStartAsync(profile, pool: null).ConfigureAwait(true);
    }

    private async Task StartFastFailoverAsync()
    {
        var total = _poolItems.Count;
        var eligible = EligibleCandidates();
        if (eligible.Count == 0)
        {
            MessageBox.Show(this,
                $"0 of {total} eligible under current policy ({CurrentPolicyLabel()}).\n\n" +
                "Import matching proxies from the session, widen the eligibility policy, or wait until candidates are checked.",
                "Proxy Chains",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        ShowBanner(
            $"Using {eligible.Count} of {total} candidates (policy: {CurrentPolicyLabel()})",
            success: true);

        var poolName = string.IsNullOrWhiteSpace(_poolName.Text) ? "default-pool" : _poolName.Text.Trim();
        // Persist the full UI list; start only with policy-eligible entries.
        var allCandidates = _poolItems.Select(i => i.Candidate).ToList();
        try
        {
            _pools.Save(poolName, allCandidates);
        }
        catch
        {
            // continue
        }

        var hops = eligible.Select(c => c.Hop).ToList();
        var profile = new ProxyChainProfile(
            Guid.NewGuid(),
            poolName + "-failover",
            ChainMode.FastFailover,
            hops,
            poolName);

        try
        {
            _profiles.Save(profile);
        }
        catch
        {
            // continue
        }

        await ApplyAndStartAsync(profile, eligible).ConfigureAwait(true);
    }

    private async Task StartPrivacyTwoHopAsync()
    {
        var total = _poolItems.Count;
        var eligible = EligibleCandidates();
        if (eligible.Count < 2)
        {
            MessageBox.Show(this,
                eligible.Count == 0
                    ? $"0 of {total} eligible under current policy ({CurrentPolicyLabel()}).\n\n" +
                      "Need at least two matching candidates for Auto 2-hop."
                    : $"{eligible.Count} of {total} eligible under current policy ({CurrentPolicyLabel()}).\n\n" +
                      "Need at least two matching candidates for Auto 2-hop.",
                "Proxy Chains",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        ShowBanner(
            $"Using {eligible.Count} of {total} candidates (policy: {CurrentPolicyLabel()})",
            success: true);

        using var cts = new CancellationTokenSource();
        using var progress = new PrivacyPairProgressDialog(cts);
        progress.Show(this);
        SetBusy(true);
        (PoolCandidate entry, PoolCandidate exit)? chosen = null;
        try
        {
            var dest = TestDestination();
            // Probe on a throwaway manager so pair search never rewires the live gateway.
            var edgeTester = new ChainManager(new ChainDialer(TimeSpan.FromSeconds(12)), _manager.Health)
            {
                VerificationDestination = dest
            };
            chosen = await ChainSelectionPolicy.SelectAutoTwoHopPrivacyAsync(
                eligible,
                async (entry, exit, ct) =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var ok = await edgeTester.ValidateEdgeAsync(entry.Hop, exit.Hop, dest, ct)
                        .ConfigureAwait(false);
                    sw.Stop();
                    return new TwoHopEdgeResult(ok, Reliability: ok ? 1.0 : 0.0, E2eLatencyMs: (int)sw.ElapsedMilliseconds);
                },
                health: _manager.Health,
                timeBudget: TimeSpan.FromSeconds(45),
                maxConcurrency: 4,
                cancellationToken: cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Auto 2-hop chain", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally
        {
            try { progress.Close(); } catch { /* ignore */ }
            SetBusy(false);
        }

        if (cts.IsCancellationRequested)
            return;

        if (chosen is null)
        {
            MessageBox.Show(this, "No compatible entry→exit pair found.", "Auto 2-hop chain",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var (entry, exit) = chosen.Value;
        var profile = new ProxyChainProfile(
            Guid.NewGuid(),
            "privacy-2hop",
            ChainMode.StrictMultiHop,
            [entry.Hop, exit.Hop],
            string.IsNullOrWhiteSpace(_poolName.Text) ? null : _poolName.Text.Trim());

        try
        {
            _profiles.Save(profile);
        }
        catch
        {
            // continue
        }

        _fixedItems.Clear();
        _fixedItems.Add(new HopItem(entry.Hop));
        _fixedItems.Add(new HopItem(exit.Hop));
        _fixedName.Text = profile.Name;

        await ApplyAndStartAsync(profile, eligible).ConfigureAwait(true);
    }

    private async Task ApplyAndStartAsync(ProxyChainProfile profile, IReadOnlyList<PoolCandidate>? pool)
    {
        PersistActiveProfileId(profile.Id);

        var modeLabel = profile.Mode switch
        {
            ChainMode.StrictMultiHop => "Strict multi-hop",
            ChainMode.FastFailover => "Fast failover",
            _ => profile.Mode.ToString()
        };

        _routing.SetStarting(profile.Name, modeLabel);
        RefreshStatus();
        ClearBanner();
        ShowBanner("Starting gateway…", success: true);

        _manager.VerificationDestination = TestDestination();

        _gateway.HttpPort = _settings.ChainHttpPort;
        _gateway.SocksPort = _settings.ChainSocksPort;

        SetBusy(true);
        try
        {
            if (_gateway.IsRunning)
            {
                _gateway.SwitchProfile(profile, pool);
            }
            else
            {
                _manager.SwitchProfile(profile, pool);
                await _gateway.StartAsync(_manager, _settings.ChainEnableSystemProxy).ConfigureAwait(true);
            }

            _routing.SetTunnelUp(
                profile.Name,
                modeLabel,
                _gateway.SystemProxyActive,
                _gateway.HttpPort,
                _gateway.SocksPort);
            RefreshStatus();
            ShowBanner(
                $"Gateway up — HTTP 127.0.0.1:{_gateway.HttpPort}, SOCKS 127.0.0.1:{_gateway.SocksPort}. Verifying exit…",
                success: true);

            await VerifyExitAfterStartAsync(profile.Name, modeLabel).ConfigureAwait(true);
        }
        catch (PortInUseException ex)
        {
            _routing.SetOff();
            await HandlePortInUseAsync(ex, profile, pool).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _routing.SetOff();
            MessageBox.Show(this, ex.Message, "Start failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ShowBanner($"Start failed: {ex.Message}", success: false);
            RefreshStatus();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task VerifyExitAfterStartAsync(string? profileName, string? modeLabel)
    {
        string? directIp = null;
        string? exitIp = null;
        var auto = _gateway.SystemProxyActive;
        var http = _gateway.HttpPort;
        var socks = _gateway.SocksPort;

        try
        {
            directIp = await ExitIpChecker.CheckDirectAsync(
                _settings.ExitIpCheckUrl,
                CancellationToken.None).ConfigureAwait(true);

            ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext ctx, CancellationToken ct)
            {
                var dest = new ChainDestination(ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port);
                return new ValueTask<Stream>(_manager.ConnectAsync(dest, ct));
            }

            exitIp = await ExitIpChecker.CheckAsync(
                ConnectCallback,
                _settings.ExitIpCheckUrl,
                CancellationToken.None).ConfigureAwait(true);

            if (!string.IsNullOrWhiteSpace(directIp)
                && !string.IsNullOrWhiteSpace(exitIp)
                && !string.Equals(directIp, exitIp, StringComparison.OrdinalIgnoreCase))
            {
                _routing.SetVerified(exitIp, directIp, profileName, modeLabel, auto, http, socks);
                ShowBanner($"Verified — exit {exitIp} (direct {directIp}).", success: true);
            }
            else if (string.Equals(directIp, exitIp, StringComparison.OrdinalIgnoreCase))
            {
                _routing.SetNotVerified(
                    $"exit IP matches direct IP ({exitIp})",
                    exitIp,
                    directIp,
                    profileName,
                    modeLabel,
                    auto,
                    http,
                    socks);
                ShowBanner($"NOT VERIFIED — exit IP matches direct ({exitIp}).", success: false);
            }
            else
            {
                _routing.SetNotVerified(
                    "could not compare exit and direct IPs",
                    exitIp,
                    directIp,
                    profileName,
                    modeLabel,
                    auto,
                    http,
                    socks);
                ShowBanner("NOT VERIFIED — could not compare exit and direct IPs.", success: false);
            }
        }
        catch (Exception ex)
        {
            _routing.SetNotVerified(
                ex.Message,
                exitIp,
                directIp,
                profileName,
                modeLabel,
                auto,
                http,
                socks);
            ShowBanner($"NOT VERIFIED — {ex.Message}", success: false);
        }

        RefreshStatus();
    }

    private async Task HandlePortInUseAsync(
        PortInUseException ex,
        ProxyChainProfile profile,
        IReadOnlyList<PoolCandidate>? pool)
    {
        var suggested = ex.SuggestedPort;
        var msg = suggested is int s
            ? $"{ex.Message}\n\nUse suggested port {s} and save?\nOpen Settings to pick ports manually?\nOr Cancel."
            : $"{ex.Message}\n\nOpen Settings to pick a free port, or Cancel.";

        var result = MessageBox.Show(this, msg, "Port in use",
            suggested is not null ? MessageBoxButtons.YesNoCancel : MessageBoxButtons.RetryCancel,
            MessageBoxIcon.Warning);

        if (suggested is int port && result == DialogResult.Yes)
        {
            // Heuristic: if HTTP port collided, update HTTP; else SOCKS.
            if (ex.Port == _settings.ChainHttpPort || ex.Port == _gateway.HttpPort)
                _settings.ChainHttpPort = port;
            else
                _settings.ChainSocksPort = port;
            _settings.Save(_settingsPath);
            await ApplyAndStartAsync(profile, pool).ConfigureAwait(true);
            return;
        }

        if (result == DialogResult.No || (suggested is null && result == DialogResult.Retry))
        {
            using var settings = new SettingsForm(_settings);
            if (settings.ShowDialog(this) == DialogResult.OK)
            {
                _settings.Save(_settingsPath);
                await ApplyAndStartAsync(profile, pool).ConfigureAwait(true);
            }
        }
    }

    private async Task StopGatewayAsync()
    {
        SetBusy(true);
        try
        {
            await _gateway.StopAsync().ConfigureAwait(true);
            _routing.SetOff();
            ShowBanner("Gateway stopped.", success: true);
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Stop failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ShowBanner($"Stop failed: {ex.Message}", success: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RestoreWindowsProxy()
    {
        if (!_winProxy.IsSupported)
        {
            MessageBox.Show(this, "System proxy restore is only available on Windows.", "Proxy Chains",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            if (_gateway.IsRunning && _gateway.SystemProxyActive)
            {
                // Stopping the gateway restores WinINET when we enabled it.
                _gateway.StopAsync().GetAwaiter().GetResult();
                _routing.SetOff();
                RefreshStatus();
                ShowBanner("Gateway stopped and Windows proxy restored.", success: true);
                return;
            }

            if (_winProxy.HasPendingRestore)
            {
                _winProxy.Restore();
                ShowBanner("Original Windows proxy settings restored.", success: true);
            }
            else
            {
                MessageBox.Show(this,
                    "No Windows proxy backup is pending. Nothing to restore.\n\n" +
                    "A backup is created when the gateway enables system proxy.",
                    "Proxy Chains",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Restore failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task CheckExitIpAsync()
    {
        if (!_gateway.IsRunning || _manager.GetActiveHops().Count == 0)
        {
            MessageBox.Show(this, "Start a chain first.", "Exit IP",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext ctx, CancellationToken ct)
            {
                var dest = new ChainDestination(ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port);
                return new ValueTask<Stream>(_manager.ConnectAsync(dest, ct));
            }

            var ip = await ExitIpChecker.CheckAsync(
                ConnectCallback,
                _settings.ExitIpCheckUrl,
                CancellationToken.None).ConfigureAwait(true);

            ShowBanner($"Exit IP: {ip}", success: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Exit IP check failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            ShowBanner($"Exit IP check failed: {ex.Message}", success: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private ProxyChainProfile BuildFixedProfile(string name)
    {
        var existingId = Guid.TryParse(_settings.ActiveChainProfileId, out var gid) ? gid : Guid.NewGuid();
        // Prefer stable id when re-saving same name.
        foreach (var n in _profiles.ListNames())
        {
            try
            {
                var prev = _profiles.Load(n);
                if (prev is not null && string.Equals(prev.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    existingId = prev.Id;
                    break;
                }
            }
            catch { /* ignore */ }
        }

        return new ProxyChainProfile(
            existingId,
            name,
            ChainMode.StrictMultiHop,
            _fixedItems.Select(i => i.Hop).ToList(),
            string.IsNullOrWhiteSpace(_poolName.Text) ? null : _poolName.Text.Trim());
    }

    private void PersistActiveProfileId(Guid id)
    {
        _settings.ActiveChainProfileId = id.ToString("N");
        try
        {
            _settings.Save(_settingsPath);
        }
        catch
        {
            // non-fatal
        }
    }

    private ChainDestination TestDestination()
    {
        var url = string.IsNullOrWhiteSpace(_settings.ExitIpCheckUrl)
            ? "https://api.ipify.org"
            : _settings.ExitIpCheckUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new ChainDestination("api.ipify.org", 443);

        var port = uri.Port;
        if (port <= 0)
            port = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
        return new ChainDestination(uri.IdnHost, port);
    }

    private static ProxyHop ResultToHop(ProxyCheckResult r)
    {
        var protocol = r.ConfirmedProtocol != ProxyProtocol.Unknown ? r.ConfirmedProtocol : r.Proxy.Protocol;
        var proxy = r.Proxy with { Protocol = protocol };
        return ProxyHop.FromParsed(proxy);
    }

    private static PoolCandidate ResultToCandidate(ProxyCheckResult r) =>
        new(ResultToHop(r), r.Country, r.LatencyMs, SuccessRate: r.IsAlive ? 1.0 : 0.0, r.CheckedAt);

    private static bool SameEndpoint(ProxyHop a, ProxyHop b) =>
        string.Equals(
            a.Proxy.Host.Trim().TrimStart('[').TrimEnd(']'),
            b.Proxy.Host.Trim().TrimStart('[').TrimEnd(']'),
            StringComparison.OrdinalIgnoreCase)
        && a.Proxy.Port == b.Proxy.Port
        && a.Kind == b.Kind;

    private sealed class HopItem(ProxyHop hop)
    {
        public ProxyHop Hop { get; } = hop;
        public override string ToString() => $"{Hop.Kind}  {Hop.Endpoint}";
    }

    private sealed class PoolItem(PoolCandidate candidate, Func<PoolCandidate, string> badgeResolver)
    {
        public PoolCandidate Candidate { get; } = candidate;
        public override string ToString()
        {
            var c = Candidate;
            var badge = badgeResolver(c);
            var meta = c.Country is { Length: > 0 } ? $"  [{c.Country}]" : "";
            var lat = c.LatencyMs is int ms ? $"  {ms}ms" : "";
            return $"{badge}  {c.Hop.Kind}  {c.Hop.Endpoint}{meta}{lat}";
        }
    }

    private sealed class PrivacyPairProgressDialog : Form
    {
        public PrivacyPairProgressDialog(CancellationTokenSource cts)
        {
            Text = "Auto 2-hop chain";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(360, 110);
            Font = new Font("Segoe UI", 9f);

            var label = new Label
            {
                Text = "Searching for a compatible entry→exit pair…\n(up to 45 seconds)",
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 8, 12, 0)
            };
            var cancel = new Button
            {
                Text = "Cancel",
                AutoSize = true,
                Padding = new Padding(16, 6, 16, 6),
                MinimumSize = new Size(88, 32),
                Dock = DockStyle.Right,
                Margin = new Padding(0, 8, 12, 12)
            };
            cancel.Click += (_, _) =>
            {
                cancel.Enabled = false;
                cts.Cancel();
            };
            CancelButton = cancel;
            FormClosing += (_, _) =>
            {
                try { cts.Cancel(); } catch { /* ignore */ }
            };

            var buttons = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 48
            };
            buttons.Controls.Add(cancel);

            Controls.Add(label);
            Controls.Add(buttons);
        }
    }

    private sealed class PickProxiesDialog : Form
    {
        private readonly CheckedListBox _list = new() { CheckOnClick = true, IntegralHeight = false, Dock = DockStyle.Fill };
        public List<ProxyCheckResult> Selected { get; } = [];

        public PickProxiesDialog(IReadOnlyList<ProxyCheckResult> items, string title)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            MinimumSize = new Size(420, 360);
            ClientSize = new Size(480, 420);
            Font = new Font("Segoe UI", 9f);

            foreach (var r in items)
                _list.Items.Add(new PickItem(r), true);

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                WrapContents = false,
                Padding = new Padding(0, 8, 0, 0)
            };
            var ok = new Button
            {
                Text = "Add",
                DialogResult = DialogResult.OK,
                AutoSize = true,
                Padding = new Padding(16, 6, 16, 6),
                MinimumSize = new Size(88, 32)
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
                Padding = new Padding(16, 6, 16, 6),
                MinimumSize = new Size(88, 32),
                Margin = new Padding(0, 0, 8, 0)
            };
            ok.Click += (_, _) =>
            {
                Selected.Clear();
                foreach (PickItem item in _list.CheckedItems)
                    Selected.Add(item.Result);
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
                Padding = new Padding(10)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(_list, 0, 0);
            root.Controls.Add(buttons, 0, 1);
            Controls.Add(root);
        }

        private sealed class PickItem
        {
            public ProxyCheckResult Result { get; }
            public PickItem(ProxyCheckResult result) => Result = result;
            public override string ToString() =>
                $"{Result.ConfirmedProtocol}  {Result.Proxy.Endpoint}  [{Result.Country}]  {Result.LatencyMs}ms";
        }
    }
}
