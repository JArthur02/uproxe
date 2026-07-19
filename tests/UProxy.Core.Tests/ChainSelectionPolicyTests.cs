using UProxy.Core.Chaining;
using UProxy.Core.Models;

namespace UProxy.Core.Tests;

public class ChainSelectionPolicyTests
{
    private static PoolCandidate Cand(
        string host,
        int port,
        double? successRate = null,
        int? latencyMs = null,
        DateTimeOffset? lastChecked = null,
        string? country = null,
        ProxyKind kind = ProxyKind.Socks5)
    {
        var hop = ProxyHop.FromParsed(new ParsedProxy(host, port, ProxyProtocol.Socks5), kind);
        return new PoolCandidate(hop, country, latencyMs, successRate, lastChecked);
    }

    [Fact]
    public void FastFailover_RanksBySuccessThenLatencyThenRecency()
    {
        var t0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var pool = new[]
        {
            Cand("1.1.1.1", 1080, successRate: 0.5, latencyMs: 10, lastChecked: t0),
            Cand("2.2.2.2", 1080, successRate: 0.9, latencyMs: 50, lastChecked: t0),
            Cand("3.3.3.3", 1080, successRate: 0.9, latencyMs: 20, lastChecked: t0),
            Cand("4.4.4.4", 1080, successRate: 0.9, latencyMs: 20, lastChecked: t0.AddMinutes(5)),
        };

        var best = ChainSelectionPolicy.SelectFastFailover(pool);
        Assert.NotNull(best);
        // Same success (0.9) and latency (20) → more recent wins (4.4.4.4)
        Assert.Equal("4.4.4.4", best!.Hop.Proxy.Host);
    }

    [Fact]
    public void FastFailover_PrefersHigherSuccessRateOverLowerLatency()
    {
        var pool = new[]
        {
            Cand("fast-bad", 1080, successRate: 0.4, latencyMs: 5),
            Cand("slow-good", 1080, successRate: 0.95, latencyMs: 200),
        };

        var best = ChainSelectionPolicy.SelectFastFailover(pool);
        Assert.Equal("slow-good", best!.Hop.Proxy.Host);
    }

    [Fact]
    public void FilterRecentlyHealthy_ExcludesCooldown()
    {
        var health = new ChainHealthTracker();
        var a = Cand("10.0.0.1", 1080, successRate: 1.0, latencyMs: 10);
        var b = Cand("10.0.0.2", 1080, successRate: 1.0, latencyMs: 10);
        health.MarkDown(b.Hop);

        var filtered = ChainSelectionPolicy.FilterRecentlyHealthy(new[] { a, b }, health).ToList();
        Assert.Single(filtered);
        Assert.Equal("10.0.0.1", filtered[0].Hop.Proxy.Host);
    }

    [Fact]
    public void Shortlist_CapsBetween10And20()
    {
        var pool = Enumerable.Range(1, 50)
            .Select(i => Cand($"10.0.0.{i}", 1080, successRate: 1.0 - i * 0.001, latencyMs: i))
            .ToList();

        var shortlist = ChainSelectionPolicy.Shortlist(pool);
        Assert.Equal(20, shortlist.Count);
        Assert.Equal("10.0.0.1", shortlist[0].Hop.Proxy.Host);
    }

    [Fact]
    public void Shortlist_TakesAllWhenFewerThanMin()
    {
        var pool = new[]
        {
            Cand("a", 1, 1.0, 10),
            Cand("b", 2, 0.9, 10),
            Cand("c", 3, 0.8, 10),
        };
        var shortlist = ChainSelectionPolicy.Shortlist(pool);
        Assert.Equal(3, shortlist.Count);
    }

    [Fact]
    public async Task AutoTwoHop_RejectsSameEndpoint_PrefersDiversityAndReliability()
    {
        var entryA = Cand("1.1.1.1", 1080, successRate: 1.0, latencyMs: 30, country: "US");
        var exitSameHost = Cand("1.1.1.1", 1080, successRate: 1.0, latencyMs: 30, country: "DE");
        var exitB = Cand("2.2.2.2", 1080, successRate: 1.0, latencyMs: 40, country: "DE");
        var exitC = Cand("3.3.3.3", 1080, successRate: 1.0, latencyMs: 40, country: "US");

        // Pad shortlist so SelectAutoTwoHop has enough candidates conceptually.
        var padding = Enumerable.Range(10, 12)
            .Select(i => Cand($"9.9.9.{i}", 1080, successRate: 0.5, latencyMs: 100, country: "XX"))
            .ToList();
        var pool = new List<PoolCandidate> { entryA, exitSameHost, exitB, exitC };
        pool.AddRange(padding);

        var chosen = await ChainSelectionPolicy.SelectAutoTwoHopPrivacyAsync(
            pool,
            (entry, exit, _) =>
            {
                // Same-endpoint pairs should never be offered; if somehow, reject.
                if (entry.Hop.Proxy.Host == exit.Hop.Proxy.Host &&
                    entry.Hop.Proxy.Port == exit.Hop.Proxy.Port)
                    return Task.FromResult(new TwoHopEdgeResult(false));

                var reliability = exit.Hop.Proxy.Host == "2.2.2.2" ? 0.99 : 0.8;
                var latency = 100;
                return Task.FromResult(new TwoHopEdgeResult(true, reliability, latency));
            });

        Assert.NotNull(chosen);
        Assert.Equal("1.1.1.1", chosen!.Value.Entry.Hop.Proxy.Host);
        Assert.Equal("2.2.2.2", chosen.Value.Exit.Hop.Proxy.Host);
        Assert.NotEqual(chosen.Value.Entry.Hop.Proxy.Host, chosen.Value.Exit.Hop.Proxy.Host);
    }

    [Fact]
    public async Task AutoTwoHop_PinExit_Honored()
    {
        var a = Cand("1.1.1.1", 1080, 1.0, 10, country: "US");
        var b = Cand("2.2.2.2", 1080, 1.0, 10, country: "DE");
        var c = Cand("3.3.3.3", 1080, 1.0, 10, country: "FR");
        var padding = Enumerable.Range(10, 10)
            .Select(i => Cand($"8.8.8.{i}", 1080, 0.5, 50))
            .ToList();
        var pool = new List<PoolCandidate> { a, b, c };
        pool.AddRange(padding);

        var chosen = await ChainSelectionPolicy.SelectAutoTwoHopPrivacyAsync(
            pool,
            (_, _, _) => Task.FromResult(new TwoHopEdgeResult(true, 1.0, 50)),
            pinExitId: c.Hop.Id);

        Assert.NotNull(chosen);
        Assert.Equal(c.Hop.Id, chosen!.Value.Exit.Hop.Id);
        Assert.NotEqual(c.Hop.Id, chosen.Value.Entry.Hop.Id);
    }

    [Fact]
    public async Task AutoTwoHop_IncompatibleEdges_Skipped()
    {
        var a = Cand("1.1.1.1", 1080, 1.0, 10);
        var b = Cand("2.2.2.2", 1080, 1.0, 10);
        var padding = Enumerable.Range(10, 10)
            .Select(i => Cand($"7.7.7.{i}", 1080, 0.5, 50))
            .ToList();
        var pool = new List<PoolCandidate> { a, b };
        pool.AddRange(padding);

        var chosen = await ChainSelectionPolicy.SelectAutoTwoHopPrivacyAsync(
            pool,
            (_, _, _) => Task.FromResult(new TwoHopEdgeResult(false)));

        Assert.Null(chosen);
    }
}
