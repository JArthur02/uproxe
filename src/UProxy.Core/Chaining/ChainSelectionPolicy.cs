using UProxy.Core.Models;

namespace UProxy.Core.Chaining;

/// <summary>
/// Ranking and shortlisting helpers for FastFailover pools and AutoTwoHopPrivacy pairs.
/// </summary>
public static class ChainSelectionPolicy
{
    public const int AutoTwoHopShortlistMin = 10;
    public const int AutoTwoHopShortlistMax = 20;
    public const int DefaultMaxConcurrentEdgeTests = 4;
    public static readonly TimeSpan DefaultAutoTwoHopBudget = TimeSpan.FromSeconds(45);

    public static PoolCandidate? SelectFastFailover(
        IEnumerable<PoolCandidate> pool,
        ChainHealthTracker? health = null)
    {
        ArgumentNullException.ThrowIfNull(pool);

        return FilterRecentlyHealthy(pool, health)
            .OrderByDescending(c => c.SuccessRate ?? health?.GetStats(c.Hop).SuccessRate ?? 0.0)
            .ThenBy(c => c.LatencyMs ?? int.MaxValue)
            .ThenByDescending(c => c.LastChecked ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
    }

    public static IEnumerable<PoolCandidate> FilterRecentlyHealthy(
        IEnumerable<PoolCandidate> pool,
        ChainHealthTracker? health = null,
        TimeSpan? maxAge = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        foreach (var c in pool)
        {
            if (health is not null && !health.IsHealthy(c.Hop))
                continue;
            if (maxAge is { } age && c.LastChecked is { } checkedAt && now - checkedAt > age)
                continue;
            yield return c;
        }
    }

    public static IReadOnlyList<PoolCandidate> Shortlist(
        IEnumerable<PoolCandidate> pool,
        ChainHealthTracker? health = null,
        int minCount = AutoTwoHopShortlistMin,
        int maxCount = AutoTwoHopShortlistMax)
    {
        ArgumentNullException.ThrowIfNull(pool);
        if (minCount < 1) throw new ArgumentOutOfRangeException(nameof(minCount));
        if (maxCount < minCount) throw new ArgumentOutOfRangeException(nameof(maxCount));

        var ranked = FilterRecentlyHealthy(pool, health)
            .OrderByDescending(c => c.SuccessRate ?? health?.GetStats(c.Hop).SuccessRate ?? 0.0)
            .ThenBy(c => c.LatencyMs ?? int.MaxValue)
            .ThenByDescending(c => c.LastChecked ?? DateTimeOffset.MinValue)
            .ToList();

        var take = ranked.Count >= minCount
            ? Math.Min(maxCount, ranked.Count)
            : ranked.Count;

        return ranked.Take(take).ToList();
    }

    /// <summary>
    /// Test entry→exit pairs with a total time budget, cancellation, and bounded concurrency.
    /// Prefers compatibility, then reliability, then e2e latency, then soft country diversity.
    /// </summary>
    public static async Task<(PoolCandidate Entry, PoolCandidate Exit)?> SelectAutoTwoHopPrivacyAsync(
        IEnumerable<PoolCandidate> pool,
        Func<PoolCandidate, PoolCandidate, CancellationToken, Task<TwoHopEdgeResult>> testEdge,
        Guid? pinExitId = null,
        ChainHealthTracker? health = null,
        int shortlistMin = AutoTwoHopShortlistMin,
        int shortlistMax = AutoTwoHopShortlistMax,
        TimeSpan? timeBudget = null,
        int maxConcurrency = DefaultMaxConcurrentEdgeTests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(testEdge);
        if (maxConcurrency < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));

        var shortlist = Shortlist(pool, health, shortlistMin, shortlistMax);
        if (shortlist.Count < 2)
            return null;

        var pairs = new List<(PoolCandidate Entry, PoolCandidate Exit)>();
        foreach (var entry in shortlist)
        {
            foreach (var exit in shortlist)
            {
                if (ReferenceEquals(entry, exit) || entry.Hop.Id == exit.Hop.Id)
                    continue;
                if (SameEndpoint(entry.Hop, exit.Hop))
                    continue;
                if (pinExitId is { } pinned && exit.Hop.Id != pinned)
                    continue;
                pairs.Add((entry, exit));
            }
        }

        if (pairs.Count == 0)
            return null;

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(timeBudget ?? DefaultAutoTwoHopBudget);
        var ct = budgetCts.Token;

        using var gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        (PoolCandidate Entry, PoolCandidate Exit)? best = null;
        var bestScore = double.NegativeInfinity;
        var scoreLock = new object();

        var tasks = pairs.Select(async pair =>
        {
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                var result = await testEdge(pair.Entry, pair.Exit, ct).ConfigureAwait(false);
                if (!result.Compatible)
                    return;

                var diversityBonus = CountryDiversityBonus(pair.Entry.Country, pair.Exit.Country);
                var score = (result.Reliability * 1_000_000.0)
                            - result.E2eLatencyMs
                            + diversityBonus;

                lock (scoreLock)
                {
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = pair;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Budget or caller cancel — stop this pair.
            }
            finally
            {
                try { gate.Release(); } catch { /* disposed */ }
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Return best found within budget (may be null).
        }

        if (cancellationToken.IsCancellationRequested)
            cancellationToken.ThrowIfCancellationRequested();

        return best;
    }

    private static bool SameEndpoint(ProxyHop a, ProxyHop b) =>
        string.Equals(
            a.Proxy.Host.Trim().TrimStart('[').TrimEnd(']'),
            b.Proxy.Host.Trim().TrimStart('[').TrimEnd(']'),
            StringComparison.OrdinalIgnoreCase)
        && a.Proxy.Port == b.Proxy.Port;

    private static double CountryDiversityBonus(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return 0;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return 0;
        return 50.0;
    }
}
