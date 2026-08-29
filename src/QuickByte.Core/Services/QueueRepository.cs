using System.Text.Json;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services;

/// <summary>
/// Stores the queue list as JSON under %AppData%/QuickByte/queues.json.
///
/// Two things separate it from <see cref="DownloadRepository"/>, and both come
/// from the same fact: a second process reads this file while the app writes it.
///
/// Writes go to a temporary file that is then moved over the real one, so a
/// reader either sees the whole previous file or the whole new one — never the
/// empty window a truncating write leaves behind, which the agent would read as
/// "this user has no scheduled queues" and act on.
///
/// Reads open the file with <see cref="FileShare.ReadWrite"/> and retry briefly,
/// because a share violation against a writer that holds the file for a
/// millisecond is not a reason to report no queues.
/// </summary>
public sealed class QueueRepository : IQueueRepository
{
    private const int ReadAttempts = 3;
    private const int ReadRetryDelayMilliseconds = 60;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _queuesFilePath;
    private readonly object _fileLock = new();

    public QueueRepository(string? appDataFolder = null)
    {
        string folder = appDataFolder ?? AppPaths.Data;
        Directory.CreateDirectory(folder);
        _queuesFilePath = Path.Combine(folder, "queues.json");
    }

    public List<DownloadQueue> LoadAll() =>
        TryLoadAll(out var queues) ? queues : new List<DownloadQueue>();

    public bool TryLoadAll(out List<DownloadQueue> queues)
    {
        lock (_fileLock)
        {
            queues = new List<DownloadQueue>();

            for (int attempt = 0; attempt < ReadAttempts; attempt++)
            {
                try
                {
                    if (!File.Exists(_queuesFilePath)) return true;

                    using var stream = new FileStream(
                        _queuesFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    queues = JsonSerializer.Deserialize<List<DownloadQueue>>(stream) ?? new List<DownloadQueue>();
                    return true;
                }
                catch (JsonException)
                {
                    // Malformed rather than busy. Retrying will not help, and a
                    // corrupt file must not stop the app from starting — the same
                    // call the download list makes.
                    queues = new List<DownloadQueue>();
                    return true;
                }
                catch (IOException)
                {
                    Thread.Sleep(ReadRetryDelayMilliseconds);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(ReadRetryDelayMilliseconds);
                }
            }

            return false;
        }
    }

    public void SaveAll(IEnumerable<DownloadQueue> queues)
    {
        lock (_fileLock)
        {
            string json = JsonSerializer.Serialize(queues.ToList(), JsonOptions);
            string temporaryPath = _queuesFilePath + ".tmp";

            File.WriteAllText(temporaryPath, json);

            // Move, not copy: this is the step that has to be atomic. File.Move
            // with overwrite replaces the directory entry in one operation, so a
            // reader is never looking at a half-written queue list.
            File.Move(temporaryPath, _queuesFilePath, overwrite: true);
        }
    }
}
