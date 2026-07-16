using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using UProxy.Core.Checking;
using UProxy.Core.Dns;
using UProxy.Core.Models;
using UProxy.Core.Parsing;

namespace UProxy.Core.Tests;

public class FakeIpDnsTests
{
    [Fact]
    public void Allocate_UsesProxifierStylePrefix()
    {
        var dns = new FakeIpDns();
        var ip = dns.Allocate("azenv.net");
        Assert.True(FakeIpDns.IsFakeIp(ip));
        Assert.StartsWith("127.8.", ip.ToString());
        Assert.True(dns.TryGetHostname(ip, out var host));
        Assert.Equal("azenv.net", host);
    }

    [Fact]
    public void SameHost_GetsSameFakeIp()
    {
        var dns = new FakeIpDns();
        var a = dns.Allocate("example.com");
        var b = dns.Allocate("EXAMPLE.com");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ResolveDestinationForProxy_ReversesFakeIp()
    {
        var dns = new FakeIpDns();
        var fake = dns.Allocate("judge.example");
        var dest = dns.ResolveDestinationForProxy(fake.ToString(), resolveHostnamesThroughProxy: true);
        Assert.Equal("judge.example", dest);
    }
}

public class ProxyAuthTests
{
    [Fact]
    public void FormatBasicHeader_MatchesProxyCheckerStyle()
    {
        var header = ProxyAuth.FormatBasicHeader("alice", "secret");
        Assert.StartsWith("Basic ", header);
        var b64 = header["Basic ".Length..];
        Assert.Equal("alice:secret", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
    }

    [Fact]
    public void Classify_DetectsNtlmWithoutSending()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired);
        response.Headers.ProxyAuthenticate.Add(new AuthenticationHeaderValue("NTLM"));
        var (method, detail) = ProxyAuth.ClassifyProxyAuthenticate(response);
        Assert.Equal(ProxyAuthMethod.NtlmRequired, method);
        Assert.Contains("privacy", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_DetectsBasic()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired);
        response.Headers.ProxyAuthenticate.Add(new AuthenticationHeaderValue("Basic", "realm=\"proxy\""));
        var (method, _) = ProxyAuth.ClassifyProxyAuthenticate(response);
        Assert.Equal(ProxyAuthMethod.Basic, method);
    }

    [Fact]
    public void Parser_KeepsEmbeddedCredentials()
    {
        Assert.True(ProxyParser.TryParse("socks5://user:pass@1.2.3.4:1080", out var p));
        Assert.NotNull(p);
        Assert.Equal("user", p!.Username);
        Assert.Equal("pass", p.Password);
        Assert.Equal(ProxyProtocol.Socks5, p.Protocol);
    }

    [Fact]
    public void FailureMessages_DnsHintsSocks4a()
    {
        var msg = FailureMessages.Describe(FailureReason.DnsFailure);
        Assert.Contains("SOCKS4a", msg, StringComparison.OrdinalIgnoreCase);
    }
}
