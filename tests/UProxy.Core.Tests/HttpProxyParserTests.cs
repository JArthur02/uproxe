using System.Net;
using System.Net.Sockets;
using System.Text;
using UProxy.Core.Gateway;
using UProxy.Core.Models;

namespace UProxy.Core.Tests;

public class HttpProxyParserTests
{
    [Fact]
    public void Parse_RejectsHttpsAbsoluteForm()
    {
        var ex = Assert.Throws<HttpProxyParseException>(() =>
            HttpProxyRequestParser.Parse(
                "GET https://example.com/secret HTTP/1.1\r\nHost: example.com\r\n\r\n"));
        Assert.Contains("CONNECT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsBareLfRequestLine()
    {
        Assert.Throws<HttpProxyParseException>(() =>
            HttpProxyRequestParser.Parse("GET / HTTP/1.1\nHost: example.com\n\n"));
    }

    [Fact]
    public void Parse_RejectsBareCr()
    {
        Assert.Throws<HttpProxyParseException>(() =>
            HttpProxyRequestParser.Parse("GET / HTTP/1.1\rHost: example.com\r\n\r\n"));
    }

    [Fact]
    public void Parse_AcceptsStrictCrlf()
    {
        var parsed = HttpProxyRequestParser.Parse(
            "GET http://example.com:8080/a?b=1 HTTP/1.1\r\nHost: example.com:8080\r\n\r\n");
        Assert.Equal("GET", parsed.Method);
        Assert.Equal("example.com", parsed.Host);
        Assert.Equal(8080, parsed.Port);
        Assert.Equal("/a?b=1", parsed.OriginFormTarget);
        Assert.True(parsed.WasAbsoluteForm);
    }

    [Fact]
    public void BuildOriginForm_KeepsTransferEncoding_InjectsConnectionClose()
    {
        var parsed = HttpProxyRequestParser.Parse(
            "POST http://example.com/upload HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Proxy-Connection: keep-alive\r\n" +
            "Connection: keep-alive\r\n" +
            "TE: trailers\r\n" +
            "\r\n");
        var bytes = HttpProxyRequestParser.BuildOriginFormRequest(parsed);
        var text = Encoding.ASCII.GetString(bytes);
        Assert.Contains("Transfer-Encoding: chunked", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connection: close", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Proxy-Connection", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TE:", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("keep-alive", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetRequestBodyLengthPolicy_ChunkedAndContentLength()
    {
        var chunked = HttpProxyRequestParser.Parse(
            "POST http://example.com/ HTTP/1.1\r\nHost: example.com\r\nTransfer-Encoding: chunked\r\n\r\n");
        Assert.Equal(RequestBodyLengthKind.Chunked,
            HttpProxyRequestParser.GetRequestBodyLengthPolicy(chunked, out _));

        var withLen = HttpProxyRequestParser.Parse(
            "POST http://example.com/ HTTP/1.1\r\nHost: example.com\r\nContent-Length: 5\r\n\r\n");
        Assert.Equal(RequestBodyLengthKind.ContentLength,
            HttpProxyRequestParser.GetRequestBodyLengthPolicy(withLen, out var n));
        Assert.Equal(5, n);

        var get = HttpProxyRequestParser.Parse(
            "GET http://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\n");
        Assert.Equal(RequestBodyLengthKind.NoBody,
            HttpProxyRequestParser.GetRequestBodyLengthPolicy(get, out _));
    }
}
