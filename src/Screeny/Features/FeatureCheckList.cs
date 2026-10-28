using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Screeny.Localization;

namespace Screeny.Features;

/// <summary>
/// Галочки функций (<see cref="FeatureCatalog"/>) для окна настроек и мастера первого запуска: название, под ним пояснение
/// стилем Caption — как у остальных галочек с подписью. Отмечены все, кроме выключенных; сохраняет окно, которое их показывает.
/// </summary>
public sealed class FeatureCheckList
{
    private readonly Dictionary<string, CheckBox> _boxes = new();
    private readonly Dictionary<string, bool> _initial = new();

    /// <summary>Любую галочку переключили (окно настроек прячет поля выключенной функции сразу).</summary>
    public event Action? Changed;

    /// <param name="disabled">Выключенные функции (<see cref="Settings.AppSettings.DisabledFeatures"/>).</param>
    public FeatureCheckList(Panel panel, IEnumerable<string> disabled)
    {
        var off = disabled.ToHashSet();
        foreach (var feature in FeatureCatalog.All)
        {
            var box = new CheckBox
            {
                IsChecked = !off.Contains(feature.Id),
                Margin = new Thickness(0, _boxes.Count == 0 ? 0 : 12, 0, 0),
            };
            Loc.Bind(box, ContentControl.ContentProperty, feature.TitleKey);
            Loc.Bind(box, AutomationProperties.HelpTextProperty, feature.DescriptionKey);
            box.Checked += (_, _) => Changed?.Invoke();
            box.Unchecked += (_, _) => Changed?.Invoke();
            var caption = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 17, Margin = new Thickness(28, 4, 0, 0) };
            Loc.Bind(caption, TextBlock.TextProperty, feature.DescriptionKey);
            caption.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
            panel.Children.Add(box);
            panel.Children.Add(caption);
            _boxes[feature.Id] = box;
            _initial[feature.Id] = box.IsChecked == true;
        }
    }

    public bool IsOn(string id) => _boxes[id].IsChecked == true;

    /// <summary>Вложенный блок под пояснением функции (точное распознавание под «Распознаванием текста»); видимостью
    /// управляет вызывающий — по <see cref="IsOn"/> и <see cref="Changed"/>.</summary>
    public void AddBelow(string id, UIElement element)
    {
        var panel = (Panel)_boxes[id].Parent;
        panel.Children.Insert(panel.Children.IndexOf(_boxes[id]) + 2, element);
    }

    /// <summary>
    /// Функции поменяли в другом окне (мастер, открытый из настроек): нетронутые здесь галочки принимают новое значение,
    /// переключённые остаются как есть; <see cref="Changes"/> дальше считается от новых настроек.
    /// </summary>
    public void Sync(IEnumerable<string> disabled)
    {
        var off = disabled.ToHashSet();
        foreach (var (id, box) in _boxes)
        {
            bool live = !off.Contains(id);
            if (box.IsChecked == _initial[id]) box.IsChecked = live;
            _initial[id] = live;
        }
    }

    /// <summary>
    /// Только переключённые галочки — для <see cref="FeatureGate.MergeDisabled"/> поверх текущих настроек. Так окно настроек
    /// и открытый из него мастер не затирают выбор друг друга: каждое меняет лишь то, что в нём трогали.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, bool>> Changes
        => _boxes.Select(kv => new KeyValuePair<string, bool>(kv.Key, kv.Value.IsChecked == true)).Where(c => c.Value != _initial[c.Key]).ToList();
}
