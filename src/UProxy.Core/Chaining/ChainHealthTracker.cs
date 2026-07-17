using UProxy.Core.Models;

namespace UProxy.Core.Chaining;

/// <summary>
/// Tracks per-proxy health by stable key (<c>host|port|kind</c>).
/// Only proxy-attributable failures count toward verification / cooldown.
/// </summary>
public sealed class ChainHealthTracker
{
    public static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(30);
    public const int FailuresBeforeVerify = 3;

    private static readonly TimeSpan[] CooldownSchedule =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30)
    ];

    private readonly TimeProvider _time;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ChainHealthTracker(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    public static string MakeKey(string host, int port, ProxyKind kind)
    {
        var h = host.Trim().TrimStart('[').TrimEnd(']').ToLowerInvariant();
        return $"{h}|{port}|{kind}";
    }

    public static string MakeKey(ProxyHop hop) =>
        MakeKey(hop.Proxy.Host, hop.Proxy.Port, hop.Kind);

    /// <summary>
    /// Reasons that indicate the proxy itself misbehaved (vs destination / cancel / TLS to target).
    /// </summary>
    public static bool IsProxyAttributable(FailureReason reason) => reason switch
    {
        FailureReason.None => false,
        FailureReason.Cancelled => false,
        // Single destination refuse / unreachable through an otherwise working proxy.
        FailureReason.TargetUnreachableThroughProxy => false,
        // Destination TLS / cert problems after the tunnel is up.
        FailureReason.TlsFailure => false,
        // Destination HTTP / judge outcomes (not proxy health).
        FailureReason.JudgeMismatch => false,
        FailureReason.JudgeUnavailable => false,
        FailureReason.EmptyResponse => false,
        _ => true
    };

    public void RecordSuccess(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var e = GetOrAdd(key);
            e.SuccessCount++;
            e.LastSuccessUtc = now;
            e.RecentFailures.Clear();
            e.NeedsVerification = false;
            e.CooldownUntilUtc = null;
            e.HalfOpen = false;
            e.CooldownLevel = 0;
        }
    }

    public void RecordSuccess(ProxyHop hop) => RecordSuccess(MakeKey(hop));

    /// <summary>
    /// Records a failure only when <paramref name="reason"/> is proxy-attributable.
    /// Destination errors, cancel, single dest refuse, and destination TLS are ignored.
    /// </summary>
    public void RecordProxyFailure(string key, FailureReason reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!IsProxyAttributable(reason))
            return;

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var e = GetOrAdd(key);
            e.FailureCount++;
            e.LastFailureUtc = now;

            if (e.HalfOpen)
            {
                // Failed half-open probe → re-enter cooldown (escalated).
                StartCooldown(e, now);
                return;
            }

            e.RecentFailures.RemoveAll(t => now - t > FailureWindow);
            e.RecentFailures.Add(now);
            if (e.RecentFailures.Count >= FailuresBeforeVerify)
                e.NeedsVerification = true;
        }
    }

    public void RecordProxyFailure(ProxyHop hop, FailureReason reason) =>
        RecordProxyFailure(MakeKey(hop), reason);

    /// <summary>Starts (or escalates) cooldown after a failed verification probe.</summary>
    public void MarkDown(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            StartCooldown(GetOrAdd(key), now);
        }
    }

    public void MarkDown(ProxyHop hop) => MarkDown(MakeKey(hop));

    public bool NeedsVerification(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var e))
                return false;
            var now = _time.GetUtcNow();
            return e.NeedsVerification && !e.HalfOpen && !IsCooldownActive(e, now);
        }
    }

    public bool NeedsVerification(ProxyHop hop) => NeedsVerification(MakeKey(hop));

    /// <summary>
    /// After cooldown expires, grants a single half-open probe. Returns false if still cooling,
    /// already probed while half-open, or not exiting a cooldown.
    /// </summary>
    public bool AllowProbe(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var e))
                return false;

            if (e.HalfOpen)
                return false; // one probe already granted for this half-open window

            if (e.CooldownUntilUtc is null)
                return false;

            if (now < e.CooldownUntilUtc.Value)
                return false;

            // Cooldown elapsed → half-open, grant one probe.
            e.CooldownUntilUtc = null;
            e.HalfOpen = true;
            e.NeedsVerification = false;
            return true;
        }
    }

    public bool AllowProbe(ProxyHop hop) => AllowProbe(MakeKey(hop));

    public bool IsHealthy(string key)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var e))
                return true; // unknown → assume healthy
            return !e.HalfOpen && !IsCooldownActive(e, now);
        }
    }

    public bool IsHealthy(ProxyHop hop) => IsHealthy(MakeKey(hop));

    public bool IsInCooldown(string key)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            return _entries.TryGetValue(key, out var e) && IsCooldownActive(e, now);
        }
    }

    public bool IsInCooldown(ProxyHop hop) => IsInCooldown(MakeKey(hop));

    public ProxyHealthRecord GetStats(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var e))
            {
                return new ProxyHealthRecord(
                    key, 0, 0, 1.0, true, false, false, 0, null, null, null);
            }

            var total = e.SuccessCount + e.FailureCount;
            var rate = total == 0 ? 1.0 : (double)e.SuccessCount / total;
            var cooling = IsCooldownActive(e, now);
            return new ProxyHealthRecord(
                key,
                e.SuccessCount,
                e.FailureCount,
                rate,
                !e.HalfOpen && !cooling,
                cooling,
                e.NeedsVerification && !e.HalfOpen && !cooling,
                e.CooldownLevel,
                e.LastSuccessUtc,
                e.LastFailureUtc,
                e.CooldownUntilUtc);
        }
    }

    public ProxyHealthRecord GetStats(ProxyHop hop) => GetStats(MakeKey(hop));

    public IReadOnlyList<ProxyHealthState> ExportStates()
    {
        lock (_gate)
        {
            return _entries.Values.Select(e => new ProxyHealthState(
                e.Key,
                e.SuccessCount,
                e.FailureCount,
                e.CooldownLevel,
                e.NeedsVerification,
                e.LastSuccessUtc,
                e.LastFailureUtc,
                e.CooldownUntilUtc,
                e.RecentFailures.Count > 0 ? e.RecentFailures.ToList() : null)).ToList();
        }
    }

    public void ImportStates(IEnumerable<ProxyHealthState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        lock (_gate)
        {
            foreach (var s in states)
            {
                if (string.IsNullOrWhiteSpace(s.Key))
                    continue;
                var e = GetOrAdd(s.Key);
                e.SuccessCount = s.SuccessCount;
                e.FailureCount = s.FailureCount;
                e.CooldownLevel = Math.Clamp(s.CooldownLevel, 0, CooldownSchedule.Length - 1);
                e.NeedsVerification = s.NeedsVerification;
                e.LastSuccessUtc = s.LastSuccessUtc;
                e.LastFailureUtc = s.LastFailureUtc;
                e.CooldownUntilUtc = s.CooldownUntilUtc;
                e.RecentFailures.Clear();
                if (s.RecentFailureUtc is { Count: > 0 })
                    e.RecentFailures.AddRange(s.RecentFailureUtc);
                e.HalfOpen = false;
            }
        }
    }

    /// <summary>Cooldown duration for the current escalation level (for tests / diagnostics).</summary>
    public static TimeSpan CooldownForLevel(int level)
    {
        var idx = Math.Clamp(level, 0, CooldownSchedule.Length - 1);
        return CooldownSchedule[idx];
    }

    private Entry GetOrAdd(string key)
    {
        if (_entries.TryGetValue(key, out var existing))
            return existing;
        var created = new Entry(key);
        _entries[key] = created;
        return created;
    }

    private static void StartCooldown(Entry e, DateTimeOffset now)
    {
        var idx = Math.Clamp(e.CooldownLevel, 0, CooldownSchedule.Length - 1);
        e.CooldownUntilUtc = now + CooldownSchedule[idx];
        // Escalate for the next MarkDown / half-open failure.
        if (e.CooldownLevel < CooldownSchedule.Length - 1)
            e.CooldownLevel++;

        e.NeedsVerification = false;
        e.HalfOpen = false;
        e.RecentFailures.Clear();
    }

    private static bool IsCooldownActive(Entry e, DateTimeOffset now) =>
        e.CooldownUntilUtc is { } until && now < until;

    private sealed class Entry(string key)
    {
        public string Key { get; } = key;
        public int SuccessCount;
        public int FailureCount;
        public int CooldownLevel;
        public bool NeedsVerification;
        public bool HalfOpen;
        public DateTimeOffset? LastSuccessUtc;
        public DateTimeOffset? LastFailureUtc;
        public DateTimeOffset? CooldownUntilUtc;
        public List<DateTimeOffset> RecentFailures { get; } = [];
    }
}
