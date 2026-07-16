using UProxy.Core.Config;

namespace UProxy.Core.Tests;

public class UserAgentAsciiTests
{
    [Fact]
    public void Default_And_Presets_AreAsciiOnly()
    {
        // A non-ASCII byte in any header value makes HttpClient throw on every request,
        // which previously broke all scraping and checking. Guard against reintroduction.
        Assert.All(UserAgents.Presets, p => Assert.True(IsAscii(p.Value), $"Preset '{p.Name}' is not ASCII: {p.Value}"));
        Assert.True(IsAscii(UserAgents.Default));
    }

    [Theory]
    [InlineData("μProxy-Tool/2.0", "Proxy-Tool/2.0")]
    [InlineData("Mozilla/5.0 (Windows)", "Mozilla/5.0 (Windows)")]
    public void AsciiSafe_StripsNonAsciiCharacters(string input, string expected) =>
        Assert.Equal(expected, UserAgents.AsciiSafe(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("μ")]      // becomes empty after stripping
    [InlineData(null)]
    public void AsciiSafe_FallsBackToDefault_WhenEmptyOrAllNonAscii(string? input) =>
        Assert.Equal(UserAgents.Default, UserAgents.AsciiSafe(input));

    [Fact]
    public void Clamp_SanitizesNonAsciiUserAgent()
    {
        var settings = new AppSettings { UserAgent = "μProxy-Tool/2.0" };
        settings.Clamp();
        Assert.True(IsAscii(settings.UserAgent));
        Assert.Equal("Proxy-Tool/2.0", settings.UserAgent);
    }

    private static bool IsAscii(string s) => s.All(c => c < 128);
}
