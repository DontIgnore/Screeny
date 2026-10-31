using System.Windows.Markup;

namespace Screeny.Localization;

/// <summary>
/// <c>Text="{l:Loc Settings.Title}"</c>: binding to <see cref="Loc"/> string that updates on language change.
/// Works in <c>Setter.Value</c>, templates and <c>Run.Text</c> — value provided by binding itself.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Bind(Key).ProvideValue(serviceProvider);
}
