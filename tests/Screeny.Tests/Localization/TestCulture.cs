using System.Runtime.CompilerServices;
using Screeny.Localization;

namespace Screeny.Tests.Localization;

/// <summary>
/// Тесты, которые проверяют тексты интерфейса, написаны по русскому тексту. Язык строк фиксируется до первого теста,
/// чтобы результат не зависел от языка Windows на машине, где они запускаются. Тесты, которые меняют язык, — в
/// <see cref="LanguageSwitchCollection"/> и возвращают его обратно.
/// </summary>
internal static class TestCulture
{
    public const string Language = "ru";

    [ModuleInitializer]
    internal static void Pin() => Loc.Apply(Language);
}

/// <summary>Тесты, переключающие язык: идут не параллельно с остальными.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageSwitchCollection
{
    public const string Name = "Language switch";
}
