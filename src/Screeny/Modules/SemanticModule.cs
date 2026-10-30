namespace Screeny.Modules;

/// <summary>
/// Скачиваемый модуль смыслового поиска по истории: EmbeddingGemma 2 (Google DeepMind) в 4-битном ONNX от onnx-community —
/// текстовая модель и визуальный кодировщик, <c>tokenizer.model</c>, лицензии и <c>module.json</c>. Нативный ORT —
/// общий компонент <see cref="OrtRuntimeModule"/>. Вся обработка (токенизация, подготовка картинки, поиск) — в приложении
/// (<c>Semantic/</c>). Модуль установлен — поиск по смыслу включён (отдельной галочки нет); удалили — выключен.
/// <para>
/// Новая версия — <c>tools/pack-semantic-module.ps1 -Version N</c>, затем <see cref="Version"/>, <see cref="ZipSha256"/>,
/// <see cref="ManifestSha256"/> и <see cref="ZipSize"/> по выводу скрипта. Архив лежит в релизе GitHub с тегом
/// <c>semantic-module-N</c>.
/// </para>
/// </summary>
public static class SemanticModule
{
    public const string Id = "semantic-eg2";
    public const int Version = 1;
    public const string ZipSha256 = "66915a22079da825e4e96f975ffaf311f529bc4ffb3ddb6224d5f0694f649ca5";
    public const string ManifestSha256 = "8085bd8539899d6525fea93ff49a56d78734970ac980dbd110cb9780c34fa30a";
    public const long ZipSize = 288_340_345;

    /// <summary>Файлы модуля (имена как в выгрузке onnx-community: графы ссылаются на свои <c>*.onnx_data</c>).</summary>
    public const string TextModelFile = "onnx/model_q4.onnx", VisionFile = "onnx/vision_encoder_q4.onnx", TokenizerFile = "tokenizer.model";

    public static string ZipName(int version) => $"Screeny-SemanticModule-{version}.zip";

    public static ModuleSpec Spec => new(Id, Version, ZipSha256, ManifestSha256, MaxZipBytes: 320_000_000, MaxUnpackedBytes: 320_000_000)
    {
        ZipName = ZipName(Version),
        DownloadUrl = new($"https://github.com/DontIgnore/Screeny/releases/download/semantic-module-{Version}/{ZipName(Version)}"),
        ZipSize = ZipSize,
        Requires = [OrtRuntimeModule.Spec],
    };
}
