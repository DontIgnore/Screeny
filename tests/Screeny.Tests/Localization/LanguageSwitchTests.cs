using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Screeny.Features;
using Screeny.Localization;

namespace Screeny.Tests.Localization;

/// <summary>On-the-fly language switch: XAML and code bindings update, subscribers notified. Language restored.</summary>
[Collection(LanguageSwitchCollection.Name)]
public class LanguageSwitchTests : IDisposable
{
    /// <summary>After each test nobody stays subscribed to language change (windows/panels unsubscribe).</summary>
    public void Dispose() => Assert.Equal(0, Loc.ChangedSubscriberCount);

    private static void Sta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
            finally { Loc.Apply(TestCulture.Language); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    [Fact]
    public void Apply_ChangesStrings_NotifiesIndexer_AndRaisesChanged() => Sta(() =>
    {
        var props = new List<string?>();
        int changed = 0;
        PropertyChangedEventHandler onProp = (_, e) => props.Add(e.PropertyName);
        Action onChanged = () => changed++;
        Loc.Instance.PropertyChanged += onProp;
        Loc.Changed += onChanged;
        try
        {
            Loc.Apply("ja");
            Assert.Equal("ja", Loc.Culture.Name);
            Assert.Equal("キャンセル", Loc.T("Common.Cancel"));
            Assert.Equal("キャンセル", Loc.Instance["Common.Cancel"]);
            Loc.Apply("JA"); // same language — no events
            Assert.Equal(["Item[]"], props);
            Assert.Equal(1, changed);
        }
        finally
        {
            Loc.Instance.PropertyChanged -= onProp;
            Loc.Changed -= onChanged;
        }
    });

    [Fact]
    public void Bind_UpdatesElementBuiltInCode() => Sta(() =>
    {
        var text = new TextBlock();
        Loc.Bind(text, TextBlock.TextProperty, "Settings.Title");
        Loc.Apply("en");
        Assert.Equal("Settings", text.Text);
        Loc.Apply("es");
        Assert.Equal("Configuración", text.Text);
    });

    [Fact]
    public void MarkupExtension_WorksInAttributesSettersAndRuns() => Sta(() =>
    {
        const string xaml = """
            <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:l="clr-namespace:Screeny.Localization;assembly=Screeny">
              <StackPanel.Resources>
                <Style x:Key="Tip" TargetType="Button" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Setter Property="ToolTip" Value="{l:Loc Common.Copy}"/>
                </Style>
              </StackPanel.Resources>
              <Button Content="{l:Loc Common.Save}" Style="{StaticResource Tip}"/>
              <TextBlock><Run Text="{l:Loc Tray.Exit}"/></TextBlock>
            </StackPanel>
            """;
        Loc.Apply("en");
        var panel = (StackPanel)XamlReader.Parse(xaml);
        var button = (Button)panel.Children[0];
        var run = (System.Windows.Documents.Run)((TextBlock)panel.Children[1]).Inlines.FirstInline;
        Assert.Equal(("Save", "Copy", "Exit"), ((string)button.Content, (string)button.ToolTip, run.Text));
        Loc.Apply("ru");
        Assert.Equal(("Сохранить", "Копировать", "Выход"), ((string)button.Content, (string)button.ToolTip, run.Text));
        Loc.Apply("zh-Hans");
        Assert.Equal(("保存", "复制", "退出"), ((string)button.Content, (string)button.ToolTip, run.Text));
    });

    [Fact]
    public void FeatureCheckList_FollowsLanguage() => Sta(() =>
    {
        var panel = new StackPanel();
        _ = new FeatureCheckList(panel, []);
        Loc.Apply("en");
        Assert.Equal("Ruler", (string)panel.Children.OfType<CheckBox>().Last().Content);
        Loc.Apply("ja");
        Assert.Equal("定規", (string)panel.Children.OfType<CheckBox>().Last().Content);
        Assert.Equal(Loc.T("Feature.Ruler.Description"), panel.Children.OfType<TextBlock>().Last().Text);
    });

    [Fact]
    public void LanguagePicker_ListsSystemThenEachLanguageInItsOwnName() => Sta(() =>
    {
        var box = new ComboBox();
        LanguagePicker.Fill(box, "es");
        var items = box.Items.Cast<ComboBoxItem>().ToList();
        Assert.Equal([Loc.Auto, .. Loc.Supported], items.Select(i => (string)i.Tag));
        Assert.Equal(["English", "Русский", "Español", "简体中文", "日本語"], items.Skip(1).Select(i => (string)i.Content));
        Assert.Equal("es", LanguagePicker.Selected(box));

        Loc.Apply("en");
        LanguagePicker.Relabel(box);
        Assert.StartsWith("Same as Windows (", LanguagePicker.AutoText(box));
        Loc.Apply("ru");
        LanguagePicker.Relabel(box);
        Assert.StartsWith("Как в системе (", LanguagePicker.AutoText(box));
        Assert.Equal("es", LanguagePicker.Selected(box)); // label changed, selection not

        LanguagePicker.Select(box, "nonsense");
        Assert.Equal(Loc.Auto, LanguagePicker.Selected(box));
    });

    /// <summary>
    /// Review error: renaming “as system” item re-selected it, wizard got SelectionChanged and from language change
    /// handler applied “auto” — language rolled back. Now label changes in place, without selection events.
    /// </summary>
    [Fact]
    public void LanguagePicker_RelabelOnAuto_RaisesNoSelectionChanged() => Sta(() =>
    {
        var box = new ComboBox();
        LanguagePicker.Fill(box, Loc.Auto);
        var picked = new List<string>();
        box.SelectionChanged += (_, _) => picked.Add(LanguagePicker.Selected(box));
        void Relabel() => LanguagePicker.Relabel(box);
        Loc.Changed += Relabel; // like settings window or wizard
        try
        {
            Loc.Apply("ja");
            Assert.Equal("ja", Loc.Culture.Name); // not rolled back to “auto”
            Assert.StartsWith("システムと同じ（", LanguagePicker.AutoText(box));
            Loc.Apply("es");
            Assert.StartsWith("Igual que Windows (", LanguagePicker.AutoText(box));
            Assert.Empty(picked);
            Assert.Equal(Loc.Auto, LanguagePicker.Selected(box));
            // Combo shows same object as item: its label updated with it.
            Assert.Equal(LanguagePicker.AutoText(box), box.SelectionBoxItem?.ToString() ?? ((ComboBoxItem)box.SelectedItem).Content.ToString());
        }
        finally { Loc.Changed -= Relabel; }
    });

    [Fact]
    public void Toolbar_RepeatedLoaded_SubscribesOnce_AndUnloadedUnsubscribes() => Sta(() =>
    {
        var toolbar = new Screeny.Overlay.ToolbarControl();
        toolbar.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        toolbar.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.Equal(1, Loc.ChangedSubscriberCount);
        Loc.Apply("en");
        Assert.Equal("Copy", ((TextBlock)toolbar.FindName("CopyLabel")).Text);
        toolbar.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.Equal(0, Loc.ChangedSubscriberCount);
    });

    [Fact]
    public void BeautifySection_RepeatedLoaded_SubscribesOnce() => Sta(() =>
    {
        var section = new Screeny.Overlay.BeautifySection();
        section.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        section.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.Equal(1, Loc.ChangedSubscriberCount);
        section.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.Equal(0, Loc.ChangedSubscriberCount);
    });

    [Fact]
    public void HotkeyMessages_FollowTheCurrentLanguage()
    {
        try
        {
            var state = new Screeny.Hotkeys.HotkeyState(Screeny.Hotkeys.HotkeyBinding.Custom("Ctrl+Shift+X", true), Screeny.Hotkeys.HotkeyStatus.Busy, 1409);
            var report = new Screeny.Hotkeys.HotkeyReport([state], FallbackErrorCode: 1409);
            Loc.Apply("en");
            Assert.Equal("The shortcut Ctrl+Shift+X is already taken by another app.", state.Message);
            Assert.Contains("already taken", report.FallbackError);
            Loc.Apply("ru");
            Assert.Equal("Сочетание Ctrl+Shift+X уже занято другим приложением.", state.Message);
            Assert.Contains("занято", report.FallbackError);
        }
        finally { Loc.Apply(TestCulture.Language); }
    }

    [Fact]
    public void ListSeparator_IsIdeographicCommaForJapaneseAndChinese()
    {
        try
        {
            Loc.Apply("ja");
            Assert.Equal("、", Loc.ListSeparator);
            Loc.Apply("zh-Hans");
            Assert.Equal("、", Loc.ListSeparator);
            Loc.Apply("es");
            Assert.Equal(", ", Loc.ListSeparator);
        }
        finally { Loc.Apply(TestCulture.Language); }
    }

    [Fact]
    public void UiFont_AddsCjkFallbackForJapaneseAndChinese()
    {
        try
        {
            Loc.Apply("ja");
            Assert.Contains("Yu Gothic UI", Loc.UiFontFamily);
            Loc.Apply("zh-Hans");
            Assert.Contains("Microsoft YaHei UI", Loc.UiFontFamily);
            Loc.Apply("es");
            Assert.Equal("Segoe UI Variable Text, Segoe UI", Loc.UiFontFamily);
        }
        finally { Loc.Apply(TestCulture.Language); }
    }
}
