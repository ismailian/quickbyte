using QuickByte.Core.Models;

namespace QuickByte.Core.Interfaces;

/// <summary>
/// Loads/saves <see cref="DownloadSettings"/> from disk and notifies
/// subscribers when they change, so all open windows immediately honor
/// new settings (e.g. a changed default connection count).
/// </summary>
public interface ISettingsService
{
    DownloadSettings Current { get; }
    event EventHandler<DownloadSettings>? SettingsChanged;

    void Load();
    void Save(DownloadSettings settings);
}
