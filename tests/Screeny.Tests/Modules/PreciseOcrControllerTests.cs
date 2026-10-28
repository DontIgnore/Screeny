using System.Net.Http;
using Screeny.Modules;
using Screeny.Ocr.Paddle;

namespace Screeny.Tests.Modules;

/// <summary>Загрузка, удаление и «Не удалять» глазами настроек и мастера; служба точного распознавания без моделей.</summary>
public class PreciseOcrControllerTests : IDisposable
{
    private readonly ModuleTestFiles _files = new();
    private string Root => Path.Combine(_files.Dir, "Modules");
    public void Dispose() => _files.Dispose();

    private sealed record FinishedArgs(bool Ok, string? Error, bool Tray);

    private static Task<FinishedArgs> NextFinished(PreciseOcrController c)
    {
        var tcs = new TaskCompletionSource<FinishedArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Finished += (ok, error, tray) => tcs.TrySetResult(new(ok, error, tray));
        return tcs.Task;
    }

    private PreciseOcrController Controller(ModuleSpec spec, IModuleSource? source, Func<string, bool>? inUse = null)
        => new(new ModuleInstaller(Root, spec), () => source, inUse ?? (_ => false));

    [Fact]
    public async Task Download_FromFolder_InstallsAndReportsSuccess()
    {
        var zip = ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles);
        var src = Path.Combine(_files.Dir, "rel");
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "m.zip"), zip);
        var c = Controller(ModuleTestFiles.SpecFor(zip), new FolderModuleSource(src, "m.zip"));
        Assert.False(c.Status.Ready);
        var finished = NextFinished(c);
        c.StartDownload(notifyInTray: true);
        var result = await finished.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new FinishedArgs(true, null, true), result);
        Assert.True(c.Status.Ready);
        Assert.False(c.Status.Downloading);
        Assert.Equal(Path.Combine(Root, "ocr-paddle-1"), c.ReadyFolder);
    }

    /// <summary>Отдаёт заголовки и затем молчит.</summary>
    private sealed class SilentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StreamContent(new HangingStream()) { Headers = { ContentLength = 1000 } },
            });
    }

    private sealed class HangingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 1000;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Download_FromWizard_StalledServer_EndsWithTrayResult()
    {
        var spec = new ModuleSpec("ocr-paddle", 1, new string('a', 64)) { ReadTimeout = TimeSpan.FromMilliseconds(200) };
        var c = Controller(spec, new HttpModuleSource(new HttpClient(new SilentHandler()), new Uri("https://example.invalid/m.zip")));
        var finished = NextFinished(c);
        c.StartDownload(notifyInTray: true);
        var result = await finished.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new FinishedArgs(false, ModuleInstaller.NoConnection, true), result);
        Assert.Equal(ModuleInstaller.NoConnection, c.Status.Error);
    }

    [Fact]
    public async Task Download_FromWizard_Cancelled_StillEndsWithTrayResult()
    {
        var c = Controller(new ModuleSpec("ocr-paddle", 1, new string('a', 64)),
            new HttpModuleSource(new HttpClient(new SilentHandler()), new Uri("https://example.invalid/m.zip")));
        var finished = NextFinished(c);
        c.StartDownload(notifyInTray: true);
        Assert.True(c.Status.Downloading);
        c.CancelDownload();
        var result = await finished.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result.Ok);
        Assert.True(result.Tray);
        Assert.False(c.Status.Downloading);
    }

    [Fact]
    public async Task Download_WithoutSource_FailsWithDevHint()
    {
        var c = Controller(new ModuleSpec("ocr-paddle", 1, ""), source: null);
        Assert.False(c.Status.SourceAvailable);
        var finished = NextFinished(c);
        c.StartDownload(notifyInTray: true);
        var result = await finished.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Ok);
        Assert.Contains("--module-source", result.Error);
    }

    [Fact]
    public void RemoveInUse_ThenUndo_RaisesRemovedThenRestored()
    {
        var dir = _files.Installed(Root, 1);
        var c = Controller(new ModuleSpec("ocr-paddle", 1, ""), null, inUse: d => d == dir);
        int removed = 0, restored = 0;
        c.Removed += () => removed++;
        c.Restored += () => restored++;

        c.Remove();
        Assert.Equal(1, removed);
        Assert.True(c.Status.RemovalPending);
        Assert.False(c.Status.Ready);
        Assert.Null(c.ReadyFolder);

        c.UndoRemove();
        Assert.Equal(1, restored);
        Assert.True(c.Status.Ready);
        Assert.Null(c.Status.Error);
    }

    [Fact]
    public void Undo_WhenFilesAreGone_DoesNotRestore()
    {
        var dir = _files.Installed(Root, 1);
        var c = Controller(new ModuleSpec("ocr-paddle", 1, ""), null, inUse: _ => true);
        int restored = 0;
        c.Restored += () => restored++;
        c.Remove();
        File.Delete(Path.Combine(dir, "det.onnx"));
        c.UndoRemove();
        Assert.Equal(0, restored);
        Assert.NotNull(c.Status.Error);
        Assert.True(c.Status.RemovalPending);
    }

    [Theory]
    // Тексты — по-русски: язык строк в тестах закреплён (Localization/TestCulture.cs).
    [InlineData(typeof(HttpRequestException), "Нет связи с сервером")]
    [InlineData(typeof(TimeoutException), "Нет связи с сервером")]
    [InlineData(typeof(InvalidDataException), "архив модуля повреждён")]
    [InlineData(typeof(UnauthorizedAccessException), "Нет доступа")]
    [InlineData(typeof(IOException), "Не удалось записать")]
    [InlineData(typeof(InvalidOperationException), "Подробности — в журнале")]
    public void FriendlyError_NeverShowsRawExceptionText(Type type, string expected)
    {
        var ex = (Exception)Activator.CreateInstance(type, "raw .NET message")!;
        var text = PreciseOcrController.FriendlyError(ex);
        Assert.Contains(expected, text);
        Assert.DoesNotContain("raw .NET message", text);
    }

    [Fact]
    public void FriendlyError_KeepsOwnMessages()
        => Assert.Equal("Модуль не найден на сервере (404).", PreciseOcrController.FriendlyError(new ModuleInstallException("Модуль не найден на сервере (404).")));

    [Fact]
    public async Task Service_TamperedFiles_DoNotLoadAndReportReason()
    {
        var dir = _files.Installed(Root, 1);
        var manifestSha = ModuleManifest.Sha256Of(Path.Combine(dir, ModuleManifest.FileName));
        var spec = new ModuleSpec("ocr-paddle", 1, "", manifestSha);
        // Подмена и файла, и манифеста: без зашитого хеша манифеста проверка прошла бы.
        var files = new Dictionary<string, byte[]>(ModuleTestFiles.DefaultFiles) { ["det.onnx"] = [1, 2, 3] };
        _files.Installed(Root, 1, files);
        using var service = new PreciseOcrService(() => dir, () => { }, spec);
        Assert.True(service.IsActive);
        Assert.False(await service.PrepareAsync(CancellationToken.None));
        Assert.Contains("изменены", service.LoadError);
        Assert.False(service.IsActive);
        Assert.Null(await service.RecognizeAsync(new OcrImage(new byte[4], 1, 1), CancellationToken.None));
    }

    [Fact]
    public async Task Service_Inactive_DoesNothing()
    {
        using var service = new PreciseOcrService(() => null, () => { });
        Assert.False(service.IsActive);
        Assert.False(await service.PrepareAsync(CancellationToken.None));
        Assert.Null(service.LoadError);
    }
}
