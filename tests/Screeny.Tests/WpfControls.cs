namespace Screeny.Tests;

/// <summary>
/// Tests creating XAML controls (panel, flyouts) run serially: parallel BAML loading from assembly resources in WPF is not thread-safe (race in PackagePart.GetStream).
///
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfControls
{
    public const string Name = "WPF controls";
}
