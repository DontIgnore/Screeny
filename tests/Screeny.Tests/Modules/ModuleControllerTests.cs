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

    private static Task<FinishedArgs> NextFinished(ModuleController c)
    {
        var tcs = new TaskCompletionSource<FinishedArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Finished += (ok, error, tray) => tcs.TrySetResult(new(ok, error, tray));
        return tcs.Task;
    }

    private ModuleController Controller(ModuleSpec spec, IModuleSource? source, Func<string, bool>? inUse = null)
        => new(new ModuleInstaller(Root, spec), _ => source, inUse ?? (_ => false));

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
        var text = ModuleController.FriendlyError(ex);
        Assert.Contains(expected, text);
        Assert.DoesNotContain("raw .NET message", text);
    }

    [Fact]
    public void FriendlyError_KeepsOwnMessages()
        => Assert.Equal("Модуль не найден на сервере (404).", ModuleController.FriendlyError(new ModuleInstallException("Модуль не найден на сервере (404).")));

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

    private static readonly Dictionary<string, byte[]> RuntimeFiles = new() { ["onnxruntime.dll"] = "native"u8.ToArray() };

    /// <summary>Архивы общего компонента и двух модулей, которым он нужен, в подпапках <c>rel</c> (как в <c>releases\</c>).</summary>
    private (ModuleSpec Runtime, ModuleSpec A, ModuleSpec B, string Source) TwoModulesWithRuntime()
    {
        var src = Path.Combine(_files.Dir, "rel");
        byte[] Put(string sub, string name, byte[] zip)
        {
            Directory.CreateDirectory(Path.Combine(src, sub));
            File.WriteAllBytes(Path.Combine(src, sub, name), zip);
            return zip;
        }
        var rt = Put("ort-runtime", "rt.zip", ModuleTestFiles.Zip(RuntimeFiles, id: "ort-runtime", version: 1));
        var a = Put("a", "a.zip", ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles, id: "mod-a", version: 1));
        var b = Put("b", "b.zip", ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles, id: "mod-b", version: 1));
        var runtime = new ModuleSpec("ort-runtime", 1, ModuleTestFiles.Sha(rt)) { ZipName = "rt.zip", ZipSize = rt.Length };
        return (runtime,
            new ModuleSpec("mod-a", 1, ModuleTestFiles.Sha(a)) { ZipName = "a.zip", ZipSize = a.Length, Requires = [runtime] },
            new ModuleSpec("mod-b", 1, ModuleTestFiles.Sha(b)) { ZipName = "b.zip", ZipSize = b.Length, Requires = [runtime] },
            src);
    }

    private ModuleController Controller(ModuleSpec spec, string source, ModuleSpec other, Func<string, bool>? inUse = null)
        => new(new ModuleInstaller(Root, spec), s => new FolderModuleSource(source, s.ZipName), inUse ?? (_ => false), [other]);

    [Fact]
    public async Task Download_InstallsMissingRuntimeFirst_ThenModule()
    {
        var (runtime, a, b, src) = TwoModulesWithRuntime();
        var c = Controller(a, src, b);
        Assert.False(c.Status.Ready);
        Assert.Equal(a.ZipSize + runtime.ZipSize, c.Status.DownloadBytes);
        var finished = NextFinished(c);
        c.StartDownload();
        Assert.True((await finished.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.True(c.Status.Ready);
        Assert.Equal(0, c.Status.DownloadBytes);
        Assert.True(Directory.Exists(Path.Combine(Root, "ort-runtime-1")));
        Assert.True(Directory.Exists(Path.Combine(Root, "mod-a-1")));
    }

    [Fact]
    public void RuntimeMissing_ModuleIsNotReady()
    {
        var (_, a, b, src) = TwoModulesWithRuntime();
        _files.Installed(Root, 1, id: "mod-a");
        var c = Controller(a, src, b);
        Assert.Equal(1, c.Status.Installed);
        Assert.False(c.Status.Ready);
        Assert.Null(c.ReadyFolder);
    }

    [Fact]
    public async Task SecondModule_ReusesInstalledRuntime()
    {
        var (runtime, a, b, src) = TwoModulesWithRuntime();
        var ca = Controller(a, src, b);
        var done = NextFinished(ca);
        ca.StartDownload();
        Assert.True((await done.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        // Архива компонента больше нет: если бы его качали снова, установка упала бы.
        File.Delete(Path.Combine(src, "ort-runtime", "rt.zip"));
        var cb = Controller(b, src, a);
        Assert.Equal(b.ZipSize, cb.Status.DownloadBytes);
        done = NextFinished(cb);
        cb.StartDownload();
        Assert.True((await done.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.True(cb.Status.Ready);
    }

    [Fact]
    public void Remove_KeepsRuntimeWhileAnotherModuleNeedsIt()
    {
        var (_, a, b, src) = TwoModulesWithRuntime();
        _files.Installed(Root, 1, RuntimeFiles, "ort-runtime");
        _files.Installed(Root, 1, id: "mod-a");
        _files.Installed(Root, 1, id: "mod-b");
        var ca = Controller(a, src, b);
        Assert.True(ca.Status.Ready);
        ca.Remove();
        Assert.False(Directory.Exists(Path.Combine(Root, "mod-a-1")));
        Assert.True(Directory.Exists(Path.Combine(Root, "ort-runtime-1")));

        var cb = Controller(b, src, a);
        cb.Remove();
        Assert.False(Directory.Exists(Path.Combine(Root, "ort-runtime-1")));
    }

    [Fact]
    public void Remove_RuntimeLoadedInProcess_IsMarkedAndUndoRestoresIt()
    {
        var (_, a, b, src) = TwoModulesWithRuntime();
        var runtimeDir = _files.Installed(Root, 1, RuntimeFiles, "ort-runtime");
        _files.Installed(Root, 1, id: "mod-a");
        var c = Controller(a, src, b, inUse: d => d == runtimeDir);
        c.Remove();
        Assert.True(File.Exists(Path.Combine(runtimeDir, ModuleInstaller.RemovedMarker)));
        Assert.False(c.Status.Ready);
        // Модуль удалился сразу (его файлы не держат), «Не удалять» его не вернёт; компонент же снова на месте.
        c.UndoRemove();
        Assert.False(File.Exists(Path.Combine(runtimeDir, ModuleInstaller.RemovedMarker)));
    }
    private static HttpModuleSource Hanging() => new(new HttpClient(new SilentHandler()), new Uri("https://example.invalid/m.zip"));

    [Fact]
    public async Task Remove_KeepsRuntimeWhileAnotherModuleDownloads_CancelThenRemovesIt()
    {
        var (_, a, b, src) = TwoModulesWithRuntime();
        var runtimeDir = _files.Installed(Root, 1, RuntimeFiles, "ort-runtime");
        _files.Installed(Root, 1, id: "mod-a");
        var ca = Controller(a, src, b);
        var cb = new ModuleController(new ModuleInstaller(Root, b), _ => Hanging(), _ => false, [a]);
        var finished = NextFinished(cb);
        cb.StartDownload(notifyInTray: true); // отмена без уведомления Finished не поднимает
        ca.Remove();
        Assert.False(Directory.Exists(Path.Combine(Root, "mod-a-1")));
        Assert.True(Directory.Exists(runtimeDir)); // нужен скачиваемому модулю
        // Загрузка отменена: компонент больше никому не нужен.
        cb.CancelDownload();
        Assert.False((await finished.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.False(Directory.Exists(runtimeDir));
    }

    [Fact]
    public async Task FirstInstall_Cancelled_LeavesNoOrphanedRuntime()
    {
        var (_, a, b, src) = TwoModulesWithRuntime();
        var c = new ModuleController(new ModuleInstaller(Root, a),
            s => s.Id == "ort-runtime" ? new FolderModuleSource(Path.Combine(src, "ort-runtime"), s.ZipName) : Hanging(), _ => false, [b]);
        var finished = NextFinished(c);
        c.StartDownload(notifyInTray: true);
        var runtimeDir = Path.Combine(Root, "ort-runtime-1");
        for (int i = 0; i < 200 && !Directory.Exists(runtimeDir); i++) await Task.Delay(25);
        Assert.True(Directory.Exists(runtimeDir));
        c.CancelDownload();
        Assert.False((await finished.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.False(Directory.Exists(runtimeDir));
        Assert.Equal(a.ZipSize + a.Requires[0].ZipSize, c.Status.DownloadBytes);
    }

    [Fact]
    public async Task StartupCleanup_RemovesRuntimeNoModuleNeeds()
    {
        var (_, a, b, src) = TwoModulesWithRuntime();
        var runtimeDir = _files.Installed(Root, 1, RuntimeFiles, "ort-runtime");
        await Controller(a, src, b).CleanupAsync();
        Assert.False(Directory.Exists(runtimeDir));

        _files.Installed(Root, 1, RuntimeFiles, "ort-runtime");
        _files.Installed(Root, 1, id: "mod-b");
        await Controller(a, src, b).CleanupAsync();
        Assert.True(Directory.Exists(runtimeDir));
    }

    [Fact]
    public void RuntimeRemovedElsewhere_StatusIsLive()
    {
        var (_, a, b, src) = TwoModulesWithRuntime();
        var runtimeDir = _files.Installed(Root, 1, RuntimeFiles, "ort-runtime");
        _files.Installed(Root, 1, id: "mod-a");
        var c = Controller(a, src, b);
        Assert.True(c.Status.Ready);
        Directory.Delete(runtimeDir, recursive: true);
        Assert.False(c.Status.Ready);
        Assert.Null(c.ReadyFolder);
    }

    [Fact]
    public async Task OcrV1WithOwnRuntime_IsOutdated_UpdateInstallsRuntimeAndV2_AndRemovesV1()
    {
        var src = Path.Combine(_files.Dir, "rel");
        Directory.CreateDirectory(Path.Combine(src, "ort-runtime"));
        Directory.CreateDirectory(Path.Combine(src, "ocr-module"));
        var rt = ModuleTestFiles.Zip(RuntimeFiles, id: "ort-runtime", version: 1);
        var v2 = ModuleTestFiles.Zip(ModuleTestFiles.DefaultFiles, id: "ocr-paddle", version: 2);
        File.WriteAllBytes(Path.Combine(src, "ort-runtime", "rt.zip"), rt);
        File.WriteAllBytes(Path.Combine(src, "ocr-module", "ocr2.zip"), v2);
        var runtime = new ModuleSpec("ort-runtime", 1, ModuleTestFiles.Sha(rt)) { ZipName = "rt.zip", ZipSize = rt.Length };
        var spec = new ModuleSpec("ocr-paddle", 2, ModuleTestFiles.Sha(v2)) { ZipName = "ocr2.zip", ZipSize = v2.Length, Requires = [runtime] };
        // Версия 1 везла свой onnxruntime.dll.
        var v1Files = new Dictionary<string, byte[]>(ModuleTestFiles.DefaultFiles) { ["onnxruntime.dll"] = "old native"u8.ToArray() };
        var v1 = _files.Installed(Root, 1, v1Files);

        var c = new ModuleController(new ModuleInstaller(Root, spec), s => new FolderModuleSource(src, s.ZipName), _ => false);
        Assert.True(c.Status.Outdated);
        Assert.False(c.Status.Ready);
        Assert.Equal(rt.Length + v2.Length, c.Status.DownloadBytes);
        var finished = NextFinished(c);
        c.StartDownload();
        Assert.True((await finished.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.True(c.Status.Ready);
        Assert.Equal(Path.Combine(Root, "ocr-paddle-2"), c.ReadyFolder);
        Assert.True(Directory.Exists(Path.Combine(Root, "ort-runtime-1")));
        Assert.False(Directory.Exists(v1));
    }
}
