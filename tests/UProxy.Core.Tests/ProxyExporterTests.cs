using UProxy.Core.Exporting;
using UProxy.Core.Models;

namespace UProxy.Core.Tests;

public class ProxyExporterTests
{
    [Fact]
    public async Task WriteProxyChainsAsync_WritesConfigAndMapsProtocols()
    {
        var path = Path.GetTempFileName();
        try
        {
            var results = new[]
            {
                Result("192.0.2.1", 8080, ProxyProtocol.Https),
                Result("192.0.2.2", 1080, ProxyProtocol.Socks4And5),
                Result("192.0.2.3", 1081, ProxyProtocol.Socks4)
            };

            var count = await ProxyExporter.WriteProxyChainsAsync(path, results, new ExportFilter());
            var config = await File.ReadAllTextAsync(path);

            Assert.Equal(3, count);
            Assert.Contains("dynamic_chain", config);
            Assert.Contains("proxy_dns", config);
            Assert.Contains("http\t192.0.2.1\t8080", config);
            Assert.Contains("socks5\t192.0.2.2\t1080", config);
            Assert.Contains("socks4\t192.0.2.3\t1081", config);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteProxyChainsAsync_OnlyIncludesCredentialsWhenRequested()
    {
        var path = Path.GetTempFileName();
        try
        {
            var result = Result("192.0.2.4", 1080, ProxyProtocol.Socks5, "alice", "secret");
            await ProxyExporter.WriteProxyChainsAsync(path, [result], new ExportFilter());
            Assert.DoesNotContain("alice", await File.ReadAllTextAsync(path));

            await ProxyExporter.WriteProxyChainsAsync(path, [result], new ExportFilter { IncludeCredentials = true });
            Assert.Contains("socks5\t192.0.2.4\t1080\talice\tsecret", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteProxyChainsAsync_RejectsCredentialsThatCannotBeTokens()
    {
        var path = Path.GetTempFileName();
        try
        {
            var result = Result("192.0.2.4", 1080, ProxyProtocol.Socks5, "alice smith", "secret");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ProxyExporter.WriteProxyChainsAsync(path, [result], new ExportFilter { IncludeCredentials = true }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ProxyCheckResult Result(
        string host,
        int port,
        ProxyProtocol protocol,
        string? username = null,
        string? password = null) => new()
    {
        Proxy = new ParsedProxy(host, port, protocol, username, password),
        IsAlive = true,
        ConfirmedProtocol = protocol
    };
}
