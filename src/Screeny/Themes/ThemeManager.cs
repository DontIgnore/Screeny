using System.Windows;
using Microsoft.Win32;
using Screeny.Logging;

namespace Screeny.Themes;

public enum AppTheme { Dark, Light }

/// <summary>
/// Chooses palette (Dark.xaml / Light.xaml) by system “App mode” setting and switches on the fly.
/// Palette is first dictionary in app MergedDictionaries; styles reference its brushes via DynamicResource.
/// Debug: environment variable SCREENY_THEME=light|dark locks theme and disables system tracking.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static readonly Uri DarkUri = new("/Screeny;component/Themes/Dark.xaml", UriKind.Relative);
    private static readonly Uri LightUri = new("/Screeny;component/Themes/Light.xaml", UriKind.Relative);

    private static ResourceDictionary? _palette;
    private static bool _followingSystem;

    /// <summary>Current theme; default dark (its palette in App.xaml).</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static void Initialize(Application app)
    {
        _palette = app.Resources.MergedDictionaries.FirstOrDefault(d => d.Source?.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase) == true);
        var forced = ParseOverride(Environment.GetEnvironmentVariable("SCREENY_THEME"));
        Apply(app, forced ?? ReadSystemTheme());
        if (forced is null)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _followingSystem = true;
        }
    }

    public static void Shutdown()
    {
        if (!_followingSystem) return;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _followingSystem = false;
    }

    public static AppTheme? ParseOverride(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => AppTheme.Light,
        "dark" => AppTheme.Dark,
        _ => null,
    };

    /// <summary>AppsUseLightTheme: 1 — light, 0 or missing — dark.</summary>
    public static AppTheme FromRegistryValue(object? value) => value is int i && i != 0 ? AppTheme.Light : AppTheme.Dark;

    public static AppTheme ReadSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return FromRegistryValue(key?.GetValue("AppsUseLightTheme"));
        }
        catch (Exception ex)
        {
            Log.Error("Theme detection failed", ex);
            return AppTheme.Dark;
        }
    }

    /// <summary>Event from SystemEvents thread: switch in UI thread.</summary>
    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color)) return;
        var app = Application.Current;
        if (app is null || app.Dispatcher.HasShutdownStarted) return;
        app.Dispatcher.BeginInvoke(() => Apply(app, ReadSystemTheme()));
    }

    private static void Apply(Application app, AppTheme theme)
    {
        if (theme == Current && _palette is not null) return;
        try
        {
            var palette = new ResourceDictionary { Source = theme == AppTheme.Light ? LightUri : DarkUri };
            var merged = app.Resources.MergedDictionaries;
            int index = _palette is null ? -1 : merged.IndexOf(_palette);
            if (index >= 0) merged[index] = palette; else merged.Insert(0, palette);
            _palette = palette;
            Current = theme;
        }
        catch (Exception ex)
        {
            Log.Error($"Theme switch to {theme} failed", ex);
            return;
        }
        foreach (Window window in app.Windows)
            if (window is ChromeWindow chrome) chrome.ApplyNativeTheme();
    }
}
