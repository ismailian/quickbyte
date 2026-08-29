namespace QuickByte.Core.Helpers;

/// <summary>
/// The folder QuickByte keeps its state in — <c>%AppData%\QuickByte</c>, home to
/// <c>settings.json</c>, <c>downloads.json</c>, <c>queues.json</c> and
/// <c>agent.log</c> — and the one sub-folder that belongs to the engine rather
/// than to the user: <c>temp\</c>, holding the chunk files a transfer is
/// assembled from.
///
/// That folder used to be under <c>%TEMP%</c>, which is the one directory on a
/// Windows machine that gets emptied on purpose — by the user, by Disk Cleanup,
/// by Storage Sense, and by every "PC cleaner" ever installed. Resume is driven
/// entirely by chunk length on disk, so a sweep that runs while a download is
/// paused does not lose a cache, it loses the download. Keeping the chunks with
/// the rest of QuickByte's own state puts them somewhere nothing else sweeps.
///
/// <see cref="Services.SettingsService"/> is what stamps this onto
/// <see cref="Models.DownloadSettings.TempFolder"/>; that property is derived
/// and never persisted, which is also what migrates an install that predates
/// the move instead of leaving its chunks where the cleaners are.
///
/// Note what is <em>not</em> here: where finished files land. That is
/// <see cref="Models.DownloadSettings.DefaultDownloadFolder"/>, an ordinary
/// setting with an ordinary default under the user's own Downloads folder, and
/// Add Download can override it per file.
/// </summary>
public static class AppPaths
{
    public const string TempFolderName = "temp";

    /// <summary><c>%AppData%\QuickByte</c> — settings.json, downloads.json, queues.json, agent.log.</summary>
    public static string Data { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickByte");

    public static string Temp => TempIn(Data);

    /// <summary>
    /// The chunk folder relative to an arbitrary data root. Tests hand
    /// <see cref="Services.SettingsService"/> a scratch directory, and the
    /// chunks have to follow it there rather than reaching into the real
    /// profile.
    /// </summary>
    public static string TempIn(string dataFolder) => Path.Combine(dataFolder, TempFolderName);
}
