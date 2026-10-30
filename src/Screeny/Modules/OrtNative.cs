using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Screeny.Logging;

namespace Screeny.Modules;

/// <summary>Нативный рантайм модуля загружать нельзя; текст — для пользователя (настройки).</summary>
public sealed class OrtLoadException(string message) : Exception(message);

/// <summary>
/// Нативный ONNX Runtime из папки общего компонента (<see cref="OrtRuntimeModule"/>). В приложении только управляемая
/// сборка (<c>Microsoft.ML.OnnxRuntime.Managed</c>), а <c>onnxruntime.dll</c> лежит в скачанном компоненте: перед первым
/// обращением к типам ORT её DllImport перенаправляется туда. Одна копия на процесс для всех модулей (точное
/// распознавание, смысловой поиск). Загруженная библиотека остаётся в процессе до выхода, поэтому удалить или заменить
/// файлы компонента после первого использования можно только после перезапуска.
/// </summary>
public static class OrtNative
{
    public const string LibraryName = "onnxruntime";

    /// <summary>
    /// Рантайм Visual C++, от которого зависит <c>onnxruntime.dll</c> (MSVCP140, MSVCP140_1, VCRUNTIME140, VCRUNTIME140_1).
    /// На машине без распространяемого пакета VC++ их нет, поэтому модуль везёт свои копии; грузятся по полному пути до ORT,
    /// и ORT связывается с уже загруженными.
    /// </summary>
    public static readonly string[] CrtLibraries = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll"];

    private static readonly object Gate = new();
    private static IntPtr _handle;
    private static string? _loadedFrom;

    /// <summary>Папка, из которой уже загружен нативный ORT, или null.</summary>
    public static string? LoadedFrom { get { lock (Gate) return _loadedFrom; } }

    /// <summary>
    /// Грузит рантайм из <paramref name="folder"/> (один раз за процесс). Бросает <see cref="OrtLoadException"/>, если в
    /// процессе уже есть более старый рантайм VC++ или ORT уже загружен из другой папки (вторая копия в процессе не
    /// нужна: DllImport всё равно смотрит на первую), и исключения загрузчика, если библиотека не загружается.
    /// </summary>
    public static void EnsureLoaded(string folder)
    {
        lock (Gate)
        {
            if (_handle != IntPtr.Zero)
            {
                if (string.Equals(Path.GetFullPath(folder), Path.GetFullPath(_loadedFrom!), StringComparison.OrdinalIgnoreCase)) return;
                Log.Error($"ONNX Runtime is already loaded from {_loadedFrom}; refusing a second copy from {folder}");
                throw new OrtLoadException(Localization.Loc.T("Module.RuntimeRestart"));
            }
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
                // Резолвер уже стоит (сборку ORT кто-то тронул раньше): библиотека загружена по полному пути, и поиск по
                // имени «onnxruntime.dll» найдёт уже загруженный модуль.
                Log.Error("ORT DllImport resolver was already set; relying on the preloaded library", ex);
            }
            _handle = handle;
            _loadedFrom = folder;
        }
    }

    /// <summary>
    /// Загрузчик Windows связывает импорт по имени с уже загруженным модулем, где бы тот ни лежал. Если какое-то расширение
    /// оболочки уже подтянуло в процесс старый <c>msvcp140.dll</c> из другого места, ORT (собран VC++ 14.4x) свяжется с ним
    /// и может упасть с нарушением доступа, которое не перехватить. Поэтому такой случай — отказ с понятной причиной.
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
    /// Текст отказа, если в процессе уже загружена <paramref name="name"/> из другого места и более старой версии, чем в
    /// модуле; иначе null (та же копия, та же или более новая версия — с ней ORT работает).
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
