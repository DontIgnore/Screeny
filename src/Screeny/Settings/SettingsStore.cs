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
        // Файл читают люди и установщик: «Ctrl+Shift+X» и кириллица в путях — как есть, без + и А.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string FilePath { get; }

    public SettingsStore(string filePath) => FilePath = filePath;

    public static SettingsStore Default() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Screeny", "settings.json"));

    /// <summary>Настройки тестового экземпляра (<c>--dev</c>): отдельный файл, рабочие настройки не меняются.</summary>
    public static SettingsStore Dev() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Screeny", "settings.dev.json"));

    /// <summary>Папка истории тестового экземпляра по умолчанию — не рядом с историей пользователя.</summary>
    public static string DevHistoryFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screeny", "Dev", "History");

    /// <summary>
    /// Настройки из файла: сначала <see cref="SettingsMigrator"/> (файл старого формата после чтения переписывается,
    /// копия старого — <c>settings.json.v1.bak</c>), затем <see cref="AppSettings.Normalize"/>. Никогда не бросает: при недоступном
    /// файле — значения по умолчанию, при битом — тоже, но сначала рядом сохраняется его копия
    /// <c>settings.json.corrupt-yyyyMMdd-HHmmss</c>, чтобы следующий Save не уничтожил данные пользователя.
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
            if (node is null) return new AppSettings(); // литерал null
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

        // Файл старого формата переписываем сразу: следующий запуск читает уже новый формат.
        if (migrated)
        {
            try { Save(settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"Settings re-save after migration failed: {FilePath}", ex); }
        }
        return settings;
    }

    /// <summary>Копия файла старого формата перед перезаписью (<c>settings.json.v1.bak</c>); уже существующую не трогаем.</summary>
    private void BackupBeforeMigration(int fromVersion)
    {
        var backup = $"{FilePath}.v{fromVersion}.bak";
        try { if (!File.Exists(backup)) File.Copy(FilePath, backup); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"Settings pre-migration backup failed: {backup}", ex); }
    }

    /// <summary>Атомарная запись: временный файл рядом, затем замена. Сбой посреди записи не оставит пустой settings.json.</summary>
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
