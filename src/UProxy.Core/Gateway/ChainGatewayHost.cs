using System.Net;
using System.Runtime.Versioning;
using UProxy.Core.Chaining;
using UProxy.Core.Models;
using UProxy.Core.Windows;

namespace UProxy.Core.Gateway;

/// <summary>
/// Orchestrates local HTTP + SOCKS5 gateway listeners and optional WinINET system-proxy opt-in.
/// </summary>
public sealed class ChainGatewayHost : IAsyncDisposable
{
    private readonly object _gate = new();
    private LocalHttpProxyServer? _http;
    private LocalSocks5Server? _socks;
    private WindowsProxyManager? _winProxy;
    private bool _weEnabledSystemProxy;
    private int _running;

    public int HttpPort { get; set; } = LocalHttpProxyServer.DefaultPort;
    public int SocksPort { get; set; } = LocalSocks5Server.DefaultPort;

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public bool SystemProxyActive
    {
        get { lock (_gate) return _weEnabledSystemProxy; }
    }

    public ChainManager? Manager { get; private set; }

    /// <summary>
    /// Start HTTP then SOCKS listeners. Optionally point WinINET at the local HTTP gateway (Windows only).
    /// </summary>
    public async Task StartAsync(
        ChainManager manager,
        bool enableSystemProxy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manager);
        // Match LocalHttp/Socks: reject a pre-cancelled token before claiming the
        // single-start guard so callers can retry StartAsync after cancellation.
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("Chain gateway host is already running.");

        Manager = manager;
        var connector = new ChainManagerConnector(manager);
        var httpPort = HttpPort;
        var socksPort = SocksPort;

        try
        {
            _http = new LocalHttpProxyServer(connector, IPAddress.Loopback, httpPort);
            try
            {
                await _http.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (LoopbackPortFinder.IsAddressInUse(ex))
            {
                await StopListenersUnsafeAsync().ConfigureAwait(false);
                Interlocked.Exchange(ref _running, 0);
                throw new PortInUseException(httpPort, LoopbackPortFinder.FindFreePort(), ex);
            }

            HttpPort = _http.Port;

            _socks = new LocalSocks5Server(connector, IPAddress.Loopback, socksPort);
            try
            {
                await _socks.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (LoopbackPortFinder.IsAddressInUse(ex))
            {
                await StopListenersUnsafeAsync().ConfigureAwait(false);
                Interlocked.Exchange(ref _running, 0);
                throw new PortInUseException(socksPort, LoopbackPortFinder.FindFreePort(), ex);
            }

            SocksPort = _socks.Port;

            if (enableSystemProxy && OperatingSystem.IsWindows())
            {
                EnableSystemProxyWindows();
            }
        }
        catch (PortInUseException)
        {
            throw;
        }
        catch
        {
            await StopListenersUnsafeAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _running, 0);
            Manager = null;
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0)
        {
            // Best-effort cleanup for Dispose after a failed/partial start.
            await RestoreSystemProxyIfNeededAsync().ConfigureAwait(false);
            await StopListenersUnsafeAsync().ConfigureAwait(false);
            Manager = null;
            return;
        }

        await RestoreSystemProxyIfNeededAsync().ConfigureAwait(false);
        await StopListenersUnsafeAsync().ConfigureAwait(false);
        Manager = null;
    }

    /// <summary>Switch the active chain profile without restarting listeners or touching WinINET.</summary>
    public void SwitchProfile(ProxyChainProfile profile, IReadOnlyList<PoolCandidate>? pool = null)
    {
        var manager = Manager ?? throw new InvalidOperationException("Gateway is not running.");
        manager.SwitchProfile(profile, pool);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    [SupportedOSPlatform("windows")]
    private void EnableSystemProxyWindows()
    {
        // Listeners are already up — point WinINET at the local HTTP gateway.
        var win = _winProxy ??= new WindowsProxyManager();
        try
        {
            win.SetLocalGateway(HttpPort);
            lock (_gate)
                _weEnabledSystemProxy = true;
        }
        catch
        {
            try
            {
                if (win.HasPendingRestore)
                    win.Restore();
            }
            catch
            {
                // ignore restore failure during rollback
            }

            // Outer StartAsync catch stops listeners and clears running state.
            throw;
        }
    }

    private Task RestoreSystemProxyIfNeededAsync()
    {
        bool restore;
        WindowsProxyManager? win;
        lock (_gate)
        {
            restore = _weEnabledSystemProxy;
            win = _winProxy;
            _weEnabledSystemProxy = false;
        }

        if (!restore || win is null || !OperatingSystem.IsWindows())
            return Task.CompletedTask;

        // Do not swallow restore failures — leaving WinINET pointed at a stopped
        // local gateway is worse than surfacing the error to the caller/UI.
        if (win.HasPendingRestore)
            win.Restore();

        return Task.CompletedTask;
    }

    private async Task StopListenersUnsafeAsync()
    {
        var socks = Interlocked.Exchange(ref _socks, null);
        var http = Interlocked.Exchange(ref _http, null);

        if (socks is not null)
        {
            try { await socks.StopAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
            try { await socks.DisposeAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        if (http is not null)
        {
            try { await http.StopAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
            try { await http.DisposeAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
        }
    }
}
