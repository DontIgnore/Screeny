using Screeny.Features;
using Screeny.Settings;

namespace Screeny.Modules;

/// <summary>
/// Правила точного распознавания. Это не функция из <see cref="FeatureCatalog"/>: каталог хранит выключенные id, и новый id
/// считался бы включённым у всех. Здесь наоборот — согласие (<see cref="AppSettings.PreciseOcr"/>, по умолчанию выключено)
/// и модуль на диске нужной версии.
/// </summary>
public static class PreciseOcrRules
{
    /// <summary>Метка пунктов списка изменений (<c>&lt;!-- feature: ocr-precise --&gt;</c>), видных только тем, у кого
    /// точное распознавание активно.</summary>
    public const string NotesTag = "ocr-precise";

    /// <summary>
    /// «Точное распознавание активно»: функция «Распознавание текста и QR» включена, пользователь согласился, и установлен
    /// модуль ровно той версии, что нужна этой сборке. Единственная такая проверка — её спрашивают и режим текста,
    /// и фильтр списка изменений.
    /// </summary>
    public static bool IsActive(AppSettings settings, int? installedVersion)
        => settings.IsFeatureOn(FeatureCatalog.Ocr) && settings.PreciseOcr && installedVersion == OcrModule.Version;

    /// <summary>Модуль стоит, но не той версии (приложение обновилось): работает Windows OCR, в настройках — «Обновить модуль».
    /// Сам модуль никогда не перекачивается без нажатия.</summary>
    public static bool NeedsModuleUpdate(AppSettings settings, int? installedVersion)
        => settings.IsFeatureOn(FeatureCatalog.Ocr) && settings.PreciseOcr && installedVersion is { } v && v != OcrModule.Version;

    /// <summary>
    /// Мастер предлагает «Скачать точное распознавание»: модуля нужной версии нет, удаление не ждёт перезапуска (загрузка
    /// упёрлась бы в занятые файлы), загрузка ещё не идёт и скачать есть откуда (dev без <c>--module-source</c> — нет).
    /// </summary>
    public static bool WizardOffersDownload(PreciseOcrStatus? status)
        => status is { Ready: false, RemovalPending: false, Downloading: false, SourceAvailable: true };

    /// <summary>
    /// <c>isOn</c> для фильтра списка изменений (<see cref="Updates.ReleaseNotes.Filter"/>, <see cref="Updates.UpdatePrompt"/>):
    /// метка <see cref="NotesTag"/> — по <see cref="IsActive"/>, остальные — по выключенным функциям. Без этого
    /// <see cref="AppSettings.IsFeatureOn"/> считал бы неизвестный ему id включённым.
    /// </summary>
    public static Func<string, bool> NotesFilter(AppSettings settings, Func<int?> installedVersion)
        => id => id == NotesTag ? IsActive(settings, installedVersion()) : settings.IsFeatureOn(id);
}
