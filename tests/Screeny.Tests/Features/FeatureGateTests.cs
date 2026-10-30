using System.Windows;
using System.Windows.Controls;
using Screeny.Editing;
using Screeny.Features;
using Screeny.Overlay;
using Screeny.Overlay.Tools;
using Screeny.Settings;

namespace Screeny.Tests.Features;

public class FeatureGateTests
{
    internal static AppSettings Without(params string[] features)
    {
        var s = new AppSettings();
        foreach (var f in features) s.SetFeature(f, false);
        return s;
    }

    [Fact]
    public void AllOn_EverythingAllowed()
    {
        var s = new AppSettings();
        Assert.All(Enum.GetValues<ToolKind>(), k => Assert.True(s.ToolAllowed(k)));
        Assert.All(Enum.GetValues<CaptureAction>(), a => Assert.True(s.ActionAllowed(a)));
    }

    [Fact]
    public void Ruler_OnlyRulerToolGated()
    {
        var s = Without(FeatureCatalog.Ruler);
        Assert.False(s.ToolAllowed(ToolKind.Ruler));
        Assert.All(Enum.GetValues<ToolKind>().Where(k => k != ToolKind.Ruler), k => Assert.True(s.ToolAllowed(k)));
    }

    [Fact]
    public void PrivacyCheck_NeedsItsFeatureAndOcr()
    {
        Assert.True(new AppSettings().PrivacyCheckAllowed());
        Assert.False(Without(FeatureCatalog.Privacy).PrivacyCheckAllowed());
        Assert.False(Without(FeatureCatalog.Ocr).PrivacyCheckAllowed());
    }

    [Theory]
    [InlineData(FeatureCatalog.Pins, CaptureAction.Pin)]
    [InlineData(FeatureCatalog.Ocr, CaptureAction.CopyText)]
    public void Actions_GatedByTheirFeature(string feature, CaptureAction action)
    {
        var s = Without(feature);
        Assert.False(s.ActionAllowed(action));
        Assert.True(s.ActionAllowed(CaptureAction.Copy));
        Assert.True(s.ActionAllowed(CaptureAction.CopyExplicit));
        Assert.True(s.ActionAllowed(CaptureAction.Save)); // «Сохранить как» — не история
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)] // без истории Enter копирует всегда
    [InlineData(false, true, true)]
    public void CopyOnEnter_ForcedWithoutHistory(bool history, bool copyOnCapture, bool expected)
    {
        var s = history ? new AppSettings() : Without(FeatureCatalog.History);
        s.CopyOnCapture = copyOnCapture;
        Assert.Equal(expected, s.CopyOnEnter());
        Assert.Equal(copyOnCapture, s.CopyOnCapture); // сохранённое значение не меняется
    }

    [Fact]
    public void MergeDisabled_KeepsUnknownIdsAndOrder()
    {
        var merged = FeatureGate.MergeDisabled(["future-thing", FeatureCatalog.Ocr],
            [new(FeatureCatalog.Ocr, true), new(FeatureCatalog.Pins, false), new(FeatureCatalog.Ruler, true)]);
        Assert.Equal(["future-thing", FeatureCatalog.Pins], merged);
    }

    [Fact]
    public void KeepHiddenSettings_HistoryOff_HistoryFieldsUnchanged()
    {
        var current = new AppSettings { HistoryFolder = @"C:\old", HistoryLimit = 42, KeepOriginals = true, CopyOnCapture = true };
        var updated = new AppSettings { HistoryFolder = @"C:\new", HistoryLimit = 7, KeepOriginals = false, CopyOnCapture = false, ExcludePinsFromCapture = true };
        updated.SetFeature(FeatureCatalog.History, false);
        FeatureGate.KeepHiddenSettings(updated, current);
        Assert.Equal(@"C:\old", updated.HistoryFolder);
        Assert.Equal(42, updated.HistoryLimit);
        Assert.True(updated.KeepOriginals); // выключение истории не выглядит как выключение оригиналов
        Assert.True(updated.CopyOnCapture);
        Assert.True(updated.ExcludePinsFromCapture); // закрепление включено — его поле из окна
    }

    [Fact]
    public void KeepHiddenSettings_PinsOff_ExcludePinsUnchanged_HistoryFromWindow()
    {
        var current = new AppSettings { ExcludePinsFromCapture = true, HistoryLimit = 42 };
        var updated = new AppSettings { ExcludePinsFromCapture = false, HistoryLimit = 7 };
        updated.SetFeature(FeatureCatalog.Pins, false);
        FeatureGate.KeepHiddenSettings(updated, current);
        Assert.True(updated.ExcludePinsFromCapture);
        Assert.Equal(7, updated.HistoryLimit);
    }
}

