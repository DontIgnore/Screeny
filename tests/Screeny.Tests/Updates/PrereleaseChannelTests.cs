using Screeny.Updates;

namespace Screeny.Tests.Updates;

public class PrereleaseChannelTests
{
    [Theory]
    [InlineData("0.3.0-alpha.1", true)]
    [InlineData("0.3.0-beta.2+abc123", true)]
    [InlineData("0.2.3", false)]
    [InlineData("0.2.3+a-b", false)]
    [InlineData(null, false)]
    public void IsPrerelease(string? version, bool expected)
        => Assert.Equal(expected, VelopackUpdateClient.IsPrerelease(version));
}
