using System.Text.Json.Nodes;
using Screeny.Features;
using Screeny.Settings;

namespace Screeny.Tests.Settings;

public class FeatureSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScreenyTests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    private AppSettings LoadJson(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, json);
        return new SettingsStore(FilePath).Load();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void Defaults_AllFeaturesOn()
    {
        var s = new AppSettings();
        Assert.All(FeatureCatalog.All, f => Assert.True(s.IsFeatureOn(f.Id)));
    }

    [Fact]
    public void OldFileWithoutField_AllFeaturesOn()
    {
        var s = LoadJson("""{ "SettingsVersion": 2, "HistoryLimit": 7 }""");
        Assert.Empty(s.DisabledFeatures);
        Assert.Equal(7, s.HistoryLimit);
    }

    [Fact]
    public void SetFeature_TogglesWithoutDuplicates()
    {
        var s = new AppSettings();
        s.SetFeature(FeatureCatalog.Ocr, false);
        s.SetFeature(FeatureCatalog.Ocr, false);
        Assert.Equal([FeatureCatalog.Ocr], s.DisabledFeatures);
        Assert.False(s.IsFeatureOn(FeatureCatalog.Ocr));
        s.SetFeature(FeatureCatalog.Ocr, true);
        Assert.Empty(s.DisabledFeatures);
    }

    [Fact]
    public void Normalize_TrimsLowercasesAndDedupes_KeepsUnknownIds()
    {
        var s = new AppSettings { DisabledFeatures = [" OCR", "ocr", "", "  ", "future-thing"] }.Normalize();
        Assert.Equal(["ocr", "future-thing"], s.DisabledFeatures);
    }

    [Theory]
    [InlineData("""{ "SettingsVersion": 2, "DisabledFeatures": "ocr", "HistoryLimit": 9 }""")]
    [InlineData("""{ "SettingsVersion": 2, "DisabledFeatures": null, "HistoryLimit": 9 }""")]
    [InlineData("""{ "SettingsVersion": 2, "DisabledFeatures": {}, "HistoryLimit": 9 }""")]
    public void Load_MalformedField_FallsBackToEmpty_KeepsOtherSettings(string json)
    {
        var s = LoadJson(json);
        Assert.Empty(s.DisabledFeatures);
        Assert.Equal(9, s.HistoryLimit); // broken field doesn't reset whole file
    }

    [Fact]
    public void Migrate_DropsNonStringEntries()
    {
        var (r, changed) = SettingsMigrator.Migrate(JsonNode.Parse("""{ "SettingsVersion": 2, "DisabledFeatures": ["ocr", 1, null, "pins"] }""")!.AsObject());
        Assert.True(changed);
        Assert.Equal("""["ocr","pins"]""", r["DisabledFeatures"]!.ToJsonString());
    }

    [Fact]
    public void Catalog_IdsAreUniqueLowercase()
    {
        var ids = FeatureCatalog.All.Select(f => f.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches("^[a-z0-9-]+$", id));
    }
}
