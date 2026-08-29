using System.Text.Json;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services;

/// <summary>
/// Loads/saves <see cref="DownloadSettings"/> as JSON under
/// %AppData%/QuickByte/settings.json and notifies subscribers on change so
/// every open window immediately reflects new defaults.
///
/// It is also the one place that decides where the chunk folder is.
/// <see cref="DownloadSettings.TempFolder"/> is not persisted; it is stamped
/// onto every settings object that passes through here as <c>temp\</c> beside
/// settings.json itself. See <see cref="AppPaths"/> for why it moved there, and
/// note that stamping on <see cref="Save"/> as well as <see cref="Load"/> is
/// what keeps it right: the Options dialog builds a <em>fresh</em>
/// <see cref="DownloadSettings"/> and no longer has a field for it.
///
/// <see cref="DownloadSettings.DefaultDownloadFolder"/> is left alone — that one
/// is a real setting, and where a user keeps their files is their business.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly string _dataFolder;
    private readonly string _settingsFilePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DownloadSettings Current { get; private set; } = new();
    public event EventHandler<DownloadSettings>? SettingsChanged;

    public SettingsService(string? appDataFolder = null)
    {
        _dataFolder = appDataFolder ?? AppPaths.Data;
        Directory.CreateDirectory(_dataFolder);
        _settingsFilePath = Path.Combine(_dataFolder, "settings.json");
        ApplyChunkFolder(Current);
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

        ApplyChunkFolder(Current);
        EnsureFoldersExist(Current);
    }

    public void Save(DownloadSettings settings)
    {
        ApplyChunkFolder(settings);
        Current = settings;
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_settingsFilePath, json);
        EnsureFoldersExist(settings);
        SettingsChanged?.Invoke(this, Current);
    }

    /// <summary>
    /// Points the chunk folder at this service's data folder. Tests hand the
    /// constructor a scratch directory precisely so the chunks follow it there
    /// instead of the real profile.
    /// </summary>
    private void ApplyChunkFolder(DownloadSettings settings) =>
        settings.TempFolder = AppPaths.TempIn(_dataFolder);

    /// <summary>
    /// Best-effort: a folder that cannot be created is a failed download later,
    /// with a message naming the path — not a reason to refuse to start or to
    /// lose the settings the user just saved.
    /// </summary>
    private static void EnsureFoldersExist(DownloadSettings settings)
    {
        try
        {
            Directory.CreateDirectory(settings.DefaultDownloadFolder);
            Directory.CreateDirectory(settings.TempFolder);
        }
        catch { /* best-effort */ }
    }
}
