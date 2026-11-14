using System.Text.Json;
using System.Text.Json.Serialization;
using WowWallPaper.Infrastructure;
using WowWallPaper.Models;

namespace WowWallPaper.Services;

public sealed class SettingsService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly object _gate = new();

    public string SettingsPath => AppPaths.SettingsFile;

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(AppPaths.SettingsFile))
                {
                    return new AppSettings();
                }

                var json = File.ReadAllText(AppPaths.SettingsFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
                settings.Normalize();
                return settings;
            }
            catch (Exception ex)
            {
                AppLog.Error("Settings could not be loaded; a fresh profile will be used.", ex);
                TryBackupCorruptFile();
                return new AppSettings();
            }
        }
    }

    public bool Save(AppSettings settings)
    {
        lock (_gate)
        {
            try
            {
                AppPaths.EnsureCreated();
                var json = JsonSerializer.Serialize(settings, _jsonOptions);
                var temporary = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(temporary, json);
                File.Move(temporary, AppPaths.SettingsFile, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Settings could not be saved.", ex);
                return false;
            }
        }
    }

    private static void TryBackupCorruptFile()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                return;
            }

            var backup = Path.Combine(
                AppPaths.DataDirectory,
                $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(AppPaths.SettingsFile, backup, overwrite: false);
        }
        catch
        {
            // Ignore; recovery must not fail because a backup could not be written.
        }
    }
}
