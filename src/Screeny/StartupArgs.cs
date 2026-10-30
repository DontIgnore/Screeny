using System.Globalization;
using Screeny.Overlay;

namespace Screeny;

/// <summary>
/// Ключи командной строки.
/// <list type="bullet">
/// <item><c>--settings</c>, <c>--history</c> — открыть окно сразу после старта.</item>
/// <item><c>--dev</c> — тестовый экземпляр: свои имена mutex/event (суффикс <see cref="DevSuffix"/>), свой файл
/// настроек и папка истории, хоткей не регистрируется, снимок экрана не делается ни при старте, ни по клику в трее.</item>
/// <item><c>--overlay-test &lt;png&gt;</c> (только с <c>--dev</c>) — открыть оверлей по картинке, как будто это снимок экрана.</item>
/// <item><c>--burst-test &lt;папка&gt;</c> (только с <c>--dev</c>) — открыть оверлей так, будто PNG из папки (по имени файла)
/// — кадры серии, снятой удержанием клавиши снимка: лента кадров внизу, одинаковые подряд кадры отброшены, как в
/// настоящей серии. Клавиши при этом не опрашиваются и экран не снимается.</item>
/// <item><c>--select x,y,w,h</c> (только с <c>--overlay-test</c> или <c>--burst-test</c>) — готовое выделение.</item>
/// <item><c>--edit &lt;png&gt;</c> (только с <c>--dev</c>) — окно повторного редактирования снимка, как из истории.</item>
/// <item><c>--scenario &lt;имя&gt;</c> (с <c>--overlay-test</c>, <c>--burst-test</c>, <c>--edit</c>, <c>--history</c> или <c>--settings</c>) — готовое состояние окна
/// для проверки UI без ввода мышью: набор фигур оверлея, <c>original</c> для редактора, <c>hover</c>, <c>search</c> и
/// <c>semantic</c> (смысловой поиск на тестовом наборе спайка) для истории,
/// <c>hotkey-problems</c> для настроек, <c>conflicts</c> для мастера первого запуска.</item>
/// <item><c>--setup-wizard</c> — открыть мастер первого запуска (конфликты с другими программами) сразу после старта.
/// В <c>--dev</c> мастер работает только с подставной средой и подставными действиями; без этого ключа dev-экземпляр
/// мастер сам не показывает.</item>
/// <item><c>--enable-ll-hook</c> (только с <c>--dev</c>) — разрешить тестовому экземпляру низкоуровневый хук Win+Shift+S
/// (если он включён в настройках). Без этого ключа dev-экземпляр хук не ставит никогда.</item>
/// <item><c>--update-source &lt;папка&gt;</c> (только с <c>--dev</c>) — искать обновления в локальной папке релизов
/// (результат <c>tools/pack.ps1</c>) вместо GitHub. Без этого ключа dev-экземпляр обновления не ищет никогда.</item>
/// <item><c>--check-updates</c> (только с <c>--dev</c>) — сразу после старта ручная проверка обновлений.</item>
/// <item><c>--module-source &lt;папка&gt;</c> (только с <c>--dev</c>) — брать архивы модулей (<c>Screeny-OcrModule-N.zip</c>,
/// <c>Screeny-SemanticModule-N.zip</c>, общий <c>Screeny-OrtRuntime-N.zip</c> — результат скриптов <c>tools/pack-*.ps1</c>)
/// из локальной папки или её подпапок вместо GitHub. Без этого ключа dev-экземпляр модули не скачивает никогда.</item>
/// <item><c>--feedback-endpoint &lt;url&gt;</c> (только с <c>--dev</c>) — куда отправлять сообщения об ошибках и предложения
/// (свой тестовый сервер). Без этого ключа dev-экземпляр сообщения не отправляет никогда, а «Отправить» говорит, что адрес не задан.</item>
/// <item><c>--feedback bug|idea</c> — открыть окно сообщения об ошибке или предложения сразу после старта. С <c>--dev</c> и
/// <c>--scenario</c> (<c>sending</c>, <c>sent</c>, <c>offline</c>, <c>rate-limited</c>, <c>invalid</c>, <c>server</c>,
/// <c>validation</c>, <c>details</c>, <c>not-configured</c>) — готовое состояние окна с подставной отправкой, без сети.</item>
/// <item>Переменная окружения <c>SCREENY_DEV=1</c> — то же, что <c>--dev</c>: она переживает перезапуск через
/// Velopack-стаб и Update.exe, поэтому тестовая копия не превращается в рабочий экземпляр.</item>
/// </list>
/// Без <c>--dev</c> тестовые ключи игнорируются: рабочий экземпляр не открывает оверлей по чужому файлу.
/// </summary>
public sealed record StartupArgs(bool Dev, bool OpenSettings, bool OpenHistory, string? OverlayTest, PixelRect? Select, string? Scenario, string? Edit = null, bool EnableLlHook = false,
    string? UpdateSource = null, bool CheckUpdates = false, bool SetupWizard = false, string? ModuleSource = null,
    string? FeedbackEndpoint = null, string? Feedback = null, string? BurstTest = null)
{
    public const string DevSuffix = ".Dev";
    public const string DevEnvironmentVariable = "SCREENY_DEV";

    /// <summary>Ключи текущего процесса с учётом <see cref="DevEnvironmentVariable"/>.</summary>
    public static StartupArgs FromProcess(IReadOnlyList<string> args)
        => Parse(args, Environment.GetEnvironmentVariable(DevEnvironmentVariable));

    /// <summary>Суффикс имён объектов ядра и файлов тестового экземпляра.</summary>
    public string Suffix => Dev ? DevSuffix : "";

    /// <param name="devEnvironment">Значение <see cref="DevEnvironmentVariable"/>: «1» или «true» включают dev-режим.</param>
    public static StartupArgs Parse(IReadOnlyList<string> args, string? devEnvironment = null)
    {
        bool dev = devEnvironment?.Trim() is "1" || string.Equals(devEnvironment?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        bool settings = false, history = false, llHook = false, checkUpdates = false, wizard = false;
        string? overlay = null, scenario = null, edit = null, updateSource = null, moduleSource = null, feedbackEndpoint = null, feedback = null, burst = null;
        PixelRect? select = null;
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            if (Is(a, "--dev")) dev = true;
            else if (Is(a, "--settings")) settings = true;
            else if (Is(a, "--history")) history = true;
            else if (Is(a, "--overlay-test")) overlay = Next();
            else if (Is(a, "--burst-test")) burst = Next();
            else if (Is(a, "--scenario")) scenario = Next();
            else if (Is(a, "--select")) select = TryParseRect(Next());
            else if (Is(a, "--edit")) edit = Next();
            else if (Is(a, "--enable-ll-hook")) llHook = true;
            else if (Is(a, "--update-source")) updateSource = Next();
            else if (Is(a, "--check-updates")) checkUpdates = true;
            else if (Is(a, "--setup-wizard")) wizard = true;
            else if (Is(a, "--module-source")) moduleSource = Next();
            else if (Is(a, "--feedback-endpoint")) feedbackEndpoint = Next();
            else if (Is(a, "--feedback")) feedback = Next()?.Trim().ToLowerInvariant();
        }
        if (!dev) (overlay, select, scenario, edit, llHook, updateSource, checkUpdates, moduleSource, feedbackEndpoint, burst) = (null, null, null, null, false, null, false, null, null, null);
        if (string.IsNullOrWhiteSpace(feedbackEndpoint)) feedbackEndpoint = null;
        if (feedback is not ("bug" or "idea")) feedback = null;
        if (string.IsNullOrWhiteSpace(updateSource)) updateSource = null;
        if (string.IsNullOrWhiteSpace(moduleSource)) moduleSource = null;
        if (string.IsNullOrWhiteSpace(overlay)) overlay = null;
        if (string.IsNullOrWhiteSpace(burst)) burst = null;
        if (overlay is null && burst is null) select = null;
        if (string.IsNullOrWhiteSpace(edit)) edit = null;
        if (overlay is null && burst is null && edit is null && !history && !settings && !wizard && feedback is null) scenario = null;
        return new StartupArgs(dev, settings, history, overlay, select, scenario, edit, llHook, updateSource, checkUpdates, wizard, moduleSource,
            feedbackEndpoint, feedback, burst);
    }

    private static bool Is(string a, string key) => string.Equals(a, key, StringComparison.OrdinalIgnoreCase);

    /// <summary>«x,y,w,h» в пикселях картинки; null, если формат неверный или размер не положительный.</summary>
    public static PixelRect? TryParseRect(string? text)
    {
        var parts = text?.Split(',', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 4 }) return null;
        var v = new int[4];
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i])) return null;
        return v[2] > 0 && v[3] > 0 ? new PixelRect(v[0], v[1], v[2], v[3]) : null;
    }
}
