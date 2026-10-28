using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Screeny.Localization;

/// <summary>
/// Список языков для настроек и мастера первого запуска: «Как в системе (…)» и языки, каждый на нём самом.
/// Подпись первого пункта зависит от языка интерфейса — окно вызывает <see cref="Relabel"/> по <see cref="Loc.Changed"/>.
/// </summary>
public static class LanguagePicker
{
    /// <summary>
    /// Содержимое пункта «как в системе»: подпись меняется на месте (шаблон привязан к <see cref="Text"/>), поэтому и в
    /// списке, и в поле выбора она обновляется без повторного выбора пункта — и без <c>SelectionChanged</c>.
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

    /// <summary>Заполняет список и выбирает <paramref name="setting"/> (неизвестное значение — «как в системе»).</summary>
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

    /// <summary>Значение настройки по выбранному пункту; null — ничего не выбрано.</summary>
    public static string? SelectedOrNull(ComboBox box) => box.SelectedItem is ComboBoxItem { Tag: string code } ? code : null;

    /// <summary>Значение настройки по выбранному пункту.</summary>
    public static string Selected(ComboBox box) => SelectedOrNull(box) ?? Loc.Auto;

    /// <summary>Новая подпись «как в системе» на текущем языке. Выбор не трогает.</summary>
    public static void Relabel(ComboBox box)
    {
        if (box.Items.Count > 0 && box.Items[0] is ComboBoxItem { Content: AutoItem auto }) auto.Refresh();
    }

    /// <summary>Подпись пункта «как в системе» (для тестов и доступности).</summary>
    public static string AutoText(ComboBox box) => box.Items[0] is ComboBoxItem { Content: AutoItem auto } ? auto.Text : "";

    /// <summary>«Как в системе (Русский)»: какой язык выберет <see cref="Loc.Auto"/> на этом компьютере.</summary>
    public static string AutoLabel() => Loc.F("Language.System", Loc.NativeName(Loc.Resolve(Loc.Auto, Loc.SystemCulture)));
}
