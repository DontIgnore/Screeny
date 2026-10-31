using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Screeny.Localization;

/// <summary>
/// Language list for settings and first-run wizard: “As system (…)” and languages, each in itself.
/// First item label depends on UI language — window calls <see cref="Relabel"/> on <see cref="Loc.Changed"/>.
/// </summary>
public static class LanguagePicker
{
    /// <summary>
    /// Content of “as system” item: label changes in place (template bound to <see cref="Text"/>), so in both
    /// list and combo it updates without re-selecting item — and without <c>SelectionChanged</c>.
    /// </summary>
    public sealed class AutoItem : INotifyPropertyChanged
    {
        public string Text { get; private set; } = AutoLabel();

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Refresh()
        {
            Text = AutoLabel();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }

        public override string ToString() => Text;
    }

    private static DataTemplate CreateAutoTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(AutoItem.Text)));
        var template = new DataTemplate(typeof(AutoItem)) { VisualTree = text };
        return template;
    }

    /// <summary>Fills list and selects <paramref name="setting"/> (unknown → “as system”).</summary>
    public static void Fill(ComboBox box, string? setting)
    {
        box.Items.Clear();
        box.Items.Add(new ComboBoxItem { Tag = Loc.Auto, Content = new AutoItem(), ContentTemplate = CreateAutoTemplate() });
        foreach (var code in Loc.Supported) box.Items.Add(new ComboBoxItem { Tag = code, Content = Loc.NativeName(code) });
        Select(box, setting);
    }

    public static void Select(ComboBox box, string? setting)
    {
        var code = Loc.Canonical(setting) ?? Loc.Auto;
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == code);
    }

    /// <summary>Setting value by selected item; null — nothing selected.</summary>
    public static string? SelectedOrNull(ComboBox box) => box.SelectedItem is ComboBoxItem { Tag: string code } ? code : null;

    /// <summary>Setting value by selected item.</summary>
    public static string Selected(ComboBox box) => SelectedOrNull(box) ?? Loc.Auto;

    /// <summary>New “as system” label in current language. Does not touch selection.</summary>
    public static void Relabel(ComboBox box)
    {
        if (box.Items.Count > 0 && box.Items[0] is ComboBoxItem { Content: AutoItem auto }) auto.Refresh();
    }

    /// <summary>“As system” item label (for tests and accessibility).</summary>
    public static string AutoText(ComboBox box) => box.Items[0] is ComboBoxItem { Content: AutoItem auto } ? auto.Text : "";

    /// <summary>“As system (Russian)”: which language <see cref="Loc.Auto"/> would pick on this machine.</summary>
    public static string AutoLabel() => Loc.F("Language.System", Loc.NativeName(Loc.Resolve(Loc.Auto, Loc.SystemCulture)));
}
