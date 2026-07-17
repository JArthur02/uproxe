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
    private readonly AppDataLayout _layout;
    private readonly ProtectedCredentialStore _credentials;
    private readonly ChainProfileStore _profiles;
    private readonly PoolStore _pools;
    private readonly WindowsProxyManager _winProxy = new();

    private readonly Label _lblMode = new() { AutoSize = true };
    private readonly Label _lblHttp = new() { AutoSize = true };
    private readonly Label _lblSocks = new() { AutoSize = true };
    private readonly Label _lblWinInet = new() { AutoSize = true };
    private readonly Label _lblState = new() { AutoSize = true };
    private readonly Label _lblHops = new() { AutoSize = true, MaximumSize = new Size(680, 0) };
    private readonly Button _btnStop = new() { Text = "Stop", AutoSize = true };
    private readonly Button _btnRestore = new() { Text = "Restore Windows Proxy", AutoSize = true };
    private readonly Button _btnExitIp = new() { Text = "Check Exit IP", AutoSize = true };

    private readonly ListBox _fixedHops = new() { IntegralHeight = false, Dock = DockStyle.Fill };
    private readonly TextBox _fixedName = new() { Dock = DockStyle.Fill };
    private readonly ListBox _poolCandidates = new() { IntegralHeight = false, Dock = DockStyle.Fill };
    private readonly TextBox _poolName = new() { Dock = DockStyle.Fill, Text = "default-pool" };

    private readonly BindingList<HopItem> _fixedItems = [];
    private readonly BindingList<PoolItem> _poolItems = [];

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
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _getAliveResults = getAliveResults ?? throw new ArgumentNullException(nameof(getAliveResults));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));

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
        MinimumSize = new Size(720, 480);
        ClientSize = new Size(780, 560);
        Font = new Font("Segoe UI", 9f);
        BackColor = Color.White;
        ShowInTaskbar = false;

        _fixedHops.DataSource = _fixedItems;
        _poolCandidates.DataSource = _poolItems;
        _fixedName.Text = "strict-chain";

        BuildUi();
        WireEvents();

        if (seedFixed is { Count: > 0 })
        {
            foreach (var r in seedFixed)
                TryAddFixed(ResultToHop(r));
        }

        if (seedPool is { Count: > 0 })
        {
            foreach (var r in seedPool)
                TryAddPool(ResultToCandidate(r));
        }

        TryLoadActiveProfile();
        RefreshStatus();
    }

    private void BuildUi()
    {
        StyleButton(_btnStop);
        StyleButton(_btnRestore);
        StyleButton(_btnExitIp);

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
        header.Controls.Add(_lblState, 0, header.RowCount++);
        header.Controls.Add(_lblHops, 0, header.RowCount++);

        var headerBtns = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 6, 0, 0),
            Padding = new Padding(0)
        };
        headerBtns.Controls.Add(_btnStop);
        headerBtns.Controls.Add(_btnRestore);
        headerBtns.Controls.Add(_btnExitIp);
        header.Controls.Add(headerBtns, 0, header.RowCount++);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildFixedTab());
        tabs.TabPages.Add(BuildPoolTab());

        var note = new Label
        {
            Text = "TCP-only local gateway (HTTP + SOCKS5). Windows system proxy points at 127.0.0.1 HTTP only.",
            AutoSize = true,
            ForeColor = Color.FromArgb(90, 90, 90),
            Margin = new Padding(0, 8, 0, 0)
        };

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(tabs, 0, 1);
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
            RowCount = 3,
            Padding = new Padding(6)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var side = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Padding = new Padding(6, 0, 0, 0)
        };

        var addSession = MakeSideButton("Add from session");
        var paste = MakeSideButton("Paste ordered list");
        var remove = MakeSideButton("Remove");
        var up = MakeSideButton("Up");
        var down = MakeSideButton("Down");
        var save = MakeSideButton("Save");
        var test = MakeSideButton("Test chain");
        var start = MakeSideButton("Start Strict");

        addSession.Click += (_, _) => AddFixedFromSession();
        paste.Click += (_, _) => PasteFixedOrdered();
        remove.Click += (_, _) => RemoveSelectedFixed();
        up.Click += (_, _) => MoveFixed(-1);
        down.Click += (_, _) => MoveFixed(1);
        save.Click += (_, _) => SaveFixedProfile();
        test.Click += async (_, _) => await TestFixedAsync();
        start.Click += async (_, _) => await StartStrictAsync();

        side.Controls.AddRange([addSession, paste, remove, up, down, save, test, start]);

        var nameRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 6, 0, 0)
        };
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        nameRow.Controls.Add(new Label { Text = "Name:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 0);
        nameRow.Controls.Add(_fixedName, 1, 0);

        root.Controls.Add(_fixedHops, 0, 0);
        root.Controls.Add(side, 1, 0);
        root.SetColumnSpan(nameRow, 2);
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
            RowCount = 2,
            Padding = new Padding(6)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var side = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Padding = new Padding(6, 0, 0, 0)
        };

        var importAlive = MakeSideButton("Import alive from session");
        var paste = MakeSideButton("Paste");
        var remove = MakeSideButton("Remove");
        var save = MakeSideButton("Save pool");
        var startFail = MakeSideButton("Start Fast Failover");
        var startTwo = MakeSideButton("Start Privacy 2-hop");

        importAlive.Click += (_, _) => ImportPoolFromSession();
        paste.Click += (_, _) => PastePool();
        remove.Click += (_, _) => RemoveSelectedPool();
        save.Click += (_, _) => SavePool();
        startFail.Click += async (_, _) => await StartFastFailoverAsync();
        startTwo.Click += async (_, _) => await StartPrivacyTwoHopAsync();

        side.Controls.AddRange([importAlive, paste, remove, save, startFail, startTwo]);

        var nameRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 6, 0, 0)
        };
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        nameRow.Controls.Add(new Label { Text = "Name:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 0);
        nameRow.Controls.Add(_poolName, 1, 0);

        root.Controls.Add(_poolCandidates, 0, 0);
        root.Controls.Add(side, 1, 0);
        root.SetColumnSpan(nameRow, 2);
        root.Controls.Add(nameRow, 0, 1);

        page.Controls.Add(root);
        return page;
    }

    private void WireEvents()
    {
        _btnStop.Click += async (_, _) => await StopGatewayAsync();
        _btnRestore.Click += (_, _) => RestoreWindowsProxy();
        _btnExitIp.Click += async (_, _) => await CheckExitIpAsync();
        Shown += (_, _) => RefreshStatus();
    }

    private static Button MakeSideButton(string text)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(10, 5, 10, 5),
            MinimumSize = new Size(160, 32),
            Margin = new Padding(0, 0, 0, 6)
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
        var profile = _manager.ActiveProfile;
        var mode = profile?.Mode switch
        {
            ChainMode.StrictMultiHop => "Strict multi-hop",
            ChainMode.FastFailover => "Fast failover",
            _ => "—"
        };
        if (!running)
            mode = "Stopped";

        _lblMode.Text = $"Mode: {mode}";
        _lblHttp.Text = $"HTTP gateway: 127.0.0.1:{_gateway.HttpPort}  (settings {_settings.ChainHttpPort})";
        _lblSocks.Text = $"SOCKS gateway: 127.0.0.1:{_gateway.SocksPort}  (settings {_settings.ChainSocksPort})";
        _lblWinInet.Text = $"WinINET system proxy: {(_gateway.SystemProxyActive ? "ACTIVE (local HTTP)" : "inactive")}";
        _lblState.Text = $"Chain state: {_manager.State}";

        var hops = _manager.GetActiveHops();
        _lblHops.Text = hops.Count == 0
            ? "Active hops: (none)"
            : "Active hops: " + string.Join(" → ", hops.Select(h => $"{h.Kind} {h.Endpoint}"));

        _btnStop.Enabled = running;
        _btnExitIp.Enabled = running && hops.Count > 0;
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
                            _poolItems.Add(new PoolItem(c));
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
        _fixedItems.Add(new HopItem(hop));
    }

    private void ImportPoolFromSession()
    {
        var alive = _getAliveResults().Where(r => r.IsAlive).ToList();
        if (alive.Count == 0)
        {
            MessageBox.Show(this, "No alive proxies in the current session.", "Proxy Chains",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        foreach (var r in alive)
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

        foreach (var p in parsed)
            TryAddPool(new PoolCandidate(ProxyHop.FromParsed(p)));
    }

    private void RemoveSelectedPool()
    {
        if (_poolCandidates.SelectedItem is PoolItem item)
            _poolItems.Remove(item);
    }

    private void TryAddPool(PoolCandidate candidate)
    {
        if (_poolItems.Any(c => SameEndpoint(c.Candidate.Hop, candidate.Hop)))
            return;
        _poolItems.Add(new PoolItem(candidate));
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

        var profile = BuildFixedProfile(_fixedName.Text.Trim().Length > 0 ? _fixedName.Text.Trim() : "test");
        _manager.SwitchProfile(profile);
        RefreshStatus();

        UseWaitCursor = true;
        try
        {
            var dest = TestDestination();
            var ok = await _manager.ValidateChainAsync(dest, CancellationToken.None).ConfigureAwait(true);
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
        if (_poolItems.Count == 0)
        {
            MessageBox.Show(this, "Import or paste pool candidates first.", "Proxy Chains",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var poolName = string.IsNullOrWhiteSpace(_poolName.Text) ? "default-pool" : _poolName.Text.Trim();
        var candidates = _poolItems.Select(i => i.Candidate).ToList();
        try
        {
            _pools.Save(poolName, candidates);
        }
        catch
        {
            // continue
        }

        var hops = candidates.Select(c => c.Hop).ToList();
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

        await ApplyAndStartAsync(profile, candidates).ConfigureAwait(true);
    }

    private async Task StartPrivacyTwoHopAsync()
    {
        if (_poolItems.Count < 2)
        {
            MessageBox.Show(this, "Need at least two pool candidates for Privacy 2-hop.", "Proxy Chains",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        UseWaitCursor = true;
        try
        {
            var dest = TestDestination();
            var candidates = _poolItems.Select(i => i.Candidate).ToList();
            var chosen = await ChainSelectionPolicy.SelectAutoTwoHopPrivacyAsync(
                candidates,
                async (entry, exit, ct) =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var ok = await _manager.ValidateEdgeAsync(entry.Hop, exit.Hop, dest, ct)
                        .ConfigureAwait(false);
                    sw.Stop();
                    return new TwoHopEdgeResult(ok, Reliability: ok ? 1.0 : 0.0, E2eLatencyMs: (int)sw.ElapsedMilliseconds);
                },
                health: _manager.Health,
                timeBudget: TimeSpan.FromSeconds(45),
                maxConcurrency: 4,
                cancellationToken: CancellationToken.None).ConfigureAwait(true);

            if (chosen is null)
            {
                MessageBox.Show(this, "No compatible entry→exit pair found.", "Privacy 2-hop",
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

            await ApplyAndStartAsync(profile, candidates).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Privacy 2-hop", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            RefreshStatus();
        }
    }

    private async Task ApplyAndStartAsync(ProxyChainProfile profile, IReadOnlyList<PoolCandidate>? pool)
    {
        PersistActiveProfileId(profile.Id);
        _gateway.HttpPort = _settings.ChainHttpPort;
        _gateway.SocksPort = _settings.ChainSocksPort;

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

            RefreshStatus();
            MessageBox.Show(this,
                $"Gateway running.\nHTTP 127.0.0.1:{_gateway.HttpPort}\nSOCKS 127.0.0.1:{_gateway.SocksPort}" +
                (_gateway.SystemProxyActive ? "\nWinINET → local HTTP gateway." : ""),
                "Proxy Chains",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (PortInUseException ex)
        {
            await HandlePortInUseAsync(ex, profile, pool).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Start failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            RefreshStatus();
        }
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
        UseWaitCursor = true;
        try
        {
            await _gateway.StopAsync().ConfigureAwait(true);
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Stop failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
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
                RefreshStatus();
                MessageBox.Show(this, "Gateway stopped and Windows proxy restored.", "Proxy Chains",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_winProxy.HasPendingRestore)
            {
                _winProxy.Restore();
                MessageBox.Show(this, "Original Windows proxy settings restored.", "Proxy Chains",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                var snap = _winProxy.Capture();
                snap.ProxyEnable = 0;
                snap.ProxyServer = "";
                _winProxy.SaveBackup(snap);
                _winProxy.Restore(snap);
                _winProxy.ClearBackup();
                MessageBox.Show(this, "System proxy disabled (no prior backup found).", "Proxy Chains",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        UseWaitCursor = true;
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

            MessageBox.Show(this, $"Exit IP: {ip}", "Exit IP",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Exit IP check failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            RefreshStatus();
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
        && a.Proxy.Port == b.Proxy.Port;

    private sealed class HopItem(ProxyHop hop)
    {
        public ProxyHop Hop { get; } = hop;
        public override string ToString() => $"{Hop.Kind}  {Hop.Endpoint}";
    }

    private sealed class PoolItem(PoolCandidate candidate)
    {
        public PoolCandidate Candidate { get; } = candidate;
        public override string ToString()
        {
            var c = Candidate;
            var meta = c.Country is { Length: > 0 } ? $"  [{c.Country}]" : "";
            var lat = c.LatencyMs is int ms ? $"  {ms}ms" : "";
            return $"{c.Hop.Kind}  {c.Hop.Endpoint}{meta}{lat}";
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
