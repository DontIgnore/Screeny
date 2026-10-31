using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Screeny.Modules;

namespace Screeny.Tests.Modules;

/// <summary>Temp folder and build of test module archives (real format, own data).</summary>
public sealed class ModuleTestFiles : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "screeny-module-tests-" + Guid.NewGuid().ToString("N"));

    public ModuleTestFiles() => Directory.CreateDirectory(Dir);

    public void Dispose() { try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } }

    public static readonly Dictionary<string, byte[]> DefaultFiles = new()
    {
        ["det.onnx"] = Encoding.ASCII.GetBytes("det model bytes"),
        ["rec.onnx"] = Encoding.ASCII.GetBytes("rec model bytes, a bit longer"),
        ["LICENSES/NOTICE.txt"] = Encoding.ASCII.GetBytes("notice"),
    };

    public static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static byte[] Manifest(string id, int version, IReadOnlyDictionary<string, byte[]> files)
        => JsonSerializer.SerializeToUtf8Bytes(new ModuleManifest(id, version,
            files.Select(f => new ModuleFile(f.Key, f.Value.Length, Sha(f.Value))).ToList()));

    /// <summary>Module archive: module.json per <paramref name="files"/> (or own <paramref name="manifest"/>) and files.</summary>
    public static byte[] Zip(IReadOnlyDictionary<string, byte[]> files, byte[]? manifest = null, string id = "ocr-paddle", int version = 1,
        IEnumerable<KeyValuePair<string, byte[]>>? extra = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] data) { using var s = zip.CreateEntry(name).Open(); s.Write(data); }
            Add(ModuleManifest.FileName, manifest ?? Manifest(id, version, files));
            foreach (var (name, data) in files) Add(name, data);
            foreach (var (name, data) in extra ?? []) Add(name, data);
        }
        return ms.ToArray();
    }

    public static ModuleSpec SpecFor(byte[] zip, int version = 1) => new("ocr-paddle", version, Sha(zip));

    /// <summary>Module folder as after install.</summary>
    public string Installed(string root, int version, IReadOnlyDictionary<string, byte[]>? files = null)
    {
        files ??= DefaultFiles;
        var dir = Path.Combine(root, $"ocr-paddle-{version}");
        foreach (var (name, data) in files)
        {
            var path = Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
        File.WriteAllBytes(Path.Combine(dir, ModuleManifest.FileName), Manifest("ocr-paddle", version, files));
        return dir;
    }
}
