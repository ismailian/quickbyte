using QuickByte.Core.Helpers;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// Covers the one function every server-supplied name passes through on its way
/// into the download folder, plus the URL-derivation rules that decide what a
/// file is called when the server names none.
/// </summary>
public sealed class FileNameHelperTests
{
    // ------------------------------------------------------ SanitizeFileName --

    [Theory]
    [InlineData("../../autorun.inf", "autorun.inf")]
    [InlineData("..\\..\\windows\\system32\\evil.dll", "evil.dll")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("C:\\Users\\someone\\report.pdf", "report.pdf")]
    public void SanitizeFileName_strips_any_directory_part(string given, string expected) =>
        Assert.Equal(expected, FileNameHelper.SanitizeFileName(given));

    [Fact]
    public void SanitizeFileName_replaces_characters_windows_will_not_accept()
    {
        string sanitized = FileNameHelper.SanitizeFileName("in<va>lid:na|me?.txt");

        Assert.Equal("in_va_lid_na_me_.txt", sanitized);
        Assert.All(Path.GetInvalidFileNameChars(), c => Assert.DoesNotContain(c, sanitized));
    }

    [Fact]
    public void SanitizeFileName_unwraps_the_quotes_a_content_disposition_arrives_in() =>
        Assert.Equal("archive.zip", FileNameHelper.SanitizeFileName("  \"archive.zip\"  "));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    public void SanitizeFileName_falls_back_when_nothing_usable_is_left(string given) =>
        Assert.Equal("download", FileNameHelper.SanitizeFileName(given));

    [Theory]
    [InlineData("report.", "report")]
    [InlineData("report.txt.", "report.txt")]
    [InlineData("report ", "report")]
    public void SanitizeFileName_drops_trailing_dots_and_spaces(string given, string expected)
    {
        // Windows drops them itself when the file is created, so a name kept with
        // them stops matching the file that actually appears on disk — and every
        // later File.Exists on it.
        Assert.Equal(expected, FileNameHelper.SanitizeFileName(given));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("NUL.txt")]
    [InlineData("COM1")]
    [InlineData("LPT9.dat")]
    [InlineData("aux")]
    [InlineData("PRN.pdf")]
    public void SanitizeFileName_defuses_reserved_device_names(string given)
    {
        // Opening one of these succeeds and writes to the device rather than to a
        // file, so the download would appear to work and produce nothing.
        string sanitized = FileNameHelper.SanitizeFileName(given);

        Assert.NotEqual(given, sanitized);
        Assert.StartsWith("_", sanitized);
    }

    [Theory]
    [InlineData("CONTENTS.txt")]
    [InlineData("console.log")]
    [InlineData("COM10.bin")]
    [InlineData("auxiliary.dat")]
    public void SanitizeFileName_leaves_names_that_merely_start_like_a_device(string given) =>
        Assert.Equal(given, FileNameHelper.SanitizeFileName(given));

    [Fact]
    public void SanitizeFileName_caps_the_length_but_keeps_the_extension()
    {
        string sanitized = FileNameHelper.SanitizeFileName(new string('a', 400) + ".zip");

        // MAX_PATH is the whole path's budget, and the download folder plus a
        // " (2)" collision suffix come out of the same 260 characters.
        Assert.True(sanitized.Length <= 180, $"length was {sanitized.Length}");
        Assert.EndsWith(".zip", sanitized);
        Assert.StartsWith("aaaa", sanitized);
    }

    [Fact]
    public void SanitizeFileName_gives_up_on_an_extension_that_is_most_of_the_name()
    {
        // Not an extension at all — a query string that came through as part of a
        // path segment. Truncating the stem to nothing to preserve it helps nobody.
        string sanitized = FileNameHelper.SanitizeFileName("file." + new string('b', 400));

        Assert.True(sanitized.Length <= 180, $"length was {sanitized.Length}");
        Assert.StartsWith("file.bbbb", sanitized);
    }

    // ----------------------------------------------------- FileNameFromUrl --

    [Fact]
    public void FileNameFromUrl_takes_the_last_segment() =>
        Assert.Equal("setup.exe", FileNameHelper.FileNameFromUrl("https://example.com/files/setup.exe"));

    [Fact]
    public void FileNameFromUrl_ignores_query_and_fragment() =>
        Assert.Equal("setup.exe", FileNameHelper.FileNameFromUrl("https://example.com/setup.exe?token=abc&x=1#top"));

