using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Screeny.Logging;

namespace Screeny.Ocr.Paddle;

/// <summary>Native module runtime cannot be loaded; text is for user (settings).</summary>
public sealed class OrtLoadException(string message) : Exception(message);

/// <summary>
/// Native ONNX Runtime from module folder. App has only managed assembly
/// (<c>Microsoft.ML.OnnxRuntime.Managed</c>), and <c>onnxruntime.dll</c> lives in downloaded module: before first ORT
/// type access its DllImport is redirected there. Loaded library stays in process until exit, so
/// deleting or replacing module files after first recognition requires restart.
/// </summary>
public static class OrtNative
{
    public const string LibraryName = "onnxruntime";

    /// <summary>
    /// Visual C++ runtime required by <c>onnxruntime.dll</c> (MSVCP140, MSVCP140_1, VCRUNTIME140, VCRUNTIME140_1).
    /// On machines without VC++ redistributable these are missing, so module carries its copies; loaded by full path before ORT,
    /// and ORT binds to already loaded ones.
    /// </summary>
    public static readonly string[] CrtLibraries = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll"];

    private static readonly object Gate = new();
    private static IntPtr _handle;
    private static string? _loadedFrom;

    /// <summary>Folder from which native ORT already loaded, or null.</summary>
    public static string? LoadedFrom { get { lock (Gate) return _loadedFrom; } }

    /// <summary>
    /// Loads runtime from <paramref name="folder"/> (once per process). Throws <see cref="OrtLoadException"/>, if in
    /// process there's already older VC++ runtime, and loader exceptions if library fails to load.
    /// </summary>
    public static void EnsureLoaded(string folder)
    {
        lock (Gate)
        {
            if (_handle != IntPtr.Zero) return;
            CheckLoadedCrt(folder);
            foreach (var crt in CrtLibraries)
            {
                var path = Path.Combine(folder, crt);
                if (File.Exists(path)) NativeLibrary.Load(path);
            }
            var handle = NativeLibrary.Load(Path.Combine(folder, LibraryName + ".dll"));
            try { NativeLibrary.SetDllImportResolver(typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly, Resolve); }
            catch (InvalidOperationException ex)
            {
                // Resolver already set (ORT assembly touched earlier): library loaded by full path, search by
                // name “onnxruntime.dll” will find already loaded module.
                Log.Error("ORT DllImport resolver was already set; relying on the preloaded library", ex);
            }
            _handle = handle;
            _loadedFrom = folder;
        }
    }

    /// <summary>
    /// Windows loader binds import by name to already loaded module wherever it is. If some shell extension
    /// already pulled old <c>msvcp140.dll</c> into process from elsewhere, ORT (built with VC++ 14.4x) binds to it
    /// and may crash with access violation that can't be caught. So such case is refusal with clear reason.
    /// </summary>
    private static void CheckLoadedCrt(string folder)
    {
        foreach (var crt in CrtLibraries)
        {
            var ours = Path.Combine(folder, crt);
            if (!File.Exists(ours)) continue;
            var loaded = LoadedModulePath(crt);
            if (loaded is null) continue;
            if (CrtConflict(crt, loaded, FileVersionOf(loaded), ours, FileVersionOf(ours)) is { } problem)
                throw new OrtLoadException(problem);
            Log.Error($"VC++ runtime {crt} is already loaded from {loaded}; ONNX Runtime will use it");
        }
    }

    /// <summary>
    /// Refusal text if process already has <paramref name="name"/> loaded from elsewhere and older than module's;
    /// else null (same copy, same or newer — ORT works with it).
    /// </summary>
    public static string? CrtConflict(string name, string loadedPath, Version? loadedVersion, string ourPath, Version? ourVersion)
    {
        if (string.Equals(Path.GetFullPath(loadedPath), Path.GetFullPath(ourPath), StringComparison.OrdinalIgnoreCase)) return null;
        if (loadedVersion is null || ourVersion is null || loadedVersion >= ourVersion) return null;
        return Localization.Loc.F("PreciseOcr.OldVcRuntime", name, loadedVersion, Path.GetDirectoryName(loadedPath), ourVersion);
    }

    private static Version? FileVersionOf(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException) { return null; }
    }

    private static string? LoadedModulePath(string name)
    {
        var module = GetModuleHandleW(name);
        if (module == IntPtr.Zero) return null;
        var sb = new StringBuilder(1024);
        int n = GetModuleFileNameW(module, sb, sb.Capacity);
        return n > 0 ? sb.ToString(0, n) : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetModuleFileNameW(IntPtr module, StringBuilder name, int size);

    private static IntPtr Resolve(string name, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        => name is LibraryName or LibraryName + ".dll" ? _handle : IntPtr.Zero;
}
