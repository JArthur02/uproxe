using UProxy.Core.Chaining;
using UProxy.Core.Models;
using UProxy.Core.Persistence;

namespace UProxy.Core.Tests;

public class PersistenceStoreTests
{
    private static (AppDataLayout layout, ProtectedCredentialStore creds, ChainProfileStore chains, PoolStore pools, HealthStore health) CreateStores()
    {
        var root = Path.Combine(Path.GetTempPath(), "uproxy-persist-" + Guid.NewGuid().ToString("N"));
        var layout = new AppDataLayout(root);
        layout.EnsureDirectories();
        var creds = new ProtectedCredentialStore(layout);
        var chains = new ChainProfileStore(layout, creds);
        var pools = new PoolStore(layout, creds);
        var health = new HealthStore(layout);
        return (layout, creds, chains, pools, health);
    }

    private static void Cleanup(AppDataLayout layout)
    {
        try
        {
            if (Directory.Exists(layout.Root))
                Directory.Delete(layout.Root, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    private static ProxyHop HopWithAuth(
        Guid id,
        string host,
        int port,
        ProxyKind kind,
        string user,
        string pass) =>
        new(
            id,
            new ParsedProxy(host, port, PersistenceProtocol(kind), user, pass),
            kind,
            ProxyTransport.Tcp,
            ProxyHop.DefaultCapabilities(kind),
            true);

    private static ProxyProtocol PersistenceProtocol(ProxyKind kind) => kind switch
    {
        ProxyKind.Http => ProxyProtocol.Http,
        ProxyKind.Socks4 => ProxyProtocol.Socks4,
        _ => ProxyProtocol.Socks5
    };

    [Fact]
    public void ChainProfile_RoundTrip_MergesCredentials_AndPreservesHopIds()
    {
        var (layout, _, chains, _, _) = CreateStores();
        try
        {
            var hop1Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            var hop2Id = Guid.Parse("11111111-2222-3333-4444-555555555555");
            var profile = new ProxyChainProfile(
                Guid.Parse("99999999-8888-7777-6666-555555555555"),
                "My Chain",
                ChainMode.StrictMultiHop,
                [
                    HopWithAuth(hop1Id, "10.0.0.1", 1080, ProxyKind.Socks5, "alice", "s3cret"),
                    HopWithAuth(hop2Id, "10.0.0.2", 8080, ProxyKind.Http, "bob", "p@ss")
                ]);

            chains.Save(profile);

            // Fresh store instances (simulate app restart).
            var layout2 = new AppDataLayout(layout.Root);
            var creds2 = new ProtectedCredentialStore(layout2);
            var chains2 = new ChainProfileStore(layout2, creds2);
            var loaded = chains2.Load("My Chain");

            Assert.NotNull(loaded);
            Assert.Equal(profile.Id, loaded!.Id);
            Assert.Equal("My Chain", loaded.Name);
            Assert.Equal(ChainMode.StrictMultiHop, loaded.Mode);
            Assert.Equal(2, loaded.Hops.Count);
            Assert.Equal(hop1Id, loaded.Hops[0].Id);
            Assert.Equal(hop2Id, loaded.Hops[1].Id);
            Assert.Equal("alice", loaded.Hops[0].Proxy.Username);
            Assert.Equal("s3cret", loaded.Hops[0].Proxy.Password);
            Assert.Equal("bob", loaded.Hops[1].Proxy.Username);
            Assert.Equal("p@ss", loaded.Hops[1].Proxy.Password);
            Assert.Equal("10.0.0.1", loaded.Hops[0].Proxy.Host);
            Assert.Equal(8080, loaded.Hops[1].Proxy.Port);
            Assert.Equal(ProxyKind.Socks5, loaded.Hops[0].Kind);
            Assert.Equal(ProxyKind.Http, loaded.Hops[1].Kind);
        }
        finally
        {
            Cleanup(layout);
        }
    }

    [Fact]
    public void ChainProfile_Json_DoesNotContainPlaintextCredentials()
    {
        var (layout, _, chains, _, _) = CreateStores();
        try
        {
            var hopId = Guid.NewGuid();
            var profile = new ProxyChainProfile(
                Guid.NewGuid(),
                "Secret Chain",
                ChainMode.FastFailover,
                [HopWithAuth(hopId, "1.2.3.4", 1080, ProxyKind.Socks5, "superuser", "SuperSecretPass99")]);

            chains.Save(profile);

            var json = File.ReadAllText(chains.ProfilePath("Secret Chain"));
            Assert.DoesNotContain("superuser", json, StringComparison.Ordinal);
            Assert.DoesNotContain("SuperSecretPass99", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"username\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"password\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("1.2.3.4", json, StringComparison.Ordinal);
            Assert.Contains(hopId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(layout);
        }
    }

    [Fact]
    public void ChainProfile_Delete_RemovesHopCredentials()
    {
        var (layout, creds, chains, _, _) = CreateStores();
        try
        {
            var hopId = Guid.NewGuid();
            var key = ProtectedCredentialStore.CredentialKeyForHop(hopId);
            var profile = new ProxyChainProfile(
                Guid.NewGuid(),
                "Temp",
                ChainMode.FastFailover,
                [HopWithAuth(hopId, "9.9.9.9", 1080, ProxyKind.Socks5, "u", "p")]);

            chains.Save(profile);
            Assert.NotNull(creds.Get(key));

            Assert.True(chains.Delete("Temp"));
            Assert.False(File.Exists(chains.ProfilePath("Temp")));
            Assert.Null(creds.Get(key));
        }
        finally
        {
            Cleanup(layout);
        }
    }

    [Fact]
    public void ChainProfile_Save_RemovesCredentialsForDeletedHops()
    {
        var (layout, creds, chains, _, _) = CreateStores();
        try
        {
            var hopA = Guid.NewGuid();
            var hopB = Guid.NewGuid();
            var keyA = ProtectedCredentialStore.CredentialKeyForHop(hopA);
            var keyB = ProtectedCredentialStore.CredentialKeyForHop(hopB);

            chains.Save(new ProxyChainProfile(
                Guid.NewGuid(),
                "Shrink",
                ChainMode.FastFailover,
                [
                    HopWithAuth(hopA, "1.1.1.1", 1080, ProxyKind.Socks5, "a", "pa"),
                    HopWithAuth(hopB, "2.2.2.2", 1080, ProxyKind.Socks5, "b", "pb")
                ]));

            Assert.NotNull(creds.Get(keyA));
            Assert.NotNull(creds.Get(keyB));

            chains.Save(new ProxyChainProfile(
                Guid.NewGuid(),
                "Shrink",
                ChainMode.FastFailover,
                [HopWithAuth(hopA, "1.1.1.1", 1080, ProxyKind.Socks5, "a", "pa")]));

            Assert.NotNull(creds.Get(keyA));
            Assert.Null(creds.Get(keyB));
        }
        finally
        {
            Cleanup(layout);
        }
    }

    [Fact]
    public void ProtectedCredentialStore_AtomicWrite_ProducesProtectedBlob()
    {
        var (layout, creds, _, _, _) = CreateStores();
        try
        {
            creds.Set("rec1", "user", "pass");
            Assert.True(File.Exists(layout.ProtectedCredentialsPath));

            var bytes = File.ReadAllBytes(layout.ProtectedCredentialsPath);
            Assert.True(bytes.Length > 1);
            // Linux CI uses FormatDevAes; Windows would use FormatDpapi.
            Assert.True(
                bytes[0] == ProtectedCredentialStore.FormatDevAes ||
                bytes[0] == ProtectedCredentialStore.FormatDpapi);

            // Payload after the format byte must not be plaintext JSON.
            var payload = System.Text.Encoding.UTF8.GetString(bytes.AsSpan(1));
            Assert.False(payload.TrimStart().StartsWith('{'), "Credential blob should not be plaintext JSON.");

            Assert.Equal(1, creds.DeleteMany(["rec1", "missing"]));
            Assert.Null(creds.Get("rec1"));
        }
        finally
        {
            Cleanup(layout);
        }
    }

    [Fact]
    public void PoolStore_RoundTrip_WithoutPlaintextPasswords()
    {
        var (layout, _, _, pools, _) = CreateStores();
        try
        {
            var hopId = Guid.NewGuid();
            var checkedAt = DateTimeOffset.Parse("2026-07-01T12:00:00Z");
            var candidates = new List<PoolCandidate>
            {
                new(
                    HopWithAuth(hopId, "5.5.5.5", 1080, ProxyKind.Socks5, "pooluser", "poolpass"),
                    Country: "US",
                    LatencyMs: 42,
                    SuccessRate: 0.95,
                    LastChecked: checkedAt)
            };

            pools.Save("Primary", candidates);

            var json = File.ReadAllText(pools.PoolPath("Primary"));
            Assert.DoesNotContain("pooluser", json, StringComparison.Ordinal);
            Assert.DoesNotContain("poolpass", json, StringComparison.Ordinal);

            var layout2 = new AppDataLayout(layout.Root);
            var creds2 = new ProtectedCredentialStore(layout2);
            var pools2 = new PoolStore(layout2, creds2);
            var loaded = pools2.Load("Primary");

            Assert.NotNull(loaded);
            Assert.Single(loaded!);
            Assert.Equal(hopId, loaded[0].Hop.Id);
            Assert.Equal("5.5.5.5", loaded[0].Hop.Proxy.Host);
            Assert.Equal("pooluser", loaded[0].Hop.Proxy.Username);
            Assert.Equal("poolpass", loaded[0].Hop.Proxy.Password);
            Assert.Equal("US", loaded[0].Country);
            Assert.Equal(42, loaded[0].LatencyMs);
            Assert.Equal(0.95, loaded[0].SuccessRate);
            Assert.Equal(checkedAt, loaded[0].LastChecked);
        }
        finally
        {
            Cleanup(layout);
        }
    }

    [Fact]
    public void HealthStore_RoundTrip()
    {
        var (layout, _, _, _, health) = CreateStores();
        try
        {
            var time = TimeProvider.System;
            var tracker = new ChainHealthTracker(time);
            var key = ChainHealthTracker.MakeKey("8.8.8.8", 1080, ProxyKind.Socks5);
            tracker.RecordSuccess(key);
            tracker.RecordSuccess(key);
            tracker.RecordProxyFailure(key, FailureReason.ProxyHandshakeFailure);

            health.Save(tracker);

            Assert.True(File.Exists(layout.ProxyHealthPath));

            var tracker2 = new ChainHealthTracker(time);
            health.LoadInto(tracker2);
            var stats = tracker2.GetStats(key);
            Assert.Equal(2, stats.SuccessCount);
            Assert.Equal(1, stats.FailureCount);

            var states = health.LoadStates();
            Assert.Contains(states, s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Cleanup(layout);
        }
    }

    [Fact]
    public void AppDataLayout_UsesInjectableRoot_AndSafeStem()
    {
        var root = Path.Combine(Path.GetTempPath(), "uproxy-layout-" + Guid.NewGuid().ToString("N"));
        var layout = new AppDataLayout(root);
        try
        {
            Assert.Equal(Path.GetFullPath(root), layout.Root);
            Assert.Equal(Path.Combine(layout.Root, "chains"), layout.ChainsDirectory);
            Assert.Equal(Path.Combine(layout.Root, "credentials", "protected.dat"), layout.ProtectedCredentialsPath);
            Assert.Equal(Path.Combine(layout.Root, "health", "proxy-health.json"), layout.ProxyHealthPath);
            Assert.Equal("My_Chain", AppDataLayout.SafeFileStem("My/Chain"));
            layout.EnsureDirectories();
            Assert.True(Directory.Exists(layout.ChainsDirectory));
            Assert.True(Directory.Exists(layout.PoolsDirectory));
            Assert.True(Directory.Exists(layout.CredentialsDirectory));
            Assert.True(Directory.Exists(layout.HealthDirectory));
        }
        finally
        {
            Cleanup(layout);
        }
    }
}