    [Fact]
    public void FileNameFromUrl_walks_back_past_a_segment_that_is_not_a_file()
    {
        // The SourceForge shape. Naming this "download" is what it did until 1.4.0.
        Assert.Equal(
            "7z2301-x64.exe",
            FileNameHelper.FileNameFromUrl("https://sourceforge.net/projects/sevenzip/files/7z2301-x64.exe/download"));
    }

    [Theory]
    [InlineData("https://example.com/v1.2/get", "get")]
    [InlineData("https://example.com/api/v2.0/fetch", "fetch")]
    public void FileNameFromUrl_does_not_mistake_a_version_number_for_a_file(string url, string expected)
    {
        // LooksLikeFileName is strict on purpose: 2-8 alphanumerics with at least
        // one letter, so "1.2" and "2.0" are not extensions.
        Assert.Equal(expected, FileNameHelper.FileNameFromUrl(url));
    }

    [Fact]
    public void FileNameFromUrl_decodes_percent_escapes() =>
        Assert.Equal("my report.pdf", FileNameHelper.FileNameFromUrl("https://example.com/my%20report.pdf"));

    [Fact]
    public void FileNameFromUrl_sanitizes_what_it_recovers()
    {
        // Percent-decoding can reintroduce a separator, so the traversal guard has
        // to run after it rather than before.
        Assert.Equal("passwd", FileNameHelper.FileNameFromUrl("https://example.com/a/..%2F..%2Fpasswd"));
    }

    [Theory]
    [InlineData("not a url at all")]
    [InlineData("")]
    [InlineData("https://example.com")]
    [InlineData("https://example.com/")]
    public void FileNameFromUrl_falls_back_rather_than_throwing(string url) =>
        Assert.Equal("download", FileNameHelper.FileNameFromUrl(url));

    // ------------------------------------------------------ EnsureExtension --

    [Theory]
    [InlineData("video/mp4", "download.mp4")]
    [InlineData("application/pdf", "download.pdf")]
    [InlineData("application/x-7z-compressed", "download.7z")]
    [InlineData("video/mp4; charset=binary", "download.mp4")]
    [InlineData("VIDEO/MP4", "download.mp4")]
    public void EnsureExtension_names_an_extensionless_file_from_the_content_type(string contentType, string expected) =>
        Assert.Equal(expected, FileNameHelper.EnsureExtension("download", contentType));

    [Fact]
    public void EnsureExtension_leaves_a_name_that_already_has_one()
    {
        // The server's idea of the type is not better than an explicit .mkv, and
        // overriding it would rename every file served as the wrong MIME type.
        Assert.Equal("movie.mkv", FileNameHelper.EnsureExtension("movie.mkv", "application/octet-stream"));
        Assert.Equal("movie.mkv", FileNameHelper.EnsureExtension("movie.mkv", "text/html"));
    }

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("application/some-thing-nobody-has-heard-of")]
    [InlineData("")]
    [InlineData(null)]
    public void EnsureExtension_leaves_the_name_alone_when_the_type_says_nothing(string? contentType) =>
        Assert.Equal("download", FileNameHelper.EnsureExtension("download", contentType));

    // ------------------------------------------------- GetAvailableFilePath --

    [Fact]
    public void GetAvailableFilePath_returns_the_plain_path_when_nothing_is_there()
    {
        using var folder = new TempFolder();

        Assert.Equal(Path.Combine(folder.Path, "a.bin"), FileNameHelper.GetAvailableFilePath(folder.Path, "a.bin"));
    }

    [Fact]
    public void GetAvailableFilePath_counts_up_past_every_collision()
    {
        using var folder = new TempFolder();
        File.WriteAllText(Path.Combine(folder.Path, "a.bin"), "1");
        File.WriteAllText(Path.Combine(folder.Path, "a (1).bin"), "2");

        Assert.Equal(Path.Combine(folder.Path, "a (2).bin"), FileNameHelper.GetAvailableFilePath(folder.Path, "a.bin"));
    }

    [Fact]
    public void GetAvailableFilePath_keeps_the_extension_on_the_end()
    {
        using var folder = new TempFolder();
        File.WriteAllText(Path.Combine(folder.Path, "archive.tar.gz"), "1");

        Assert.Equal(
            Path.Combine(folder.Path, "archive.tar (1).gz"),
            FileNameHelper.GetAvailableFilePath(folder.Path, "archive.tar.gz"));
    }
}
