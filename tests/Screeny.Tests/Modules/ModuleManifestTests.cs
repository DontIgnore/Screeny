using System.Text;
using Screeny.Modules;

namespace Screeny.Tests.Modules;

public class ModuleManifestTests : IDisposable
{
    private readonly ModuleTestFiles _files = new();
    public void Dispose() => _files.Dispose();

    [Fact]
    public void Verify_IntactFolder_Passes()
        => Assert.Null(ModuleManifest.Verify(_files.Installed(_files.Dir, 1), "ocr-paddle", 1));

    [Fact]
    public void Verify_ChangedFile_FailsOnHash()
    {
        var dir = _files.Installed(_files.Dir, 1);
        File.WriteAllBytes(Path.Combine(dir, "det.onnx"), Encoding.ASCII.GetBytes("det model byteZ")); // та же длина
        Assert.Contains("SHA256 det.onnx", ModuleManifest.Verify(dir, "ocr-paddle", 1));
    }

    [Fact]
    public void Verify_TruncatedFile_FailsOnSize()
    {
        var dir = _files.Installed(_files.Dir, 1);
        File.WriteAllBytes(Path.Combine(dir, "rec.onnx"), [1, 2, 3]);
        Assert.Contains("размер rec.onnx", ModuleManifest.Verify(dir, "ocr-paddle", 1));
    }

    [Fact]
    public void Verify_MissingFile_Fails()
    {
        var dir = _files.Installed(_files.Dir, 1);
        File.Delete(Path.Combine(dir, "LICENSES", "NOTICE.txt"));
        Assert.Contains("нет файла LICENSES/NOTICE.txt", ModuleManifest.Verify(dir, "ocr-paddle", 1));
    }

    [Fact]
    public void Verify_ExtraFile_Fails()
    {
        var dir = _files.Installed(_files.Dir, 1);
        File.WriteAllText(Path.Combine(dir, "evil.dll"), "x");
        Assert.Contains("лишний файл evil.dll", ModuleManifest.Verify(dir, "ocr-paddle", 1));
    }

    [Theory]
    [InlineData("ocr-paddle", 2)]
    [InlineData("other", 1)]
    public void Verify_WrongIdOrVersion_Fails(string id, int version)
        => Assert.NotNull(ModuleManifest.Verify(_files.Installed(_files.Dir, 1), id, version));

    [Fact]
    public void Verify_PinnedManifest_CatchesConsistentTampering()
    {
        // Подменены и файл, и module.json под него: сам по себе манифест проверку прошёл бы, зашитый хеш — нет.
        var dir = _files.Installed(_files.Dir, 1);
        var pinned = ModuleManifest.Sha256Of(Path.Combine(dir, ModuleManifest.FileName));
        _files.Installed(_files.Dir, 1, new Dictionary<string, byte[]>(ModuleTestFiles.DefaultFiles) { ["det.onnx"] = [9, 9, 9] });
        Assert.Null(ModuleManifest.Verify(dir, "ocr-paddle", 1));
        Assert.Contains("module.json изменён", ModuleManifest.Verify(dir, "ocr-paddle", 1, pinned));
    }

    [Fact]
    public void Verify_PinnedManifest_IntactFolderPasses()
    {
        var dir = _files.Installed(_files.Dir, 1);
        Assert.Null(ModuleManifest.Verify(dir, "ocr-paddle", 1, ModuleManifest.Sha256Of(Path.Combine(dir, ModuleManifest.FileName)).ToUpperInvariant()));
    }

    [Fact]
    public void Verify_NoManifest_Fails()
    {
        var dir = _files.Installed(_files.Dir, 1);
        File.Delete(Path.Combine(dir, ModuleManifest.FileName));
        Assert.Contains(ModuleManifest.FileName, ModuleManifest.Verify(dir, "ocr-paddle", 1));
    }

    [Fact]
    public void Verify_CorruptManifest_Fails()
    {
        var dir = _files.Installed(_files.Dir, 1);
        File.WriteAllText(Path.Combine(dir, ModuleManifest.FileName), "{ not json");
        Assert.NotNull(ModuleManifest.Verify(dir, "ocr-paddle", 1));
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("LICENSES/../../outside.dll")]
    [InlineData("C:/Windows/evil.dll")]
    public void Verify_ManifestPathOutsideFolder_Fails(string path)
    {
        var dir = Path.Combine(_files.Dir, "ocr-paddle-1");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, ModuleManifest.FileName),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new ModuleManifest("ocr-paddle", 1, [new ModuleFile(path, 1, new string('0', 64))])));
        Assert.Contains("недопустимый путь", ModuleManifest.Verify(dir, "ocr-paddle", 1));
    }

    [Theory]
    [InlineData("det.onnx", true)]
    [InlineData("LICENSES/NOTICE.txt", true)]
    [InlineData("LICENSES\\NOTICE.txt", true)]
    [InlineData("../x", false)]
    [InlineData("a/../../x", false)]
    [InlineData("a/./x", false)]
    [InlineData("/etc/x", false)]
    [InlineData("\\x", false)]
    [InlineData("C:\\x", false)]
    [InlineData("C:x", false)]
    [InlineData("\\\\server\\share\\x", false)]
    [InlineData("", false)]
    [InlineData("..", false)]
    public void SafeTarget_AcceptsOnlyPathsInside(string name, bool ok)
    {
        var target = ModuleInstaller.SafeTarget(_files.Dir, name);
        Assert.Equal(ok, target is not null);
        if (target is not null) Assert.StartsWith(Path.GetFullPath(_files.Dir), target);
    }
}
