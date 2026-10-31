using System.Runtime.CompilerServices;
using Screeny.Localization;

namespace Screeny.Tests.Localization;

/// <summary>
/// Tests checking UI texts are written against Russian. String language fixed before first test,
/// so result independent of Windows language on machine. Tests that change language are in
/// <see cref="LanguageSwitchCollection"/> and restore it.
/// </summary>
internal static class TestCulture
{
    public const string Language = "ru";

    [ModuleInitializer]
    internal static void Pin() => Loc.Apply(Language);
}

/// <summary>Language-switching tests: don't run parallel with others.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageSwitchCollection
{
    public const string Name = "Language switch";
}
