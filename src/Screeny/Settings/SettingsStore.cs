using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Screeny.Logging;

namespace Screeny.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        // Keep shortcut labels and Cyrillic paths readable for humans and the installer, without Unicode escaping.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string FilePath { get; }

    public SettingsStore(string filePath) => FilePath = filePath;

    public static SettingsStore Default() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Screeny", "settings.json"));

    /// <summary>Test instance settings (<c>--dev</c>): separate file, working settings unchanged.</summary>
    public static SettingsStore Dev() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Screeny", "settings.dev.json"));

    /// <summary>Test instance default history folder — not next to user history.</summary>
    public static string DevHistoryFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screeny", "Dev", "History");

    /// <summary>
    /// Settings from file: first <see cref="SettingsMigrator"/> (old format file rewritten after read,
    /// old copy — <c>settings.json.v1.bak</c>), then <see cref="AppSettings.Normalize"/>. Never throws: inaccessible file — defaults,
    /// corrupted — also, but first copy saved next to it <c>settings.json.corrupt-yyyyMMdd-HHmmss</c>, so next Save doesn't destroy user data.
    ///
    /// </summary>
    public AppSettings Load()
    {
        string json;
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            json = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Settings read failed: {FilePath}", ex);
            return new AppSettings();
        }

        AppSettings settings;
        bool migrated;
        try
        {
            var node = JsonNode.Parse(json);
            if (node is null) return new AppSettings(); // literal null
            if (node is not JsonObject raw) throw new JsonException("Settings root is not an object");
            int fromVersion = SettingsMigrator.ReadVersion(raw);
            (raw, migrated) = SettingsMigrator.Migrate(raw);
            settings = (raw.Deserialize<AppSettings>(Options) ?? new AppSettings()).Normalize();
            if (migrated && fromVersion < AppSettings.CurrentVersion) BackupBeforeMigration(fromVersion);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            Log.Error($"Settings file is corrupt: {FilePath}", ex);
            BackupCorrupt();
            return new AppSettings();
        }

        // Old format file rewritten immediately: next launch reads new format.
        if (migrated)
        {
            try { Save(settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"Settings re-save after migration failed: {FilePath}", ex); }
        }
        return settings;
    }

    /// <summary>Copy of old format file before rewrite (<c>settings.json.v1.bak</c>); existing not touched.</summary>
    private void BackupBeforeMigration(int fromVersion)
    {
        var backup = $"{FilePath}.v{fromVersion}.bak";
        try { if (!File.Exists(backup)) File.Copy(FilePath, backup); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"Settings pre-migration backup failed: {backup}", ex); }
    }

    /// <summary>Atomic write: temp file next to, then replace. Mid-write failure leaves no empty settings.json.</summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"Settings temp cleanup failed: {tmp}", ex); }
        }
    }

    private void BackupCorrupt()
    {
        var backup = $"{FilePath}.corrupt-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        try { File.Copy(FilePath, backup, overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"Settings backup failed: {backup}", ex); }
    }
}
