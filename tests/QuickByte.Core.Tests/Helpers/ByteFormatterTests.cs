using System.Globalization;
using QuickByte.Core.Helpers;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// Formatting is not cosmetic here: every one of these strings updates several
/// times a second in a list cell, and the fixed two decimals exist so the text
/// does not change width — and therefore jitter sideways — as the number moves.
/// </summary>
public sealed class ByteFormatterTests
{
    public ByteFormatterTests()
    {
        // The formatter renders with the current culture, which is right for the
        // UI and unhelpful for an assertion. Pinned so the expected strings mean
        // the same thing on a machine whose decimal separator is a comma.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    [Theory]
    [InlineData(0, "0.00 B")]
    [InlineData(1, "1.00 B")]
    [InlineData(1023, "1023.00 B")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(1024L * 1024, "1.00 MB")]
    [InlineData(1024L * 1024 * 1024, "1.00 GB")]
    [InlineData(1024L * 1024 * 1024 * 1024, "1.00 TB")]
    public void FormatBytes_scales_by_1024_and_always_shows_two_decimals(long bytes, string expected) =>
        Assert.Equal(expected, ByteFormatter.FormatBytes(bytes));

    [Fact]
    public void FormatBytes_stops_at_the_largest_unit_it_knows()
    {
        // Nothing above TB, so a petabyte-scale number stays in TB rather than
        // running off the end of the unit table.
        Assert.EndsWith(" TB", ByteFormatter.FormatBytes(long.MaxValue));
    }

    [Fact]
    public void FormatBytes_clamps_a_negative_count_to_zero() =>
        Assert.Equal("0.00 B", ByteFormatter.FormatBytes(-500));

    [Fact]
    public void FormatBytes_output_is_the_same_width_across_a_unit() =>
        Assert.Equal(ByteFormatter.FormatBytes(4 * 1024 * 1024).Length,
                     ByteFormatter.FormatBytes(4_530_000).Length);

    [Fact]
    public void FormatSpeed_is_a_size_per_second() =>
        Assert.Equal("1.00 MB/s", ByteFormatter.FormatSpeed(1024 * 1024));

    [Fact]
    public void FormatSpeed_of_nothing_is_zero() =>
        Assert.Equal("0.00 B/s", ByteFormatter.FormatSpeed(0));

    [Theory]
    [InlineData(0, "0.00%")]
    [InlineData(42.456, "42.46%")]
    [InlineData(100, "100.00%")]
    [InlineData(-5, "0.00%")]
    [InlineData(140, "100.00%")]
    public void FormatPercentage_clamps_to_the_bar_it_labels(double percentage, string expected) =>
        Assert.Equal(expected, ByteFormatter.FormatPercentage(percentage));

    [Fact]
    public void FormatEta_of_nothing_known_is_a_dash() =>
        Assert.Equal("--", ByteFormatter.FormatEta(null));

    [Theory]
    [InlineData(-1)]
    [InlineData(-3600)]
    public void FormatEta_of_a_negative_estimate_is_a_dash(int seconds) =>
        Assert.Equal("--", ByteFormatter.FormatEta(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void FormatEta_of_the_maximum_is_a_dash() =>
        Assert.Equal("--", ByteFormatter.FormatEta(TimeSpan.MaxValue));

    [Fact]
    public void FormatEta_grows_a_unit_at_a_time()
    {
        Assert.Equal("45s", ByteFormatter.FormatEta(TimeSpan.FromSeconds(45)));
        Assert.Equal("2m 5s", ByteFormatter.FormatEta(TimeSpan.FromSeconds(125)));
        Assert.Equal("1h 1m 5s", ByteFormatter.FormatEta(TimeSpan.FromSeconds(3665)));
    }

    [Fact]
    public void FormatEta_rolls_days_up_into_hours()
    {
        // There is no day unit, so a two-day estimate reads as 48 hours rather
        // than starting again at zero.
        Assert.Equal("48h 0m 0s", ByteFormatter.FormatEta(TimeSpan.FromDays(2)));
    }

    [Fact]
    public void BytesPerKilobyte_matches_what_the_formatter_scales_by()
    {
        // The Options window converts a KB/s limit with this constant; if it
        // disagreed with the formatter, a limit of "100 KB/s" would display as
        // something else.
        Assert.Equal(1024, ByteFormatter.BytesPerKilobyte);
        Assert.Equal("1.00 KB", ByteFormatter.FormatBytes(ByteFormatter.BytesPerKilobyte));
    }
}
