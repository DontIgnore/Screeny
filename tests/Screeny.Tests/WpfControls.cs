namespace Screeny.Tests;

/// <summary>
/// Тесты, создающие контролы из XAML (панель, flyout'ы), идут по очереди: параллельная загрузка BAML из ресурсов
/// сборки в WPF не потокобезопасна (гонка в PackagePart.GetStream).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfControls
{
    public const string Name = "WPF controls";
}
