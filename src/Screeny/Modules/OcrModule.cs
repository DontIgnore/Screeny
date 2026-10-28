namespace Screeny.Modules;

/// <summary>
/// Скачиваемый модуль точного распознавания текста (PaddleOCR PP-OCRv5 на ONNX Runtime). Кто его не включал, ничего
/// не скачивает, и exe остаётся прежнего размера. В модуле только данные и нативный рантайм: модели <c>det.onnx</c>
/// (ch_PP-OCRv5_det_mobile) и <c>rec.onnx</c> (cyrillic_PP-OCRv5_rec_mobile), <c>onnxruntime.dll</c> 1.31.0 и рантайм
/// Visual C++, лицензии и <c>module.json</c> с SHA256 каждого файла. Вся обработка — в приложении (<c>Ocr/Paddle</c>),
/// поэтому новая версия модуля нужна редко.
/// <para>
/// Версия, адрес и SHA256 архива зашиты здесь: приложение принимает только этот архив. Новая версия модуля —
/// <c>tools/pack-ocr-module.ps1 -Version N</c>, затем <see cref="Version"/>, <see cref="ZipSha256"/>,
/// <see cref="ManifestSha256"/> и <see cref="ZipSize"/> по выводу скрипта (см. tools/ocr-module/README.md). Архив лежит в релизе GitHub
/// со своим тегом <c>ocr-module-N</c>, не связанным с версиями приложения.
/// </para>
/// </summary>
public static class OcrModule
{
    public const string Id = "ocr-paddle";
    /// <summary>Версия модуля, которая нужна этой сборке приложения.</summary>
    public const int Version = 1;
    public const string ZipSha256 = "048d3afbebfaac00f5e617449f8b42b2bb1a0940a4fd7aff49f4a2c8cd29fa0c";
    /// <summary>SHA256 <c>module.json</c> из этого архива: по нему проверяются уже установленные файлы (манифест на диске
    /// сам по себе доверия не заслуживает).</summary>
    public const string ManifestSha256 = "39067fddbf4bcd286694536a82194552eeb442ec7ebd44ceb351ae0594ac760f";
    /// <summary>Размер архива в байтах: для подписи «Скачать (N МБ)» и проверки длины ответа.</summary>
    public const long ZipSize = 31_290_305;

    public static string ZipName(int version) => $"Screeny-OcrModule-{version}.zip";
    public static Uri DownloadUrl(int version) => new($"https://github.com/DontIgnore/Screeny/releases/download/ocr-module-{version}/{ZipName(version)}");
    public static string FolderName(int version) => $"{Id}-{version}";

    /// <summary>Что ставит и проверяет приложение.</summary>
    public static ModuleSpec Spec => new(Id, Version, ZipSha256, ManifestSha256);

    /// <summary>
    /// Папка модулей: <c>%LocalAppData%\Screeny\Modules</c> — рядом с историей, но не в папке установки Velopack, поэтому
    /// переживает обновления приложения; портативная версия пользуется той же. Тестовый экземпляр — своя папка
    /// <c>%LocalAppData%\Screeny\Dev\Modules</c>, как и своя история.
    /// </summary>
    public static string ModulesRoot(bool dev)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return dev ? Path.Combine(local, "Screeny", "Dev", "Modules") : Path.Combine(local, "Screeny", "Modules");
    }

    /// <summary>Подпись размера: «30 МБ» — в двоичных мегабайтах, как размер файла показывает Проводник.</summary>
    public static string SizeText => Localization.Loc.F("Units.Megabytes", Math.Max(1, (int)Math.Round(ZipSize / 1048576.0)));
}
