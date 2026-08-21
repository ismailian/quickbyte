using System.Text.Json;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services;

/// <summary>
/// Persists the download list as JSON under %AppData%/QuickByte/downloads.json
/// so the In Progress / Completed / Failed tabs survive app restarts.
/// </summary>
public sealed class DownloadRepository : IDownloadRepository
{
    private readonly string _dbFilePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _fileLock = new();

    public DownloadRepository(string? appDataFolder = null)
    {
        string folder = appDataFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickByte");
        Directory.CreateDirectory(folder);
        _dbFilePath = Path.Combine(folder, "downloads.json");
    }

    public List<DownloadItem> LoadAll()
    {
        lock (_fileLock)
        {
            try
            {
                if (!File.Exists(_dbFilePath)) return new List<DownloadItem>();
                string json = File.ReadAllText(_dbFilePath);
                return JsonSerializer.Deserialize<List<DownloadItem>>(json) ?? new List<DownloadItem>();
            }
            catch
            {
                return new List<DownloadItem>();
            }
        }
    }

    public void SaveAll(IEnumerable<DownloadItem> items)
    {
        lock (_fileLock)
        {
            string json = JsonSerializer.Serialize(items.ToList(), JsonOptions);
            File.WriteAllText(_dbFilePath, json);
        }
    }
}
