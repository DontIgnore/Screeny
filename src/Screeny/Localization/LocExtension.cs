using System.Windows.Markup;

namespace Screeny.Localization;

/// <summary>
/// <c>Text="{l:Loc Settings.Title}"</c>: привязка к строке <see cref="Loc"/>, которая обновляется при смене языка.
/// Работает и в <c>Setter.Value</c>, шаблонах и <c>Run.Text</c> — значение отдаёт сама привязка.
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
