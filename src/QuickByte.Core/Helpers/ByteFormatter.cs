namespace QuickByte.Core.Helpers;

/// <summary>Formats byte counts, speeds and durations for display in the UI.</summary>
public static class ByteFormatter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    /// <summary>
    /// The 1 KB = 1024 B convention this class formats with, exposed so the
    /// windows that take a speed limit in KB/s convert it the same way the
    /// numbers they display are scaled.
    /// </summary>
    public const long BytesPerKilobyte = 1024;

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        double value = bytes;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < Units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }
        return unitIndex == 0 ? $"{value:0} {Units[unitIndex]}" : $"{value:0.##} {Units[unitIndex]}";
    }

    public static string FormatSpeed(double bytesPerSecond) => $"{FormatBytes((long)bytesPerSecond)}/s";

    public static string FormatEta(TimeSpan? eta)
    {
        if (eta is null) return "--";
        var t = eta.Value;
        if (t.TotalSeconds < 0 || t == TimeSpan.MaxValue) return "--";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }
}
