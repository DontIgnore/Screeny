using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Screeny.Localization;

namespace Screeny.Features;

/// <summary>
/// Feature checkboxes (<see cref="FeatureCatalog"/>) for settings window and first-run wizard: name, description below
/// in Caption style — like other checkboxes with caption. All checked except disabled; saving window does it.
/// </summary>
public sealed class FeatureCheckList
{
    private readonly Dictionary<string, CheckBox> _boxes = new();
    private readonly Dictionary<string, bool> _initial = new();

    /// <summary>Any checkbox toggled (settings window hides disabled feature fields immediately).</summary>
    public event Action? Changed;

    /// <param name="disabled">Disabled features (<see cref="Settings.AppSettings.DisabledFeatures"/>).</param>
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

    /// <summary>Nested block under feature description (precise recognition under “Text recognition”); visibility
    /// controlled by caller — by <see cref="IsOn"/> and <see cref="Changed"/>.</summary>
    public void AddBelow(string id, UIElement element)
    {
        var panel = (Panel)_boxes[id].Parent;
        panel.Children.Insert(panel.Children.IndexOf(_boxes[id]) + 2, element);
    }

    /// <summary>
    /// Features changed in another window (wizard opened from settings): untouched checkboxes here take new value,
    /// toggled ones stay; <see cref="Changes"/> computed from new settings.
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
    /// Only toggled checkboxes — for <see cref="FeatureGate.MergeDisabled"/> over current settings. So settings window
    /// and wizard opened from it don't overwrite each other's choices: each changes only what was touched.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, bool>> Changes
        => _boxes.Select(kv => new KeyValuePair<string, bool>(kv.Key, kv.Value.IsChecked == true)).Where(c => c.Value != _initial[c.Key]).ToList();
}
