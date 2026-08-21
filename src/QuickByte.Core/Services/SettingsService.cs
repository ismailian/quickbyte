using System.Text.Json;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services;

/// <summary>
/// Loads/saves <see cref="DownloadSettings"/> as JSON under
/// %AppData%/QuickByte/settings.json and notifies subscribers on change so
/// every open window immediately reflects new defaults.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly string _settingsFilePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DownloadSettings Current { get; private set; } = new();
    public event EventHandler<DownloadSettings>? SettingsChanged;

    public SettingsService(string? appDataFolder = null)
    {
        string folder = appDataFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickByte");
        Directory.CreateDirectory(folder);
        _settingsFilePath = Path.Combine(folder, "settings.json");
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                string json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize<DownloadSettings>(json);
                if (loaded is not null) Current = loaded;
            }
        }
        catch
        {
            // Corrupt/unreadable settings file — fall back to defaults rather than crash startup.
            Current = new DownloadSettings();
        }

        Directory.CreateDirectory(Current.DefaultDownloadFolder);
        Directory.CreateDirectory(Current.TempFolder);
    }

    public void Save(DownloadSettings settings)
    {
        Current = settings;
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_settingsFilePath, json);
        SettingsChanged?.Invoke(this, Current);
    }
}
