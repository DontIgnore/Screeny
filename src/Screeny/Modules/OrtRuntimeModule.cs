namespace Screeny.Modules;

/// <summary>
/// Общий компонент модулей: нативный ONNX Runtime 1.31.0 (<c>onnxruntime.dll</c>, <c>onnxruntime_providers_shared.dll</c>),
/// рантайм Visual C++, лицензии и <c>module.json</c>. Сам по себе не скачивается: его ставит модуль, которому он нужен
/// (<see cref="ModuleSpec.Requires"/> у <see cref="OcrModule"/> и <see cref="SemanticModule"/>), и удаляется вместе с
/// последним таким модулем. Версия связана с <c>Microsoft.ML.OnnxRuntime.Managed</c> в <c>Screeny.csproj</c>.
/// <para>
/// Новая версия — <c>tools/pack-ort-runtime.ps1 -Version N</c>, затем <see cref="Version"/>, <see cref="ZipSha256"/>,
/// <see cref="ManifestSha256"/> и <see cref="ZipSize"/> по выводу скрипта и новые версии модулей, которые его требуют.
/// Архив лежит в релизе GitHub с тегом <c>ort-runtime-N</c>.
/// </para>
/// </summary>
public static class OrtRuntimeModule
{
    public const string Id = "ort-runtime";
    public const int Version = 1;
    public const string ZipSha256 = "3feef64499d03b4dedbc79fed05d6c6387bb3d86e236584c0325bcea2c0bf795";
    public const string ManifestSha256 = "724461c1c16385ae58936d7ef84f9d331cc7b8787be89c897f35ceb3b1b417f2";
    public const long ZipSize = 18_384_141;

    public static string ZipName(int version) => $"Screeny-OrtRuntime-{version}.zip";

    public static ModuleSpec Spec => new(Id, Version, ZipSha256, ManifestSha256)
    {
        ZipName = ZipName(Version),
        DownloadUrl = new($"https://github.com/DontIgnore/Screeny/releases/download/ort-runtime-{Version}/{ZipName(Version)}"),
        ZipSize = ZipSize,
    };

    /// <summary>Папка компонента рядом с папкой модуля (все модули лежат в одном корне, <see cref="OcrModule.ModulesRoot"/>).</summary>
    public static string FolderNextTo(string moduleFolder)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(moduleFolder).TrimEnd(Path.DirectorySeparatorChar))!, Spec.FolderName);

    /// <summary>
    /// Загружает ORT из компонента рядом с <paramref name="moduleFolder"/>. Перед первой загрузкой в процесс файлы
    /// сверяются с <c>module.json</c>, а сам он — с <see cref="ManifestSha256"/>: нативный код грузится только проверенный.
    /// Бросает <see cref="OrtLoadException"/> с текстом для пользователя.
    /// </summary>
    public static void EnsureLoaded(string moduleFolder)
    {
        var folder = FolderNextTo(moduleFolder);
        if (OrtNative.LoadedFrom is { } loaded && string.Equals(Path.GetFullPath(loaded), folder, StringComparison.OrdinalIgnoreCase)) return;
        if (OrtNative.LoadedFrom is null && ModuleManifest.Verify(folder, Id, Version, ManifestSha256) is { } error)
            throw new OrtLoadException(Localization.Loc.F("PreciseOcr.FilesDamaged", error));
        OrtNative.EnsureLoaded(folder);
    }
}
