namespace Screeny.Modules;

/// <summary>
/// Downloadable precise text recognition module (PaddleOCR PP-OCRv5 on ONNX Runtime). Who didn't enable it downloads nothing,
/// and exe stays same size. Module has only data and native runtime: models <c>det.onnx</c>
/// (ch_PP-OCRv5_det_mobile) and <c>rec.onnx</c> (cyrillic_PP-OCRv5_rec_mobile), <c>onnxruntime.dll</c> 1.31.0 and runtime
/// Visual C++, licenses and <c>module.json</c> with SHA256 of each file. All processing in app (<c>Ocr/Paddle</c>),
/// so new module version rarely needed.
/// <para>
/// Version, address and SHA256 of archive hardcoded here: app accepts only this archive. New module version —
/// <c>tools/pack-ocr-module.ps1 -Version N</c>, then <see cref="Version"/>, <see cref="ZipSha256"/>,
/// <see cref="ManifestSha256"/> and <see cref="ZipSize"/> from script output (see tools/ocr-module/README.md). Archive in GitHub release
/// with its own tag <c>ocr-module-N</c>, unrelated to app versions.
/// </para>
/// </summary>
public static class OcrModule
{
    public const string Id = "ocr-paddle";
    /// <summary>Module version needed by this app build.</summary>
    public const int Version = 1;
    public const string ZipSha256 = "048d3afbebfaac00f5e617449f8b42b2bb1a0940a4fd7aff49f4a2c8cd29fa0c";
    /// <summary>SHA256 of <c>module.json</c> from this archive: installed files checked against it (manifest on disk
    /// alone is untrustworthy).</summary>
    public const string ManifestSha256 = "39067fddbf4bcd286694536a82194552eeb442ec7ebd44ceb351ae0594ac760f";
    /// <summary>Archive size in bytes: for “Download (N MB)” label and response length check.</summary>
    public const long ZipSize = 31_290_305;

    public static string ZipName(int version) => $"Screeny-OcrModule-{version}.zip";
    public static Uri DownloadUrl(int version) => new($"https://github.com/DontIgnore/Screeny/releases/download/ocr-module-{version}/{ZipName(version)}");
    public static string FolderName(int version) => $"{Id}-{version}";

    /// <summary>What app installs and verifies.</summary>
    public static ModuleSpec Spec => new(Id, Version, ZipSha256, ManifestSha256);

    /// <summary>
    /// Modules folder: <c>%LocalAppData%\Screeny\Modules</c> — near history but not in Velopack install folder, so
    /// survives app updates; portable version uses same. Test instance has own folder
    /// <c>%LocalAppData%\Screeny\Dev\Modules</c>, like own history.
    /// </summary>
    public static string ModulesRoot(bool dev)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return dev ? Path.Combine(local, "Screeny", "Dev", "Modules") : Path.Combine(local, "Screeny", "Modules");
    }

    /// <summary>Size label: “30 MB” — binary megabytes, like Explorer shows file size.</summary>
    public static string SizeText => Localization.Loc.F("Units.Megabytes", Math.Max(1, (int)Math.Round(ZipSize / 1048576.0)));
}
