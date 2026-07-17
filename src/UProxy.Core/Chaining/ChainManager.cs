using UProxy.Core.Models;

namespace UProxy.Core.Chaining;

/// <summary>
/// High-level chain orchestration: active profile, dialing, health feedback, validation.
/// Thread-safe.
/// </summary>
public sealed class ChainManager
{
    private readonly ChainDialer _dialer;
    private readonly ChainHealthTracker _health;
    private readonly object _gate = new();

    private ProxyChainProfile? _profile;
    private IReadOnlyList<PoolCandidate> _pool = Array.Empty<PoolCandidate>();
    private IReadOnlyList<ProxyHop> _activeHops = Array.Empty<ProxyHop>();
    private ChainRuntimeState _state = ChainRuntimeState.Stopped;

    public ChainManager(ChainDialer? dialer = null, ChainHealthTracker? health = null)
    {
        _dialer = dialer ?? new ChainDialer();
        _health = health ?? new ChainHealthTracker();
    }

    public ChainHealthTracker Health => _health;
    public ChainDialer Dialer => _dialer;

    public ChainRuntimeState State
    {
        get { lock (_gate) return _state; }
    }

    public ProxyChainProfile? ActiveProfile
    {
        get { lock (_gate) return _profile; }
    }

    /// <summary>Atomically replace the active profile (and optional FastFailover pool).</summary>
    public void SwitchProfile(ProxyChainProfile profile, IReadOnlyList<PoolCandidate>? pool = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(profile.Hops);

        lock (_gate)
        {
            _state = ChainRuntimeState.Switching;
            _profile = profile;
            _pool = pool is { Count: > 0 }
                ? pool.ToList()
                : profile.Hops.Select(h => new PoolCandidate(h)).ToList();

            _activeHops = profile.Mode switch
            {
                ChainMode.StrictMultiHop => profile.Hops.ToList(),
                ChainMode.FastFailover => ResolveFastFailoverHopsUnlocked(),
                _ => profile.Hops.ToList()
            };

            _state = _activeHops.Count > 0
                ? ChainRuntimeState.Healthy
                : ChainRuntimeState.Stopped;
        }
    }

    /// <summary>Ordered hops currently used for dialing (Strict order is preserved).</summary>
    public IReadOnlyList<ProxyHop> GetActiveHops()
    {
        lock (_gate)
            return _activeHops.ToList();
    }

    public async Task<Stream> ConnectAsync(ChainDestination destination, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProxyHop> hops;
        lock (_gate)
        {
            if (_profile is null || _activeHops.Count == 0)
                throw new InvalidOperationException("No active chain profile.");

            if (_profile.Mode == ChainMode.FastFailover)
            {
                // Re-select best hop each connect so health changes take effect.
                hops = ResolveFastFailoverHopsUnlocked();
                _activeHops = hops;
            }
            else
            {
                hops = _activeHops;
            }
        }

        try
        {
            var stream = await _dialer.ConnectAsync(hops, destination, cancellationToken)
                .ConfigureAwait(false);

            foreach (var hop in hops)
                _health.RecordSuccess(hop);

            lock (_gate)
            {
                if (_state is ChainRuntimeState.Degraded or ChainRuntimeState.Switching)
                    _state = ChainRuntimeState.Healthy;
            }

            return stream;
        }
        catch (ChainDialException ex)
        {
            RecordHopFailure(hops, ex);
            lock (_gate)
            {
                if (_state == ChainRuntimeState.Healthy)
                    _state = ChainRuntimeState.Degraded;
            }

            throw;
        }
    }

    /// <summary>Dial <paramref name="from"/> → <paramref name="to"/> → test destination.</summary>
    public async Task<bool> ValidateEdgeAsync(
        ProxyHop from,
        ProxyHop to,
        ChainDestination testDestination,
        CancellationToken cancellationToken,
        TimeSpan? overallTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var hops = new[] { from, to };
        try
        {
            await using var stream = await _dialer
                .ConnectAsync(hops, testDestination, cancellationToken, overallTimeout)
                .ConfigureAwait(false);
            _health.RecordSuccess(from);
            _health.RecordSuccess(to);
            return true;
        }
        catch (ChainDialException ex)
        {
            RecordHopFailure(hops, ex);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Dial the active chain to a test destination.</summary>
    public async Task<bool> ValidateChainAsync(
        ChainDestination testDestination,
        CancellationToken cancellationToken,
        TimeSpan? overallTimeout = null)
    {
        IReadOnlyList<ProxyHop> hops;
        lock (_gate)
        {
            if (_activeHops.Count == 0)
                return false;
            hops = _activeHops.ToList();
        }

        try
        {
            await using var stream = await _dialer
                .ConnectAsync(hops, testDestination, cancellationToken, overallTimeout)
                .ConfigureAwait(false);
            foreach (var hop in hops)
                _health.RecordSuccess(hop);
            return true;
        }
        catch (ChainDialException ex)
        {
            RecordHopFailure(hops, ex);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private IReadOnlyList<ProxyHop> ResolveFastFailoverHopsUnlocked()
    {
        var best = ChainSelectionPolicy.SelectFastFailover(_pool, _health);
        if (best is not null)
            return new[] { best.Hop };

        // Fall back to first profile hop if pool empty / all unhealthy.
        if (_profile is { Hops.Count: > 0 })
            return new[] { _profile.Hops[0] };

        return Array.Empty<ProxyHop>();
    }

    private void RecordHopFailure(IReadOnlyList<ProxyHop> hops, ChainDialException ex)
    {
        if (!ChainHealthTracker.IsProxyAttributable(ex.Reason))
            return;

        var index = ex.FailedHopIndex;
        if (index < 0)
            index = 0;
        if (index >= hops.Count)
            return;

        _health.RecordProxyFailure(hops[index], ex.Reason);
    }
}
