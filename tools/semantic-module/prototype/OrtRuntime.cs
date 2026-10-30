using System.Runtime.InteropServices;

namespace SemanticProto;

/// <summary>
/// Loads the native ONNX Runtime from a folder and points the managed package's DllImport at it, like the app's
/// <c>Ocr/Paddle/OrtNative.cs</c>. This is the "shared runtime component" from the README: one folder, one version
/// (1.31.0), loaded once per process; every module that needs ORT calls <see cref="EnsureLoaded"/> with the same folder.
/// </summary>
public static class OrtRuntime
{
    private static readonly object Gate = new();
    private static IntPtr _handle;
    public static string? LoadedFrom { get; private set; }

    public static void EnsureLoaded(string folder)
    {
        lock (Gate)
        {
            if (_handle != IntPtr.Zero)
            {
                if (!string.Equals(Path.GetFullPath(folder), LoadedFrom, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"ONNX Runtime is already loaded from {LoadedFrom}; refusing a second copy from {folder}");
                return;
            }
            foreach (var crt in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll" })
            {
                var p = Path.Combine(folder, crt);
                if (File.Exists(p)) NativeLibrary.Load(p);
            }
            _handle = NativeLibrary.Load(Path.Combine(folder, "onnxruntime.dll"));
            NativeLibrary.SetDllImportResolver(typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly,
                (name, _, _) => name is "onnxruntime" or "onnxruntime.dll" ? _handle : IntPtr.Zero);
            LoadedFrom = Path.GetFullPath(folder);
        }
    }
}