/// <summary>Галочки функций окна настроек и мастера; кнопки панели по функциям.</summary>
[Collection(WpfControls.Name)]
public class FeatureControlsTests
{
    private static void Sta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    [Fact]
    public void CheckList_OneBoxAndCaptionPerFeature_UncheckedForDisabled() => Sta(() =>
    {
        var panel = new StackPanel();
        var list = new FeatureCheckList(panel, [FeatureCatalog.Pins, "future-thing"]);
        Assert.Equal(FeatureCatalog.All.Count * 2, panel.Children.Count);
        var boxes = panel.Children.OfType<CheckBox>().ToList();
        Assert.Equal(FeatureCatalog.All.Select(f => f.Title), boxes.Select(b => (string)b.Content));
        Assert.Equal(FeatureCatalog.All.Select(f => f.Description), panel.Children.OfType<TextBlock>().Select(t => t.Text));
        Assert.False(list.IsOn(FeatureCatalog.Pins));
        Assert.True(list.IsOn(FeatureCatalog.History));

        int changes = 0;
        list.Changed += () => changes++;
        boxes[FeatureCatalog.All.ToList().FindIndex(f => f.Id == FeatureCatalog.History)].IsChecked = false;
        Assert.Equal(1, changes);
        Assert.Equal([new(FeatureCatalog.History, false)], list.Changes); // только переключённое
        boxes[FeatureCatalog.All.ToList().FindIndex(f => f.Id == FeatureCatalog.History)].IsChecked = true;
        Assert.Empty(list.Changes); // вернули как было — изменений нет
    });

    [Fact]
    public void Toolbar_ApplyFeatures_HidesAndShowsButtons_OcrStaysHiddenInEditor() => Sta(() =>
    {
        var toolbar = new ToolbarControl();
        UIElement Named(string name) => (UIElement)toolbar.FindName(name);
        var s = new AppSettings();
        foreach (var f in FeatureCatalog.All) s.SetFeature(f.Id, false);
        toolbar.ApplyFeatures(s);
        foreach (var name in new[] { "OcrButton", "PinButton", "BeautifyButton", "PipetteButton" })
            Assert.Equal(Visibility.Collapsed, Named(name).Visibility);
        Assert.Equal(Visibility.Collapsed, toolbar.ToolButton(ToolKind.Ruler).Visibility);
        Assert.Equal(Visibility.Visible, toolbar.ToolButton(ToolKind.Rect).Visibility);
        Assert.Equal(Visibility.Visible, Named("CopyButton").Visibility);

        toolbar.ConfigureForEditor(s);
        string CopyTip() => (string)((FrameworkElement)Named("CopyButton")).ToolTip;
        Assert.DoesNotContain("истори", CopyTip()); // история выключена: «Копировать» запись не добавляет
        s.DisabledFeatures.Clear();
        toolbar.ApplyFeatures(s);
        foreach (var name in new[] { "PinButton", "BeautifyButton", "PipetteButton" })
            Assert.Equal(Visibility.Visible, Named(name).Visibility);
        Assert.Equal(Visibility.Visible, toolbar.ToolButton(ToolKind.Ruler).Visibility);
        Assert.Equal(Visibility.Collapsed, Named("OcrButton").Visibility); // в окне редактора OCR нет никогда
        Assert.Contains("истори", CopyTip());
        s.SetFeature(FeatureCatalog.History, false); // выключили, пока редактор открыт
        toolbar.ApplyFeatures(s);
        Assert.DoesNotContain("истори", CopyTip());
    });

    [Fact]
    public void CheckList_Sync_TakesLiveValuesButKeepsOwnToggles() => Sta(() =>
    {
        // Окно настроек открыто, из него запущен мастер: тот выключил OCR и историю. В окне до этого сняли «Линейку».
        var panel = new StackPanel();
        var list = new FeatureCheckList(panel, []);
        var boxes = panel.Children.OfType<CheckBox>().ToList();
        CheckBox Box(string id) => boxes[FeatureCatalog.All.ToList().FindIndex(f => f.Id == id)];
        Box(FeatureCatalog.Ruler).IsChecked = false;
        Box(FeatureCatalog.History).IsChecked = false; // и здесь, и в мастере

        list.Sync([FeatureCatalog.Ocr, FeatureCatalog.History]);

        Assert.False(list.IsOn(FeatureCatalog.Ocr));    // нетронутая — как в живых настройках
        Assert.False(list.IsOn(FeatureCatalog.History));
        Assert.False(list.IsOn(FeatureCatalog.Ruler));  // своё переключение не теряется
        Assert.True(list.IsOn(FeatureCatalog.Pins));
        Assert.Equal([new(FeatureCatalog.Ruler, false)], list.Changes); // от новых настроек: только линейка
    });

    [Fact]
    public void EditorCloseQuestion_FollowsHistoryAndPins()
    {
        Assert.Contains("в историю", EditorWindow.CloseWithoutSavingText(new AppSettings()));
        Assert.Contains("Закрепить", EditorWindow.CloseWithoutSavingText(new AppSettings()));
        Assert.DoesNotContain("истори", EditorWindow.CloseWithoutSavingText(FeatureGateTests.Without(FeatureCatalog.History)));
        Assert.Contains("закрепить", EditorWindow.CloseWithoutSavingText(FeatureGateTests.Without(FeatureCatalog.History)));
        Assert.DoesNotContain("закрепить", EditorWindow.CloseWithoutSavingText(FeatureGateTests.Without(FeatureCatalog.History, FeatureCatalog.Pins)));
        Assert.DoesNotContain("Закрепить", EditorWindow.CloseWithoutSavingText(FeatureGateTests.Without(FeatureCatalog.Pins)));
    }
}
