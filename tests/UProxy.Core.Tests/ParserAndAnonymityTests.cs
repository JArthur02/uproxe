using UProxy.Core.Checking;
using UProxy.Core.Models;
using UProxy.Core.Parsing;

namespace UProxy.Core.Tests;

public class ProxyParserTests
{
    [Theory]
    [InlineData("1.2.3.4:8080", true)]
    [InlineData("1.2.3.4:65535", true)]
    [InlineData("1.2.3.4:0", false)]
    [InlineData("1.2.3.4:65536", false)]
    [InlineData("256.1.1.1:80", false)]
    [InlineData("127.0.0.1:3128", true)]
    [InlineData("http://1.2.3.4:8080", true)]
    [InlineData("socks5://user:pass@1.2.3.4:1080", true)]
    [InlineData("[2001:db8::1]:8080", true)]
    [InlineData("not-a-proxy", false)]
    public void TryParse_Validates(string input, bool expected)
    {
        var ok = ProxyParser.TryParse(input, out var proxy);
        Assert.Equal(expected, ok);
        if (expected)
            Assert.NotNull(proxy);
    }

    [Fact]
    public void ExtractFromText_DedupesAndSkipsJunk()
    {
        var text = """
            # comment
            1.1.1.1:80
            foo 2.2.2.2:8080 bar
            1.1.1.1:80
            999.1.1.1:80
            """;
        var list = ProxyParser.ExtractFromText(text);
        Assert.Contains(list, p => p.Host == "1.1.1.1" && p.Port == 80);
        Assert.Contains(list, p => p.Host == "2.2.2.2" && p.Port == 8080);
        Assert.DoesNotContain(list, p => p.Host.StartsWith("999"));
    }

    [Fact]
    public void SchemeSetsProtocolAndAuth()
    {
        Assert.True(ProxyParser.TryParse("socks5://alice:secret@8.8.8.8:1080", out var p));
        Assert.Equal(ProxyProtocol.Socks5, p!.Protocol);
        Assert.Equal("alice", p.Username);
        Assert.Equal("secret", p.Password);
        Assert.DoesNotContain("secret", p.ToString());
    }
}

public class AnonymityClassifierTests
{
    private const string EliteBody = """
        HTTP_ACCEPT = */*
        HTTP_USER_AGENT = test
        HTTP_HOST = azenv.net
        REMOTE_ADDR = 203.0.113.50
        REMOTE_PORT = 12345
        """;

    private const string AnonBody = """
        HTTP_VIA = 1.1 proxy
        HTTP_USER_AGENT = test
        HTTP_HOST = azenv.net
        REMOTE_ADDR = 203.0.113.50
        """;

    private const string TransparentBody = """
        HTTP_X_FORWARDED_FOR = 198.51.100.10
        HTTP_USER_AGENT = test
        REMOTE_ADDR = 203.0.113.50
        """;

    [Fact]
    public void Elite_WhenNoProxyMarkers()
    {
        Assert.Equal(AnonymityLevel.Elite, AnonymityClassifier.Classify(EliteBody));
    }

    [Fact]
    public void Anonymous_WhenViaPresent()
    {
        Assert.Equal(AnonymityLevel.Anonymous, AnonymityClassifier.Classify(AnonBody));
    }

    [Fact]
    public void Transparent_WhenForwardedForPresent()
    {
        Assert.Equal(AnonymityLevel.Transparent, AnonymityClassifier.Classify(TransparentBody));
    }

    [Fact]
    public void Transparent_WhenRemoteAddrIsClient()
    {
        var client = System.Net.IPAddress.Parse("203.0.113.50");
        Assert.Equal(AnonymityLevel.Transparent, AnonymityClassifier.Classify(EliteBody, client));
    }

    [Fact]
    public void Unknown_WhenNotAzenv()
    {
        Assert.Equal(AnonymityLevel.Unknown, AnonymityClassifier.Classify("<html>captive portal</html>"));
    }

    [Fact]
    public void LooksLikeAzenv_RequiresRemoteAddr()
    {
        Assert.True(AnonymityClassifier.LooksLikeAzenv(EliteBody));
        Assert.False(AnonymityClassifier.LooksLikeAzenv("OK"));
    }
}
