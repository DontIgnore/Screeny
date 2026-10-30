namespace Screeny.Modules;

/// <summary>
/// Скачиваемый модуль точного распознавания текста (PaddleOCR PP-OCRv5 на ONNX Runtime). Кто его не включал, ничего
/// не скачивает, и exe остаётся прежнего размера. В модуле только данные: модели <c>det.onnx</c>
/// (ch_PP-OCRv5_det_mobile) и <c>rec.onnx</c> (cyrillic_PP-OCRv5_rec_mobile), лицензии и <c>module.json</c> с SHA256
/// каждого файла. ONNX Runtime и рантайм Visual C++ — общий компонент <see cref="OrtRuntimeModule"/> (до версии 2 модуль
/// вёз их сам; у кого стоит версия 1, тому предлагается «Обновить модуль»). Вся обработка — в приложении
/// (<c>Ocr/Paddle</c>), поэтому новая версия модуля нужна редко.
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
    public const int Version = 2;
    public const string ZipSha256 = "003d40f45286f601c2b70521fe27d3f22dbd9d70533252bab5feb5b90c467b20";
    /// <summary>SHA256 <c>module.json</c> из этого архива: по нему проверяются уже установленные файлы (манифест на диске
    /// сам по себе доверия не заслуживает).</summary>
    public const string ManifestSha256 = "eae3bfa912a629d5b5aed98af7c950f2857ed77a4a660dbfd767b8d0ee48b768";
    /// <summary>Размер архива в байтах: для подписи «Скачать (N МБ)» и проверки длины ответа.</summary>
    public const long ZipSize = 12_907_061;

    public static string ZipName(int version) => $"Screeny-OcrModule-{version}.zip";
    public static Uri DownloadUrl(int version) => new($"https://github.com/DontIgnore/Screeny/releases/download/ocr-module-{version}/{ZipName(version)}");
    public static string FolderName(int version) => $"{Id}-{version}";

    /// <summary>Что ставит и проверяет приложение.</summary>
    public static ModuleSpec Spec => new(Id, Version, ZipSha256, ManifestSha256)
    {
        ZipName = ZipName(Version),
        DownloadUrl = DownloadUrl(Version),
        ZipSize = ZipSize,
        Requires = [OrtRuntimeModule.Spec],
    };

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

    /// <summary>Подпись размера для мастера первого запуска (там ещё ничего не скачано): модуль вместе с общим ORT.</summary>
    public static string SizeText => MegabytesText(ZipSize + OrtRuntimeModule.ZipSize);

    /// <summary>Подпись размера: «30 МБ» — в двоичных мегабайтах, как размер файла показывает Проводник.</summary>
    public static string MegabytesText(long bytes) => Localization.Loc.F("Units.Megabytes", Math.Max(1, (int)Math.Round(bytes / 1048576.0)));
}
