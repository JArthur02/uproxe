using System.Net;
using UProxy.Core.Checking;
using UProxy.Core.Config;
using UProxy.Core.Models;

namespace UProxy.Core.Tests;

public class JudgeClientTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public int Calls { get; private set; }

        public CountingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body)
            });
        }
    }

    [Fact]
    public async Task Judges_AreDeduped_AndInjectedHandlerIsReusedAcrossJudges()
    {
        var settings = new AppSettings
        {
            JudgeUrl = "http://azenv.net",
            FallbackJudgeUrls =
            [
                "http://azenv.net/",
                "https://www.proxyjudge.info/"
            ]
        };

        // Not an azenv-style body → every judge is a JudgeMismatch, so all are tried.
        var handler = new CountingHandler(HttpStatusCode.OK, "<html>captive portal</html>");
        var client = new JudgeClient(settings, handler);

        var (body, failure, _, _) = await client.FetchThroughHttpProxyAsync(
            new ParsedProxy("1.2.3.4", 8080, ProxyProtocol.Http), CancellationToken.None);

        Assert.Null(body);
        Assert.Equal(FailureReason.JudgeMismatch, failure);
        // "http://azenv.net" and "http://azenv.net/" normalize to one judge → 2 distinct judges.
        // Calls == 2 also proves the injected handler was reused (not disposed after judge #1).
        Assert.Equal(2, handler.Calls);
    }
}
