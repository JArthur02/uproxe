using UProxy.Core.Checking;
using UProxy.Core.Models;

namespace UProxy.Core.Tests;

public class SocksConnectMsTests
{
    private static (bool Ok, FailureReason Failure, string? Error, int LatencyMs, int ConnectMs, ProxyAuthMethod Auth)
        Attempt(bool ok, int connectMs) =>
        (ok, ok ? FailureReason.None : FailureReason.ProxyHandshakeFailure, null, 1000, connectMs, ProxyAuthMethod.None);

    [Fact]
    public void PickSocksConnectMs_IgnoresZeroFromFailedSibling()
    {
        // SOCKS4 failed before TCP completed (ConnectMs=0); SOCKS5 succeeded in 42ms.
        // Old Math.Min(0, 42) wrongly reported 0 in the UI.
        var connect = ProxyChecker.PickSocksConnectMs(Attempt(false, 0), Attempt(true, 42));
        Assert.Equal(42, connect);
    }

    [Fact]
    public void PickSocksConnectMs_UsesFasterWhenBothSucceed()
    {
        Assert.Equal(30, ProxyChecker.PickSocksConnectMs(Attempt(true, 30), Attempt(true, 55)));
    }

    [Fact]
    public void PickSocksConnectMs_BothFailed_PrefersCompletedConnect()
    {
        Assert.Equal(80, ProxyChecker.PickSocksConnectMs(Attempt(false, 0), Attempt(false, 80)));
    }
}
