using Screeny.Features;
using Screeny.Modules;
using Screeny.Settings;
using Screeny.Updates;

namespace Screeny.Tests.Modules;

/// <summary>«Точное распознавание активно» и его метка в списке изменений.</summary>
public class PreciseOcrRulesTests
{
    private const int Required = OcrModule.Version;

    private static AppSettings Settings(bool optedIn, bool ocrOn = true)
    {
        var s = new AppSettings { PreciseOcr = optedIn };
        s.SetFeature(FeatureCatalog.Ocr, ocrOn);
        return s;
    }

    [Fact]
    public void Default_IsOff() => Assert.False(new AppSettings().PreciseOcr);

    [Theory]
    [InlineData(true, true, Required, true)]
    [InlineData(false, true, Required, false)]     // модуль стоит, но согласия нет
    [InlineData(true, false, Required, false)]     // распознавание текста выключено
    [InlineData(true, true, null, false)]          // модуля нет
    [InlineData(true, true, Required + 1, false)]  // другая версия
    public void IsActive(bool optedIn, bool ocrOn, int? installed, bool active)
        => Assert.Equal(active, PreciseOcrRules.IsActive(Settings(optedIn, ocrOn), installed));

    [Theory]
    [InlineData(true, Required + 1, true)]
    [InlineData(true, Required, false)]
    [InlineData(true, null, false)]
    [InlineData(false, Required + 1, false)]       // не включали — и напоминать не о чем
    public void NeedsModuleUpdate(bool optedIn, int? installed, bool needs)
        => Assert.Equal(needs, PreciseOcrRules.NeedsModuleUpdate(Settings(optedIn), installed));

    [Fact]
    public void NotesFilter_RoutesTagThroughPredicate_OthersThroughFeatures()
    {
        var s = Settings(optedIn: true);
        s.SetFeature(FeatureCatalog.Pins, false);
        int? installed = null;
        var isOn = PreciseOcrRules.NotesFilter(s, () => installed);
        Assert.False(isOn(PreciseOcrRules.NotesTag)); // для AppSettings.IsFeatureOn неизвестный id — «включён»
        Assert.True(isOn(FeatureCatalog.Ocr));
        Assert.False(isOn(FeatureCatalog.Pins));
        installed = Required;                          // версия читается при каждом вызове
        Assert.True(isOn(PreciseOcrRules.NotesTag));
    }

    private const string PreciseOnly = """
        # Screeny 0.3.1
        - Точное распознавание быстрее на полном экране. <!-- feature: ocr-precise -->
        """;

    private const string Mixed = """
        # Screeny 0.3.1
        - Точное распознавание быстрее на полном экране. <!-- feature: ocr-precise -->
        - Исправлена история. <!-- feature: history -->
        """;

    private static UpdateCandidate Next(string notes) => new("0.3.1", notes, EarlierNotes: []);

    [Theory]
    [InlineData(false, Required)]  // не согласился
    [InlineData(true, null)]       // модуль не установлен
    public void TaggedNote_HiddenWhenNotActive(bool optedIn, int? installed)
    {
        var prompt = UpdatePrompt.Decide(Next(Mixed), PreciseOcrRules.NotesFilter(Settings(optedIn), () => installed), UpdateCheckKind.Automatic);
        Assert.Equal(UpdatePromptAction.Show, prompt.Action);
        var text = string.Concat(prompt.Notes.SelectMany(b => b.Inlines).Select(i => i.Text));
        Assert.DoesNotContain("Точное распознавание", text);
        Assert.Contains("история", text);
    }

    [Fact]
    public void TaggedNote_ShownWhenActive()
    {
        var prompt = UpdatePrompt.Decide(Next(PreciseOnly), PreciseOcrRules.NotesFilter(Settings(true), () => Required), UpdateCheckKind.Automatic);
        Assert.Equal(UpdatePromptAction.Show, prompt.Action);
        Assert.Contains("Точное распознавание", string.Concat(prompt.Notes.SelectMany(b => b.Inlines).Select(i => i.Text)));
    }

    [Fact]
    public void OnlyPreciseNotes_NotActive_ApplySilently_LikeDisabledFeatures()
    {
        // Всё про точное распознавание, которым пользователь не пользуется: как про выключенную функцию — молча при выходе;
        // ручная проверка всё равно показывает окно.
        var isOn = PreciseOcrRules.NotesFilter(Settings(false), () => null);
        Assert.Equal(UpdatePromptAction.ApplySilently, UpdatePrompt.Decide(Next(PreciseOnly), isOn, UpdateCheckKind.Automatic).Action);
        var manual = UpdatePrompt.Decide(Next(PreciseOnly), isOn, UpdateCheckKind.Manual);
        Assert.Equal(UpdatePromptAction.Show, manual.Action);
        Assert.True(manual.OnlyDisabledFeatures);
    }

    [Fact]
    public void OnlyPreciseNotes_Active_IsNeverSilent()
        => Assert.Equal(UpdatePromptAction.Show,
            UpdatePrompt.Decide(Next(PreciseOnly), PreciseOcrRules.NotesFilter(Settings(true), () => Required), UpdateCheckKind.Automatic).Action);

