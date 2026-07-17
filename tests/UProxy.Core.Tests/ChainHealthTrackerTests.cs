using UProxy.Core.Chaining;
using UProxy.Core.Models;

namespace UProxy.Core.Tests;

public class ChainHealthTrackerTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utc;
        public ManualTimeProvider(DateTimeOffset start) => _utc = start;
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(TimeSpan delta) => _utc += delta;
    }

    private static ProxyHop Hop(string host, int port, ProxyKind kind = ProxyKind.Socks5) =>
        ProxyHop.FromParsed(new ParsedProxy(host, port, ProxyProtocol.Socks5), kind);

    [Fact]
    public void DestinationErrors_DoNotDropHealth()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var tracker = new ChainHealthTracker(time);
        var hop = Hop("1.1.1.1", 1080);
        var key = ChainHealthTracker.MakeKey(hop);

        tracker.RecordProxyFailure(key, FailureReason.TargetUnreachableThroughProxy);
        tracker.RecordProxyFailure(key, FailureReason.TlsFailure);
        tracker.RecordProxyFailure(key, FailureReason.Cancelled);
        tracker.RecordProxyFailure(key, FailureReason.JudgeMismatch);
        tracker.RecordProxyFailure(key, FailureReason.EmptyResponse);

        Assert.True(tracker.IsHealthy(key));
        Assert.False(tracker.NeedsVerification(key));
        Assert.False(tracker.IsInCooldown(key));
        Assert.Equal(0, tracker.GetStats(key).FailureCount);
    }

    [Fact]
    public void ThreeFailuresWithinWindow_TriggerNeedVerify()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var tracker = new ChainHealthTracker(time);
        var key = ChainHealthTracker.MakeKey(Hop("2.2.2.2", 1080));

        tracker.RecordProxyFailure(key, FailureReason.ProxyHandshakeFailure);
        tracker.RecordProxyFailure(key, FailureReason.ConnectTimeout);
        Assert.False(tracker.NeedsVerification(key));

        tracker.RecordProxyFailure(key, FailureReason.AuthenticationRequired);
        Assert.True(tracker.NeedsVerification(key));
        Assert.True(tracker.IsHealthy(key)); // not down until failed verify
    }

    [Fact]
    public void FailedVerify_StartsCooldown()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var tracker = new ChainHealthTracker(time);
        var key = ChainHealthTracker.MakeKey(Hop("3.3.3.3", 1080));

        for (var i = 0; i < 3; i++)
            tracker.RecordProxyFailure(key, FailureReason.ProxyHandshakeFailure);
        Assert.True(tracker.NeedsVerification(key));

        tracker.MarkDown(key);

        Assert.True(tracker.IsInCooldown(key));
        Assert.False(tracker.IsHealthy(key));
        Assert.False(tracker.NeedsVerification(key));
        Assert.Equal(ChainHealthTracker.CooldownForLevel(0),
            tracker.GetStats(key).CooldownUntilUtc!.Value - time.GetUtcNow());
    }

    [Fact]
    public void Cooldown_Escalates_30s_2m_10m_30m()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var tracker = new ChainHealthTracker(time);
        var key = ChainHealthTracker.MakeKey(Hop("4.4.4.4", 1080));

        TimeSpan CooldownRemaining() =>
            tracker.GetStats(key).CooldownUntilUtc!.Value - time.GetUtcNow();

        tracker.MarkDown(key);
        Assert.Equal(TimeSpan.FromSeconds(30), CooldownRemaining());

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.True(tracker.AllowProbe(key));
        tracker.RecordProxyFailure(key, FailureReason.ConnectRefused); // half-open fail
        Assert.Equal(TimeSpan.FromMinutes(2), CooldownRemaining());

        time.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        Assert.True(tracker.AllowProbe(key));
        tracker.MarkDown(key);
        Assert.Equal(TimeSpan.FromMinutes(10), CooldownRemaining());

        time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        Assert.True(tracker.AllowProbe(key));
        tracker.MarkDown(key);
        Assert.Equal(TimeSpan.FromMinutes(30), CooldownRemaining());

        // Max stays at 30m
        time.Advance(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(1));
        Assert.True(tracker.AllowProbe(key));
        tracker.MarkDown(key);
        Assert.Equal(TimeSpan.FromMinutes(30), CooldownRemaining());
    }

    [Fact]
    public void HalfOpen_Success_Restores()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var tracker = new ChainHealthTracker(time);
        var key = ChainHealthTracker.MakeKey(Hop("5.5.5.5", 1080));

        tracker.MarkDown(key);
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.True(tracker.AllowProbe(key));
        Assert.False(tracker.IsHealthy(key)); // half-open
        Assert.False(tracker.AllowProbe(key)); // only once

        tracker.RecordSuccess(key);
        Assert.True(tracker.IsHealthy(key));
        Assert.False(tracker.IsInCooldown(key));
        Assert.Equal(0, tracker.GetStats(key).CooldownLevel);
    }

    [Fact]
    public void HalfOpen_Failure_ReCools()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var tracker = new ChainHealthTracker(time);
        var key = ChainHealthTracker.MakeKey(Hop("6.6.6.6", 1080));

        tracker.MarkDown(key); // 30s
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.True(tracker.AllowProbe(key));

        tracker.RecordProxyFailure(key, FailureReason.ProxyHandshakeFailure);
        Assert.True(tracker.IsInCooldown(key));
        Assert.False(tracker.IsHealthy(key));
        Assert.Equal(TimeSpan.FromMinutes(2),
            tracker.GetStats(key).CooldownUntilUtc!.Value - time.GetUtcNow());
    }

    [Fact]
    public void ExportImport_RoundTripsState()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var a = new ChainHealthTracker(time);
        var key = ChainHealthTracker.MakeKey(Hop("7.7.7.7", 9050));
        a.RecordSuccess(key);
        a.RecordProxyFailure(key, FailureReason.ConnectTimeout);
        a.MarkDown(key);

        var exported = a.ExportStates();
        var b = new ChainHealthTracker(time);
        b.ImportStates(exported);

        var sa = a.GetStats(key);
        var sb = b.GetStats(key);
        Assert.Equal(sa.SuccessCount, sb.SuccessCount);
        Assert.Equal(sa.FailureCount, sb.FailureCount);
        Assert.Equal(sa.IsInCooldown, sb.IsInCooldown);
        Assert.Equal(sa.CooldownLevel, sb.CooldownLevel);
    }

    [Fact]
    public void StrictMultiHop_GetActiveHops_NeverReorders()
    {
        var h1 = Hop("10.0.0.1", 1080);
        var h2 = Hop("10.0.0.2", 1080);
        var h3 = Hop("10.0.0.3", 1080);
        var profile = new ProxyChainProfile(
            Guid.NewGuid(),
            "strict",
            ChainMode.StrictMultiHop,
            new[] { h1, h2, h3 });

        var mgr = new ChainManager();
        mgr.SwitchProfile(profile);

        var hops = mgr.GetActiveHops();
        Assert.Equal(3, hops.Count);
        Assert.Equal(h1.Id, hops[0].Id);
        Assert.Equal(h2.Id, hops[1].Id);
        Assert.Equal(h3.Id, hops[2].Id);

        // Health noise must not reorder Strict hops.
        mgr.Health.RecordProxyFailure(h1, FailureReason.ConnectRefused);
        mgr.Health.RecordProxyFailure(h2, FailureReason.ConnectRefused);
        mgr.Health.MarkDown(h2);

        hops = mgr.GetActiveHops();
        Assert.Equal(new[] { h1.Id, h2.Id, h3.Id }, hops.Select(h => h.Id).ToArray());
    }
}
