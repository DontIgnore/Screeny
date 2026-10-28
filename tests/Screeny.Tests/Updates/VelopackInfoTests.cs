using Screeny.Updates;

namespace Screeny.Tests.Updates;

public class VelopackInfoTests
{
    [Fact]
    public void StubFor_ReturnsRootExeWithSameName_WhenItExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "ScreenyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "current"));
        try
        {
            var process = Path.Combine(root, "current", "Screeny.exe");
            Assert.Null(VelopackInfo.StubFor(root, process));
            File.WriteAllText(Path.Combine(root, "Screeny.exe"), "");
            Assert.Equal(Path.Combine(root, "Screeny.exe"), VelopackInfo.StubFor(root, process));
            Assert.Null(VelopackInfo.StubFor(root, null));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TestHost_IsNotManaged()
        => Assert.False(VelopackInfo.Detect().IsManaged);

    [Fact]
    public void EntryPoint_IsProgramMain()
    {
        // VelopackApp.Build().Run() должен выполняться до WPF: точка входа — Program.Main, а не сгенерированный App.Main.
        var entry = typeof(App).Assembly.EntryPoint;
        Assert.Equal(typeof(Program), entry?.DeclaringType);
        Assert.NotNull(entry!.GetCustomAttributes(typeof(STAThreadAttribute), false).SingleOrDefault());
    }
}
