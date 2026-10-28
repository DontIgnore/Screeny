using System.Net;
using System.Net.Http;
using System.Text;
using Screeny.Modules;

namespace Screeny.Tests.Modules;

/// <summary>Установка модуля: загрузка через подставной <see cref="HttpMessageHandler"/>, проверки, уборка за собой.</summary>
public class ModuleInstallerTests : IDisposable
{
    private readonly ModuleTestFiles _files = new();
    private string Root => Path.Combine(_files.Dir, "Modules");
    private static readonly Uri Url = new("https://example.invalid/ocr-module-1/Screeny-OcrModule-1.zip");

    public void Dispose() => _files.Dispose();

    /// <summary>Отвечает на любой запрос заданным статусом и телом (поток — чтобы проверять отмену посреди загрузки).</summary>
    private sealed class FakeHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(respond());
        }
    }

    private static HttpModuleSource Source(byte[] body, HttpStatusCode status = HttpStatusCode.OK)
        => new(new HttpClient(new FakeHandler(() => new HttpResponseMessage(status) { Content = new ByteArrayContent(body) })), Url);

    /// <summary>Отдаёт первый кусок, а дальше ждёт до отмены.</summary>
    private sealed class StallingStream(byte[] first, long length) : Stream
    {
        private bool _sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_sent) { _sent = true; first.CopyTo(buffer); return first.Length; }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>В корне модулей не осталось временных файлов и папок установки.</summary>
    private void AssertNoTemporaries()
    {
        if (!Directory.Exists(Root)) return;
        Assert.Empty(Directory.EnumerateFiles(Root, ".download-*"));
        Assert.Empty(Directory.EnumerateDirectories(Root, ".staging-*"));
    }

    [Fact]
    public async Task Install_VerifiesAndMovesIntoVersionFolder()
    {
        var zip = ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles);
        var installer = new ModuleInstaller(Root, ModuleTestFiles.SpecFor(zip));
        var progress = new List<int>();
        var dir = await installer.InstallAsync(Source(zip), progress.Add, CancellationToken.None);

        Assert.Equal(Path.Combine(Root, "ocr-paddle-1"), dir);
        Assert.Equal("det model bytes", File.ReadAllText(Path.Combine(dir, "det.onnx")));
        Assert.Null(ModuleManifest.Verify(dir, "ocr-paddle", 1));
        Assert.Equal(1, ModuleInstaller.DetectInstalled(Root, "ocr-paddle", 1));
        Assert.Equal(0, progress[0]);
        Assert.Equal(100, progress[^1]);
        Assert.Equal(progress.OrderBy(p => p), progress); // не убывает
        AssertNoTemporaries();
    }

    [Fact]
    public async Task Install_ZipHashMismatch_IsRejectedAndLeavesNothing()
    {
        var zip = ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles);
        var installer = new ModuleInstaller(Root, new ModuleSpec("ocr-paddle", 1, new string('a', 64)));
        var ex = await Assert.ThrowsAsync<ModuleInstallException>(() => installer.InstallAsync(Source(zip), null, CancellationToken.None));
        Assert.Contains("SHA256", ex.Message);
        Assert.False(Directory.Exists(installer.TargetFolder));
        AssertNoTemporaries();
    }

    [Fact]
    public async Task Install_CancelMidDownload_DeletesPartialFile()
    {
        var zip = ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles);
        var installer = new ModuleInstaller(Root, ModuleTestFiles.SpecFor(zip));
        using var cts = new CancellationTokenSource();
        var http = new HttpClient(new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream(zip[..100], zip.Length)) { Headers = { ContentLength = zip.Length } },
        }));
        var progress = new List<int>();
        var install = installer.InstallAsync(new HttpModuleSource(http, Url), p => { progress.Add(p); if (p > 0) cts.Cancel(); }, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        Assert.Contains(progress, p => p is > 0 and < 100);
        Assert.False(Directory.Exists(installer.TargetFolder));
        AssertNoTemporaries();
    }

    [Fact]
    public async Task Install_AlreadyCancelled_DoesNotInstall()
    {
        var zip = ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles);
        var installer = new ModuleInstaller(Root, ModuleTestFiles.SpecFor(zip));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(Source(zip), null, new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(installer.TargetFolder));
        AssertNoTemporaries();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "404")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task Install_HttpError_IsReported(HttpStatusCode status, string text)
    {
        var installer = new ModuleInstaller(Root, new ModuleSpec("ocr-paddle", 1, new string('a', 64)));
        var ex = await Assert.ThrowsAsync<ModuleInstallException>(() => installer.InstallAsync(Source([], status), null, CancellationToken.None));
        Assert.Contains(text, ex.Message);
        AssertNoTemporaries();
    }

    [Fact]
    public async Task Install_TooLargeResponse_IsRejected()
    {
        var zip = ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles);
        var installer = new ModuleInstaller(Root, ModuleTestFiles.SpecFor(zip) with { MaxZipBytes = 100 });
        await Assert.ThrowsAsync<ModuleInstallException>(() => installer.InstallAsync(Source(zip), null, CancellationToken.None));
        AssertNoTemporaries();
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("LICENSES/../../evil.txt")]
    [InlineData("/evil.txt")]
    [InlineData("C:/evil.txt")]
    public async Task Install_ZipSlipEntry_IsRejected(string entry)
    {
        // Хеш архива верный (как будто подменили архив на сервере и хеш в приложении): защита всё равно срабатывает.
        var zip = ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles, extra: [new(entry, Encoding.ASCII.GetBytes("pwned"))]);
        var installer = new ModuleInstaller(Root, ModuleTestFiles.SpecFor(zip));
        var ex = await Assert.ThrowsAsync<ModuleInstallException>(() => installer.InstallAsync(Source(zip), null, CancellationToken.None));
        Assert.Contains("Недопустимый путь", ex.Message);
        Assert.False(File.Exists(Path.Combine(_files.Dir, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Root, "evil.txt")));
        Assert.False(Directory.Exists(installer.TargetFolder));
        AssertNoTemporaries();
    }

    [Fact]
    public async Task Install_FileNotMatchingManifest_IsRejected()
    {
        var files = new Dictionary<string, byte[]>(ModuleTestFiles.DefaultFiles);
        var manifest = ModuleTestFiles.Manifest("ocr-paddle", 1, files);
        files["det.onnx"] = Encoding.ASCII.GetBytes("tampered model!"); // та же длина, другой хеш
        var zip = ModuleTestFiles.Zip(files, manifest);
        var installer = new ModuleInstaller(Root, ModuleTestFiles.SpecFor(zip));
        var ex = await Assert.ThrowsAsync<ModuleInstallException>(() => installer.InstallAsync(Source(zip), null, CancellationToken.None));
        Assert.Contains("SHA256 det.onnx", ex.Message);
        Assert.False(Directory.Exists(installer.TargetFolder));
        AssertNoTemporaries();
    }

    [Fact]
    public async Task Install_ExtraFileInArchive_IsRejected()
    {
        var zip = ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles, extra: [new("unlisted.dll", [1, 2, 3])]);
        var installer = new ModuleInstaller(Root, ModuleTestFiles.SpecFor(zip));
        var ex = await Assert.ThrowsAsync<ModuleInstallException>(() => installer.InstallAsync(Source(zip), null, CancellationToken.None));
        Assert.Contains("лишний файл unlisted.dll", ex.Message);
    }

    [Fact]
    public async Task Install_WrongManifestVersion_IsRejected()
    {
        var zip = ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles, version: 2);
        var installer = new ModuleInstaller(Root, ModuleTestFiles.SpecFor(zip, version: 1));
        await Assert.ThrowsAsync<ModuleInstallException>(() => installer.InstallAsync(Source(zip), null, CancellationToken.None));
        Assert.Null(ModuleInstaller.DetectInstalled(Root, "ocr-paddle", 1));
    }

    [Fact]
    public async Task Install_NewVersion_RemovesOlderOne()
    {
        _files.Installed(Root, 1);
        var zip = ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles, version: 2);
        var installer = new ModuleInstaller(Root, ModuleTestFiles.SpecFor(zip, version: 2));
        await installer.InstallAsync(Source(zip), null, CancellationToken.None);
        Assert.Equal([2], ModuleInstaller.InstalledVersions(Root, "ocr-paddle"));
    }

    [Fact]
    public async Task Install_ReplacesBrokenFolderOfSameVersion()
    {
        var broken = _files.Installed(Root, 1);
        File.WriteAllText(Path.Combine(broken, "det.onnx"), "corrupted");
        var zip = ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles);
        var installer = new ModuleInstaller(Root, ModuleTestFiles.SpecFor(zip));
        await installer.InstallAsync(Source(zip), null, CancellationToken.None);
        Assert.Null(ModuleManifest.Verify(installer.TargetFolder, "ocr-paddle", 1));
    }

    [Fact]
    public async Task FolderSource_ReadsZipFromLocalFolder()
    {
        var zip = ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles);
        var src = Path.Combine(_files.Dir, "rel");
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "Screeny-OcrModule-1.zip"), zip);
        var installer = new ModuleInstaller(Root, ModuleTestFiles.SpecFor(zip));
        await installer.InstallAsync(new FolderModuleSource(src, "Screeny-OcrModule-1.zip"), null, CancellationToken.None);
        Assert.Equal(1, ModuleInstaller.DetectInstalled(Root, "ocr-paddle", 1));
    }

    [Fact]
    public async Task FolderSource_MissingZip_IsReported()
    {
        var installer = new ModuleInstaller(Root, new ModuleSpec("ocr-paddle", 1, new string('a', 64)));
        var ex = await Assert.ThrowsAsync<ModuleInstallException>(() => installer.InstallAsync(new FolderModuleSource(_files.Dir, "none.zip"), null, CancellationToken.None));
        Assert.Contains("none.zip", ex.Message);
    }

    [Fact]
    public void DetectInstalled_PrefersRequiredVersion_ElseNewest_IgnoresBroken()
    {
        Assert.Null(ModuleInstaller.DetectInstalled(Root, "ocr-paddle", 1)); // папки нет
        _files.Installed(Root, 1);
        _files.Installed(Root, 3);
        Directory.CreateDirectory(Path.Combine(Root, "ocr-paddle-7")); // без module.json
        Directory.CreateDirectory(Path.Combine(Root, "ocr-paddle-x"));
        Assert.Equal(1, ModuleInstaller.DetectInstalled(Root, "ocr-paddle", 1));
        Assert.Equal(3, ModuleInstaller.DetectInstalled(Root, "ocr-paddle", 2));
    }

    [Fact]
    public void DetectInstalled_FolderNameMustMatchManifestVersion()
    {
        var dir = _files.Installed(Root, 1);
        Directory.Move(dir, Path.Combine(Root, "ocr-paddle-2"));
        Assert.Empty(ModuleInstaller.InstalledVersions(Root, "ocr-paddle"));
    }

    [Fact]
    public void Remove_DeletesFolder()
    {
        _files.Installed(Root, 1);
        var installer = new ModuleInstaller(Root, new ModuleSpec("ocr-paddle", 1, ""));
        Assert.Equal(RemoveResult.Removed, installer.Remove());
        Assert.False(Directory.Exists(installer.TargetFolder));
        Assert.Equal(RemoveResult.NotInstalled, installer.Remove());
    }

    [Fact]
    public void Remove_FolderInUse_IsMarkedThenCleanedOnStartup()
    {
        var dir = _files.Installed(Root, 1);
        var installer = new ModuleInstaller(Root, new ModuleSpec("ocr-paddle", 1, ""));
        Assert.Equal(RemoveResult.PendingRestart, installer.Remove(inUse: d => d == dir));
        Assert.True(installer.RemovalPending);
        Assert.True(File.Exists(Path.Combine(dir, "det.onnx"))); // файлы целы: их держит загруженная библиотека
        Assert.Null(ModuleInstaller.DetectInstalled(Root, "ocr-paddle", 1)); // но модуль уже не считается установленным

        installer.CleanupOnStartup();
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Restore_UndoesPendingRemoval()
    {
        var dir = _files.Installed(Root, 1);
        var installer = new ModuleInstaller(Root, new ModuleSpec("ocr-paddle", 1, ""));
        installer.Remove(inUse: _ => true);
        Assert.True(installer.Restore());
        Assert.False(installer.RemovalPending);
        Assert.Equal(1, ModuleInstaller.DetectInstalled(Root, "ocr-paddle", 1));
        Assert.Null(ModuleManifest.Verify(dir, "ocr-paddle", 1));
    }

    [Fact]
    public void Restore_BrokenFiles_StaysPending()
    {
        var dir = _files.Installed(Root, 1);
        var installer = new ModuleInstaller(Root, new ModuleSpec("ocr-paddle", 1, ""));
        installer.Remove(inUse: _ => true);
        File.Delete(Path.Combine(dir, "rec.onnx"));
        Assert.False(installer.Restore());
        Assert.True(installer.RemovalPending);
    }

    [Fact]
    public void CleanupOnStartup_RemovesLeftoversOfInterruptedInstalls()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".staging-abc"));
        File.WriteAllText(Path.Combine(Root, ".download-abc.zip"), "partial");
        var kept = _files.Installed(Root, 1);
        new ModuleInstaller(Root, new ModuleSpec("ocr-paddle", 1, "")).CleanupOnStartup();
        AssertNoTemporaries();
        Assert.True(Directory.Exists(kept));
    }

    [Theory]
    [InlineData(false, null, ModuleSourceKind.Github)]
    [InlineData(true, null, ModuleSourceKind.None)]   // dev без --module-source в сеть не ходит
    [InlineData(true, @"C:\mod", ModuleSourceKind.Folder)]
    [InlineData(false, @"C:\mod", ModuleSourceKind.Folder)]
    public void ChooseSource(bool dev, string? folder, ModuleSourceKind kind)
        => Assert.Equal(kind, ModuleInstaller.ChooseSource(dev, folder));

    [Fact]
    public void PinnedModule_PointsToItsOwnReleaseTag()
    {
        Assert.Equal("https://github.com/DontIgnore/Screeny/releases/download/ocr-module-1/Screeny-OcrModule-1.zip",
            OcrModule.DownloadUrl(1).ToString());
        Assert.Matches("^[0-9a-f]{64}$", OcrModule.ZipSha256);
        Assert.True(OcrModule.ZipSize > 0);
        Assert.EndsWith(Path.Combine("Screeny", "Modules"), OcrModule.ModulesRoot(dev: false));
        Assert.EndsWith(Path.Combine("Screeny", "Dev", "Modules"), OcrModule.ModulesRoot(dev: true));
        Assert.Matches("^[0-9a-f]{64}$", OcrModule.ManifestSha256);
        Assert.Equal(OcrModule.ManifestSha256, OcrModule.Spec.ManifestSha256);
    }

    [Fact]
    public async Task Install_StalledBody_TimesOutAsNoConnection()
    {
        var zip = ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles);
        var installer = new ModuleInstaller(Root, ModuleTestFiles.SpecFor(zip) with { ReadTimeout = TimeSpan.FromMilliseconds(200) });
        var http = new HttpClient(new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream(zip[..100], zip.Length)) { Headers = { ContentLength = zip.Length } },
        }));
        var ex = await Assert.ThrowsAsync<ModuleInstallException>(() => installer.InstallAsync(new HttpModuleSource(http, Url), null, CancellationToken.None));
        Assert.Equal(ModuleInstaller.NoConnection, ex.Message);
        Assert.False(Directory.Exists(installer.TargetFolder));
        AssertNoTemporaries();
    }

    /// <summary>Сервер не отвечает даже заголовками.</summary>
    private sealed class NeverRespondingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }
    }

    [Fact]
    public async Task Install_NoResponse_TimesOutAsNoConnection()
    {
        var installer = new ModuleInstaller(Root, new ModuleSpec("ocr-paddle", 1, new string('a', 64)) { ReadTimeout = TimeSpan.FromMilliseconds(200) });
        var source = new HttpModuleSource(new HttpClient(new NeverRespondingHandler()) { Timeout = Timeout.InfiniteTimeSpan }, Url);
        var ex = await Assert.ThrowsAsync<ModuleInstallException>(() => installer.InstallAsync(source, null, CancellationToken.None));
        Assert.Equal(ModuleInstaller.NoConnection, ex.Message);
        AssertNoTemporaries();
    }

    [Fact]
    public async Task Install_SlowButSteadyBody_IsNotTimedOut()
    {
        // Таймаут — на паузу между кусками, а не на всю загрузку.
        var zip = ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles);
        var installer = new ModuleInstaller(Root, ModuleTestFiles.SpecFor(zip) with { ReadTimeout = TimeSpan.FromMilliseconds(300) });
        var http = new HttpClient(new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new TricklingStream(zip, chunk: 64, delayMs: 50)) { Headers = { ContentLength = zip.Length } },
        }));
        await installer.InstallAsync(new HttpModuleSource(http, Url), null, CancellationToken.None);
        Assert.True(Directory.Exists(installer.TargetFolder));
    }

    /// <summary>Отдаёт данные маленькими кусками с паузой.</summary>
    private sealed class TricklingStream(byte[] data, int chunk, int delayMs) : Stream
    {
        private int _pos;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos >= data.Length) return 0;
            await Task.Delay(delayMs, ct);
            int n = Math.Min(Math.Min(chunk, buffer.Length), data.Length - _pos);
            data.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Install_ManifestNotMatchingPinnedHash_IsRejected()
    {
        // Архив цел и согласован сам с собой, но module.json — не тот, что зашит в приложении.
        var zip = ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles);
        var installer = new ModuleInstaller(Root, ModuleTestFiles.SpecFor(zip) with { ManifestSha256 = new string('b', 64) });
        var ex = await Assert.ThrowsAsync<ModuleInstallException>(() => installer.InstallAsync(Source(zip), null, CancellationToken.None));
        Assert.Contains("module.json изменён", ex.Message);
        Assert.False(Directory.Exists(installer.TargetFolder));
    }

    [Fact]
    public async Task Install_ManifestMatchingPinnedHash_Installs()
    {
        var zip = ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles);
        var manifest = ModuleTestFiles.Sha(ModuleTestFiles.Manifest("ocr-paddle", 1, ModuleTestFiles.DefaultFiles));
        var installer = new ModuleInstaller(Root, ModuleTestFiles.SpecFor(zip) with { ManifestSha256 = manifest });
        await installer.InstallAsync(Source(zip), null, CancellationToken.None);
        Assert.Null(ModuleManifest.Verify(installer.TargetFolder, "ocr-paddle", 1, manifest));
    }

    [Fact]
    public void MoveWithRetry_WaitsOutBriefLock()
    {
        var from = Path.Combine(_files.Dir, "from");
        Directory.CreateDirectory(from);
        var locked = new FileStream(Path.Combine(from, "x.dll"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var release = new Timer(_ => locked.Dispose(), null, 300, Timeout.Infinite); // «антивирус» отпускает файл
        var to = Path.Combine(_files.Dir, "to");
        ModuleInstaller.MoveWithRetry(from, to, attempts: 5, delayMs: 200);
        Assert.True(File.Exists(Path.Combine(to, "x.dll")));
    }

    [Fact]
    public void MoveWithRetry_PermanentLock_GivesFriendlyError()
    {
        var from = Path.Combine(_files.Dir, "from");
        Directory.CreateDirectory(from);
        using var locked = new FileStream(Path.Combine(from, "x.dll"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var ex = Assert.Throws<ModuleInstallException>(() => ModuleInstaller.MoveWithRetry(from, Path.Combine(_files.Dir, "to"), attempts: 2, delayMs: 10));
        Assert.Contains("антивирус", ex.Message);
    }

    [Fact]
    public void DeleteRootOrMark_RemovesEverything()
    {
        _files.Installed(Root, 1);
        ModuleInstaller.DeleteRootOrMark(Root);
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public void DeleteRootOrMark_LockedModule_IsMarkedAndNoLongerInstalled()
    {
        // Модуль держит работающая портативная копия: папка общая.
        var dir = _files.Installed(Root, 1);
        Directory.CreateDirectory(Path.Combine(Root, ".staging-x"));
        using (new FileStream(Path.Combine(dir, "onnxruntime.dll"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            ModuleInstaller.DeleteRootOrMark(Root);
            Assert.True(File.Exists(Path.Combine(dir, ModuleInstaller.RemovedMarker)));
            Assert.Null(ModuleInstaller.DetectInstalled(Root, "ocr-paddle", 1));
            Assert.False(Directory.Exists(Path.Combine(Root, ".staging-x")));
        }
        new ModuleInstaller(Root, new ModuleSpec("ocr-paddle", 1, "")).CleanupOnStartup(); // следующий запуск копии
        Assert.False(Directory.Exists(dir));
    }
}
