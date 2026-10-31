using System.Text.Json;
using Screeny.Logging;

namespace Screeny.Updates;

/// <summary>
/// Update state outside settings: version user skipped (“Skip this version”).
/// Separate small file not to add field to <c>settings.json</c>.
/// </summary>
public sealed class UpdateState
{
    public string? SkippedVersion { get; set; }
}

public sealed class UpdateStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string FilePath { get; }

    public UpdateStateStore(string filePath) => FilePath = filePath;

    /// <summary><c>%LocalAppData%\Screeny\update-state.json</c>; test instance — <c>update-state.dev.json</c>.</summary>
    public static UpdateStateStore Default(bool dev) => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screeny",
        dev ? "update-state.dev.json" : "update-state.json"));

    /// <summary>Never throws: no file or corrupted — empty state.</summary>
    public UpdateState Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new UpdateState();
            return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(FilePath), Options) ?? new UpdateState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error($"Update state read failed: {FilePath}", ex);
            return new UpdateState();
        }
    }

    /// <summary>false — write failed (reason in log).</summary>
    public bool Save(UpdateState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Update state save failed: {FilePath}", ex);
            return false;
        }
    }
}
