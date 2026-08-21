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

    /// <summary>
    /// Every size, rate and percentage in the app is rendered with exactly two
    /// decimals. Trimming them ("4 MB", "4.5 MB", "4.53 MB") makes a value that
    /// updates several times a second change *width* as it changes, so the text
    /// jitters sideways in a list cell or beside a label. A fixed shape costs
    /// two characters and buys a column that stays still.
    /// </summary>
    private const string FixedDecimals = "0.00";

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        double value = bytes;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < Units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }
        return $"{value.ToString(FixedDecimals)} {Units[unitIndex]}";
    }

    public static string FormatSpeed(double bytesPerSecond) => $"{FormatBytes((long)bytesPerSecond)}/s";

    /// <summary>
    /// Renders a 0-100 percentage at the same fixed width as <see cref="FormatBytes"/>.
    /// Used by every progress surface — list cells, progress bars, detail labels —
    /// so a bar's label can't change width mid-animation.
    /// </summary>
    public static string FormatPercentage(double percentage) =>
        $"{Math.Clamp(percentage, 0, 100).ToString(FixedDecimals)}%";

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
