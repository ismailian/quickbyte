using QuickByte.Core.Helpers;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// The Zone.Identifier stream a finished download is tagged with.
///
/// SmartScreen, Protected View and PowerShell's execution policy all read it, and
/// a download handed over from Chrome used to arrive without it — fewer
/// protections than letting Chrome save the file. These pin what is written, and
/// that nothing in a captured header can rewrite it: the referrer is whatever the
/// extension was sent, and the stream is parsed line by line.
/// </summary>
public sealed class MarkOfTheWebTests
{
    [Fact]
    public void The_stream_says_internet_and_names_the_page_and_the_file()
    {
        string text = MarkOfTheWeb.Build("https://cdn.example.com/setup.exe", "https://example.com/downloads");

        Assert.Equal(
            "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://example.com/downloads\r\nHostUrl=https://cdn.example.com/setup.exe\r\n",
            text);
    }

    [Fact]
    public void A_login_in_either_address_is_never_written_down()
    {
        // The stream travels with the file wherever it is copied.
        string text = MarkOfTheWeb.Build("ftp://alice:hunter2@files.example.com/disk.iso", "https://bob:secret@example.com/");

        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("secret", text);
        Assert.Contains("HostUrl=ftp://files.example.com/disk.iso", text);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("chrome-extension://abcdef/popup.html")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void An_address_that_is_not_a_web_or_ftp_one_is_left_out(string? referrer)
    {
        string text = MarkOfTheWeb.Build("https://example.com/f.exe", referrer);

        Assert.DoesNotContain("ReferrerUrl", text);
        Assert.Contains("ZoneId=3", text);
    }

    [Fact]
    public void A_referrer_cannot_smuggle_in_a_zone_of_its_own()
    {
        // A captured header carrying a line break, trying to lower the zone the
        // file is judged by.
        string text = MarkOfTheWeb.Build("https://example.com/f.exe", "https://example.com/\r\nZoneId=0\r\n");

        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines, line => line.StartsWith("ZoneId=", StringComparison.Ordinal));
        Assert.Contains("ZoneId=3", lines);
    }

    [Fact]
    public void Unicode_in_an_address_is_escaped_rather_than_written_raw()
    {
        Assert.Equal("https://example.com/%D1%84%D0%B0%D0%B9%D0%BB.exe", MarkOfTheWeb.Sanitize("https://example.com/файл.exe"));
    }

    [Fact]
    public void A_finished_file_gets_the_stream_and_keeps_its_contents()
    {
        using var folder = new TempFolder();
        string file = folder.WriteFile("setup.exe", new byte[] { 0x4D, 0x5A, 0x90 });

        bool written = MarkOfTheWeb.Apply(file, "https://example.com/setup.exe", null);

        Assert.True(written);
        Assert.Contains("ZoneId=3", File.ReadAllText($"{file}:{MarkOfTheWeb.StreamName}"));
        Assert.Equal(new byte[] { 0x4D, 0x5A, 0x90 }, File.ReadAllBytes(file));
    }

    [Fact]
    public void A_file_that_is_not_there_is_not_created_to_hang_the_stream_on()
    {
        using var folder = new TempFolder();
        string file = folder.File("never-merged.exe");

        Assert.False(MarkOfTheWeb.Apply(file, "https://example.com/f.exe", null));
        Assert.False(File.Exists(file));
    }
}
