using UProxy.Core.Checking;
using UProxy.Core.Config;
using UProxy.Core.Models;

namespace UProxy.Core.Tests;

public class DiagnosticsMappingTests
{
    [Theory]
    [InlineData(FailureReason.TargetUnreachableThroughProxy)]
    [InlineData(FailureReason.HttpsConnectForbidden)]
    public void FailureMessages_HaveDistinctTextForNewReasons(FailureReason reason)
    {
        var msg = FailureMessages.Describe(reason);
        Assert.False(string.IsNullOrWhiteSpace(msg));
        Assert.NotEqual(reason.ToString(), msg); // a real sentence, not the enum name
    }

    [Fact]
    public void UserAgentPresets_AreDistinctAndNonEmpty()
    {
        Assert.NotEmpty(UserAgents.Presets);
        Assert.Contains(UserAgents.Presets, p => p.Value == UserAgents.Default);
        Assert.All(UserAgents.Presets, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.Value));
        });
        var values = UserAgents.Presets.Select(p => p.Value).ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
    }
}
