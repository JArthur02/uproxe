using UProxy.Core.Config;

namespace UProxy.Core.Tests;

public class WindowPlacementTests
{
    private static readonly (int X, int Y, int Width, int Height)[] Primary =
        [(0, 0, 1920, 1080)];

    [Fact]
    public void TryRestore_ReturnsNull_WhenSizeMissing()
    {
        Assert.Null(WindowPlacement.TryRestore(10, 10, null, 600, false, Primary));
        Assert.Null(WindowPlacement.TryRestore(10, 10, 800, null, false, Primary));
    }

    [Fact]
    public void TryRestore_KeepsSize_WhenPositionOffScreen()
    {
        // Saved on a disconnected external monitor at x=2500.
        var restored = WindowPlacement.TryRestore(2500, 100, 1000, 700, false, Primary);
        Assert.NotNull(restored);
        Assert.Equal(1000, restored!.Value.Width);
        Assert.Equal(700, restored.Value.Height);
        // Caller recenters when Left/Top aren't on a screen; helper still returns size.
        Assert.Equal(0, restored.Value.Left);
        Assert.Equal(0, restored.Value.Top);
    }

    [Fact]
    public void TryRestore_PreservesOnScreenBounds()
    {
        var restored = WindowPlacement.TryRestore(100, 80, 1100, 680, true, Primary);
        Assert.NotNull(restored);
        Assert.Equal(100, restored!.Value.Left);
        Assert.Equal(80, restored.Value.Top);
        Assert.Equal(1100, restored.Value.Width);
        Assert.True(restored.Value.Maximized);
    }

    [Fact]
    public void Clamp_MigratesStrippedDefaultUserAgent()
    {
        var s = new AppSettings { UserAgent = "Proxy-Tool/2.0 (+https://github.com; privacy-respecting proxy checker)" };
        s.Clamp();
        Assert.Equal(UserAgents.Default, s.UserAgent);
    }
}
