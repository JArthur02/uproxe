using UProxy.Core.Models;

namespace UProxy.Core.Chaining;

/// <summary>
/// High-level chain orchestration: active profile, dialing, health feedback, validation.
/// Thread-safe. FastFailover runs verification probes and cooldown so dead hops are replaced.
/// </summary>
public sealed class ChainManager
{
    private readonly ChainDialer _dialer;
    private readonly ChainHealthTracker _health;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _verifyLock = new(1, 1);
    private int _halfOpenSweepRunning;

    private ProxyChainProfile? _profile;
    private IReadOnlyList<PoolCandidate> _pool = Array.Empty<PoolCandidate>();
    private IReadOnlyList<ProxyHop> _activeHops = Array.Empty<ProxyHop>();
    private ChainRuntimeState _state = ChainRuntimeState.Stopped;

    /// <summary>Destination used for confirmation probes after repeated proxy failures.</summary>
    public ChainDestination VerificationDestination { get; set; } = new("www.google.com", 443);

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

    public void SwitchProfile(ProxyChainProfile profile, IReadOnlyList<PoolCandidate>? pool = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(profile.Hops);

        if (profile.Mode == ChainMode.StrictMultiHop && profile.Hops.Count > 1)
            ChainDialer.ValidateHopOrder(profile.Hops);

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

    public IReadOnlyList<ProxyHop> GetActiveHops()
    {
        lock (_gate)
            return _activeHops.ToList();
    }

    public async Task<Stream> ConnectAsync(ChainDestination destination, CancellationToken cancellationToken)
    {
        // Kick half-open recovery in the background — never block the client path
        // behind sequential expired-cooldown probes.
        KickHalfOpenProbes();

        IReadOnlyList<ProxyHop> hops;
        lock (_gate)
        {
            if (_profile is null)
                throw new InvalidOperationException("No active chain profile.");

            if (_profile.Mode == ChainMode.FastFailover)
            {
                hops = ResolveFastFailoverHopsUnlocked();
                _activeHops = hops;
            }
            else
            {
                hops = _activeHops;
            }

            if (hops.Count == 0)
                throw new InvalidOperationException(
                    "No healthy proxy available (all candidates are cooling down).");
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
            await HandleDialFailureAsync(hops, ex, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

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
            await HandleDialFailureAsync(hops, ex, cancellationToken).ConfigureAwait(false);
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
            await HandleDialFailureAsync(hops, ex, cancellationToken).ConfigureAwait(false);
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

        // Prefer any still-healthy profile hop, but never fall back to a cooling-down
        // hop — that would bypass cooldown and keep hammering a known-bad proxy.
        if (_profile is { Hops.Count: > 0 })
        {
            foreach (var hop in _profile.Hops)
            {
                if (_health.IsHealthy(hop))
                    return new[] { hop };
            }
        }

        return Array.Empty<ProxyHop>();
    }

    private async Task HandleDialFailureAsync(
        IReadOnlyList<ProxyHop> hops,
        ChainDialException ex,
        CancellationToken cancellationToken)
    {
        if (!ChainHealthTracker.IsProxyAttributable(ex.Reason))
            return;

        var index = ex.FailedHopIndex;
        if (index < 0)
            index = 0;
        if (index >= hops.Count)
            return;

        var hop = hops[index];
        _health.RecordProxyFailure(hop, ex.Reason);

        lock (_gate)
        {
            if (_state == ChainRuntimeState.Healthy)
                _state = ChainRuntimeState.Degraded;
        }

        if (_health.NeedsVerification(hop))
            await RunVerificationAsync(hop, cancellationToken).ConfigureAwait(false);

        // FastFailover: drop dead hop from active selection immediately.
        lock (_gate)
        {
            if (_profile?.Mode == ChainMode.FastFailover)
                _activeHops = ResolveFastFailoverHopsUnlocked();
        }
    }

    private async Task RunVerificationAsync(ProxyHop hop, CancellationToken cancellationToken)
    {
        await _verifyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_health.NeedsVerification(hop))
                return;

            try
            {
                await using var stream = await _dialer
                    .ConnectAsync(
                        new[] { hop },
                        VerificationDestination,
                        cancellationToken,
                        TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                _health.RecordSuccess(hop);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _health.MarkDown(hop);
            }
        }
        finally
        {
            _verifyLock.Release();
        }
    }

    private void KickHalfOpenProbes()
    {
        if (Interlocked.CompareExchange(ref _halfOpenSweepRunning, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await TryHalfOpenProbesAsync().ConfigureAwait(false);
            }
            catch
            {
                // Background recovery — never surface to the client path.
            }
            finally
            {
                Interlocked.Exchange(ref _halfOpenSweepRunning, 0);
            }
        });
    }

    private async Task TryHalfOpenProbesAsync()
    {
        List<ProxyHop> candidates;
        lock (_gate)
        {
            candidates = _pool.Select(c => c.Hop).ToList();
            if (_profile is not null)
                candidates.AddRange(_profile.Hops);
        }

        foreach (var hop in candidates.DistinctBy(h => ChainHealthTracker.MakeKey(h)))
        {
            if (_health.IsInCooldown(hop) || !_health.AllowProbe(hop))
                continue;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await using var stream = await _dialer
                    .ConnectAsync(
                        new[] { hop },
                        VerificationDestination,
                        cts.Token,
                        TimeSpan.FromSeconds(8))
                    .ConfigureAwait(false);
                _health.RecordSuccess(hop);
            }
            catch
            {
                _health.MarkDown(hop);
            }
        }
    }
}
