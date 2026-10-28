namespace Screeny.Tests;

public class SmokeTests
{
    [Fact]
    public void ProjectReferenceWorks() => Assert.NotNull(typeof(Screeny.App).Assembly);
}
