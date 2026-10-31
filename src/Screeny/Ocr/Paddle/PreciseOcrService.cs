using Screeny.Logging;
using Screeny.Modules;

namespace Screeny.Ocr.Paddle;

/// <summary>Text refinement for text mode (overlay doesn't know where it comes from).</summary>
public interface IOcrRefiner
{
    /// <summary>Precise recognition active and module not broken: refinement should run.</summary>
    bool IsActive { get; }

    /// <summary>Verify module files and load sessions if not yet. false — module failed to load (reason in log).</summary>
    Task<bool> PrepareAsync(CancellationToken ct);

    /// <summary>Layout in pixels of <paramref name="image"/> or null (inactive, load error — already in log).</summary>
    Task<OcrLayout?> RecognizeAsync(OcrImage image, CancellationToken ct);

    /// <summary>User entered text mode (Ctrl+T): reason to remind about outdated module.</summary>
    void TextModeUsed();
}

/// <summary>
/// Precise recognition for whole app. ORT sessions created at first refinement and released via
/// <see cref="IdleTimeout"/> of inactivity: on full screen ORT arena holds up to ~1 GB, background tray app doesn't need it.
/// Runs are single (semaphore), release by timer takes same semaphore and doesn't collide with running run.
/// Each time sessions are recreated (first and after release), module files verified against <c>module.json</c>,
/// and it against SHA256 hardcoded in app. Load error (corrupted files, missing/outdated VC++ runtime,
/// antivirus) disables refinement until restart or reinstall — text mode uses Windows OCR.
/// </summary>
public sealed class PreciseOcrService : IOcrRefiner, IDisposable
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);

    private readonly Func<string?> _activeFolder;
    private readonly Action _textModeUsed;
    private readonly ModuleSpec _spec;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idle;
    private readonly (int Par, int Threads) _split = PaddlePipeline.ThreadSplit(Environment.ProcessorCount);
    private PaddleEngine? _engine;
    private string? _engineFolder;
    private volatile string? _loadError;
    private bool _disposed;

    /// <param name="activeFolder">Module folder if precise recognition active (<see cref="PreciseOcrRules.IsActive"/>), else null.</param>
    /// <param name="textModeUsed">Called on each entry to text mode.</param>
    /// <param name="spec">What to verify in folder; default — module of this build (<see cref="OcrModule.Spec"/>).</param>
    public PreciseOcrService(Func<string?> activeFolder, Action textModeUsed, ModuleSpec? spec = null)
    {
        _activeFolder = activeFolder;
        _textModeUsed = textModeUsed;
        _spec = spec ?? OcrModule.Spec;
        _idle = new Timer(_ => ReleaseIdle(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Module failed to load in this process: text for settings. null — no errors.</summary>
    public string? LoadError => _loadError;

    public bool IsActive => _loadError is null && !_disposed && _activeFolder() is not null;

    public void TextModeUsed() => _textModeUsed();

    /// <summary>Module reinstalled or deleted: previous error no longer applies.</summary>
    public void Reset()
    {
        _loadError = null;
        ReleaseIdle();
    }

    public async Task<bool> PrepareAsync(CancellationToken ct)
        => await UseEngineAsync(engine => true, ct);

    public Task<OcrLayout?> RecognizeAsync(OcrImage image, CancellationToken ct)
        => UseEngineAsync<OcrLayout?>(engine =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var layout = engine.Run(image, ct);
            Log.Error($"Precise OCR {image.W}x{image.H}: {sw.ElapsedMilliseconds} ms, {layout.WordCount} words ({engine.Stats}, par={_split.Par} threads={_split.Threads})");
            return layout;
        }, ct);

    /// <summary>Under semaphore, in background: sessions present → <paramref name="work"/>. Module inactive or failed — default.</summary>
    private async Task<T?> UseEngineAsync<T>(Func<PaddleEngine, T> work, CancellationToken ct)
    {
        if (_activeFolder() is not { } folder || _loadError is not null) return default;
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed) return default;
            _idle.Change(Timeout.Infinite, Timeout.Infinite);
            return await Task.Run(() => EnsureEngine(folder) is { } engine ? work(engine) : default, ct);
        }
        finally
        {
            if (!_disposed) _idle.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    /// <summary>Sessions present (under semaphore). null — module failed, reason in <see cref="LoadError"/>.</summary>
    private PaddleEngine? EnsureEngine(string folder)
    {
        if (_engine is not null && _engineFolder == folder) return _engine;
        _engine?.Dispose();
        _engine = null;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Every time before creating sessions, not once per process: after release files could be swapped.
            if (ModuleManifest.Verify(folder, _spec.Id, _spec.Version, _spec.ManifestSha256) is { } error)
            {
                Fail(Localization.Loc.F("PreciseOcr.FilesDamaged", error), null);
                return null;
            }
            long verified = sw.ElapsedMilliseconds;
            _engine = PaddleEngine.Load(folder, _split.Par, _split.Threads);
            _engineFolder = folder;
            Log.Error($"Precise OCR sessions loaded from {folder}: files verified in {verified} ms, loaded in {sw.ElapsedMilliseconds - verified} ms");
            return _engine;
        }
        catch (OrtLoadException ex) { Fail(ex.Message, ex); return null; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Most often DllNotFoundException: no runtime or file locked by antivirus. .NET exception text
            // not shown; it's in log.
            Fail(ex is DllNotFoundException or BadImageFormatException
                ? Localization.Loc.T("PreciseOcr.OnnxLoadFailed")
                : Localization.Loc.T("PreciseOcr.ModuleLoadFailed"), ex);
            return null;
        }
    }

    private void Fail(string message, Exception? ex)
    {
        _loadError = message;
        Log.Error("Precise OCR disabled for this session: " + message, ex);
    }

    private void ReleaseIdle()
    {
        if (!_gate.Wait(0)) return; // run ongoing: after it timer restarts
        try
        {
            if (_engine is null) return;
            _engine.Dispose();
            _engine = null;
            _engineFolder = null;
            Log.Error("Precise OCR sessions released (idle)");
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        _idle.Dispose();
        ReleaseIdle();
    }
}
