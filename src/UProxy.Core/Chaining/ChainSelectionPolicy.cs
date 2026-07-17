using UProxy.Core.Models;

namespace UProxy.Core.Chaining;

/// <summary>
/// Ranking and shortlisting helpers for FastFailover pools and AutoTwoHopPrivacy pairs.
/// </summary>
public static class ChainSelectionPolicy
{
    public const int AutoTwoHopShortlistMin = 10;
    public const int AutoTwoHopShortlistMax = 20;

    /// <summary>
    /// Pick the best healthy hop: success rate (desc), then latency (asc), then recency (desc).
    /// </summary>
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

    /// <summary>
    /// Keep candidates that are healthy in the tracker (unknown = healthy) and optionally
    /// checked within <paramref name="maxAge"/>.
    /// </summary>
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

    /// <summary>
    /// Shortlist the top N candidates by health (success rate) then latency.
    /// </summary>
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

        var take = Math.Clamp(ranked.Count, 0, maxCount);
        // Prefer at least minCount when available; otherwise take all.
        if (ranked.Count >= minCount)
            take = Math.Min(maxCount, ranked.Count);
        else
            take = ranked.Count;

        return ranked.Take(take).ToList();
    }

    /// <summary>
    /// Conceptually test entry→exit pairs (caller supplies edge test). Prefers compatibility,
    /// then reliability, then e2e latency, then a soft country-diversity bonus.
    /// Rejects the same endpoint twice. Optional <paramref name="pinExitId"/> forces exit hop.
    /// </summary>
    public static async Task<(PoolCandidate Entry, PoolCandidate Exit)?> SelectAutoTwoHopPrivacyAsync(
        IEnumerable<PoolCandidate> pool,
        Func<PoolCandidate, PoolCandidate, CancellationToken, Task<TwoHopEdgeResult>> testEdge,
        Guid? pinExitId = null,
        ChainHealthTracker? health = null,
        int shortlistMin = AutoTwoHopShortlistMin,
        int shortlistMax = AutoTwoHopShortlistMax,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(testEdge);

        var shortlist = Shortlist(pool, health, shortlistMin, shortlistMax);
        if (shortlist.Count < 2)
            return null;

        (PoolCandidate Entry, PoolCandidate Exit)? best = null;
        var bestScore = double.NegativeInfinity;

        foreach (var entry in shortlist)
        {
            foreach (var exit in shortlist)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (ReferenceEquals(entry, exit) || entry.Hop.Id == exit.Hop.Id)
                    continue;
                if (SameEndpoint(entry.Hop, exit.Hop))
                    continue;
                if (pinExitId is { } pinned && exit.Hop.Id != pinned)
                    continue;

                var result = await testEdge(entry, exit, cancellationToken).ConfigureAwait(false);
                if (!result.Compatible)
                    continue;

                var diversityBonus = CountryDiversityBonus(entry.Country, exit.Country);
                // Higher reliability and diversity, lower latency → higher score.
                var score = (result.Reliability * 1_000_000.0)
                            - result.E2eLatencyMs
                            + diversityBonus;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = (entry, exit);
                }
            }
        }

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
        // Soft bonus — enough to break ties, not enough to beat reliability/latency.
        return 50.0;
    }
}
