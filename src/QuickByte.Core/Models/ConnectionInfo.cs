using QuickByte.Core.Enums;

namespace QuickByte.Core.Models;

/// <summary>
/// Immutable snapshot of a single connection's state, used to keep the UI
/// (Download Details connections ListView) in sync without exposing the
/// live, mutable worker object to consumers on other threads.
/// </summary>
public sealed class ConnectionInfo
{
    public int ConnectionId { get; init; }
    public long RangeStart { get; init; }
    public long RangeEnd { get; init; }
    public long BytesDownloaded { get; init; }
    public ConnectionStatus Status { get; init; }
    public int RetryCount { get; init; }
    public string? LastError { get; init; }

    public long TotalBytes => RangeEnd - RangeStart + 1;
    public long RemainingBytes => Math.Max(0, TotalBytes - BytesDownloaded);
    public double ProgressPercentage => TotalBytes <= 0 ? 0 : (double)BytesDownloaded / TotalBytes * 100.0;
}