    [Fact]
    public void OnlyPreciseNotes_WithPlainIsFeatureOn_WouldWronglyCountAsOn()
        // Почему нужен свой фильтр: обычная проверка функций считает «ocr-precise» включённым у всех.
        => Assert.Equal(UpdatePromptAction.Show, UpdatePrompt.Decide(Next(PreciseOnly), Settings(false).IsFeatureOn, UpdateCheckKind.Automatic).Action);

    [Fact]
    public void PreciseNote_AlsoTaggedOcr_FollowsOcrFeature()
    {
        const string notes = "- Новое в режиме текста. <!-- feature: ocr, ocr-precise -->";
        var isOn = PreciseOcrRules.NotesFilter(Settings(false), () => null);
        Assert.Equal(UpdatePromptAction.Show, UpdatePrompt.Decide(Next(notes), isOn, UpdateCheckKind.Automatic).Action);
        var ocrOff = PreciseOcrRules.NotesFilter(Settings(true, ocrOn: false), () => Required);
        Assert.Equal(UpdatePromptAction.ApplySilently, UpdatePrompt.Decide(Next(notes), ocrOff, UpdateCheckKind.Automatic).Action);
    }

    [Fact]
    public void Notes_AnnounceToOcrUsers_DetailsOnlyToActive()
    {
        const string notes = "## Режим текста <!-- feature: ocr -->\n\n- Точное распознавание: модуль скачивается кнопкой.\n"
                           + "- Уточнённый текст приходит следом. <!-- feature: ocr-precise -->\n";
        var blocks = ReleaseNotes.Parse(notes);
        Assert.Contains(blocks, b => b.Features.SequenceEqual([FeatureCatalog.Ocr]));
        Assert.Contains(blocks, b => b.Features.SequenceEqual([PreciseOcrRules.NotesTag]));
        // Кто модуль не скачивал, видит объявление (метка ocr), но не подробности (метка ocr-precise).
        var visible = ReleaseNotes.Filter(blocks, PreciseOcrRules.NotesFilter(Settings(false), () => null));
        Assert.DoesNotContain(visible, b => b.Features.Contains(PreciseOcrRules.NotesTag));
        Assert.Contains(visible, b => b.Features.Contains(FeatureCatalog.Ocr));
    }

    [Fact]
    public void ReleaseNotes020_HaveNoPreciseTag()
    {
        var path = Screeny.Tests.Updates.ReleaseNotesTests.RepoFile("docs", "releases", "0.2.0.md");
        Assert.DoesNotContain(ReleaseNotes.Parse(File.ReadAllText(path)), b => b.Features.Contains(PreciseOcrRules.NotesTag));
    }

    [Theory]
    [InlineData(null, false, false, true, true)]           // модуля нет, источник есть — предлагаем
    [InlineData(Required + 1, false, false, true, true)]   // устаревший — предлагаем обновить
    [InlineData(Required, false, false, true, false)]      // уже стоит
    [InlineData(null, true, false, true, false)]           // ждёт удаления после перезапуска: файлы заняты
    [InlineData(null, false, true, true, false)]           // уже качается
    [InlineData(null, false, false, false, false)]         // dev без --module-source
    public void WizardOffersDownload(int? installed, bool removalPending, bool downloading, bool source, bool offered)
        => Assert.Equal(offered, PreciseOcrRules.WizardOffersDownload(new ModuleStatus(installed, downloading, 0, removalPending, null, source)));

    [Fact]
    public void WizardOffersDownload_NoController_False() => Assert.False(PreciseOcrRules.WizardOffersDownload(null));

    private const string Ours = @"C:\Users\u\AppData\Local\Screeny\Modules\ocr-paddle-1\msvcp140.dll";

    [Theory]
    [InlineData(@"C:\Windows\System32\msvcp140.dll", "14.29.30133.0", "14.50.35710.0", true)]   // старый из System32 — отказ
    [InlineData(@"C:\Windows\System32\msvcp140.dll", "14.50.35710.0", "14.50.35710.0", false)]  // та же версия
    [InlineData(@"C:\Windows\System32\msvcp140.dll", "14.51.1.0", "14.50.35710.0", false)]      // новее — годится
    [InlineData(Ours, "14.29.30133.0", "14.50.35710.0", false)]                                    // это наша копия
    [InlineData(@"C:\Windows\System32\msvcp140.dll", null, "14.50.35710.0", false)]               // версия неизвестна
    public void CrtConflict(string loaded, string? loadedVersion, string ourVersion, bool conflict)
    {
        var text = Screeny.Modules.OrtNative.CrtConflict("msvcp140.dll", loaded, loadedVersion is null ? null : Version.Parse(loadedVersion), Ours, Version.Parse(ourVersion));
        Assert.Equal(conflict, text is not null);
        if (text is not null)
        {
            Assert.Contains("14.29.30133.0", text);
            Assert.Contains(@"C:\Windows\System32", text);
        }
    }
}
