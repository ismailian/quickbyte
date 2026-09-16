using QuickByte.Core.Helpers;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// Turning pasted text into links for a batch. The traps are the ones a URL sets
/// for a naive parser: an IPv6 host looks like a range, a sentence ends a link
/// with a full stop, a page source escapes its ampersands, and a typo in a range
/// is a hundred million rows.
/// </summary>
public sealed class BatchLinksTests
{
    // ------------------------------------------------------------ patterns --

    [Fact]
    public void A_numbered_range_pads_to_the_width_of_its_first_number()
    {
        var links = BatchLinks.ExpandPattern("https://example.com/photo[008-011].jpg");

        Assert.Equal(new[]
        {
            "https://example.com/photo008.jpg", "https://example.com/photo009.jpg",
            "https://example.com/photo010.jpg", "https://example.com/photo011.jpg"
        }, links);
    }

    [Fact]
    public void A_range_without_a_leading_zero_is_not_padded()
    {
        Assert.Equal(new[] { "https://x/9.bin", "https://x/10.bin" }, BatchLinks.ExpandPattern("https://x/[9-10].bin"));
    }

    [Fact]
    public void A_letter_range_and_a_reversed_range_both_expand()
    {
        Assert.Equal(new[] { "https://x/disc-a.iso", "https://x/disc-b.iso", "https://x/disc-c.iso" },
            BatchLinks.ExpandPattern("https://x/disc-[a-c].iso"));
        Assert.Equal(new[] { "https://x/3", "https://x/2", "https://x/1" }, BatchLinks.ExpandPattern("https://x/[3-1]"));
    }

    [Fact]
    public void Several_ranges_in_one_address_multiply()
    {
        var links = BatchLinks.ExpandPattern("https://x/s[1-2]/e[01-03].mkv");

        Assert.Equal(6, links.Count);
        Assert.Equal("https://x/s1/e01.mkv", links[0]);
        Assert.Equal("https://x/s2/e03.mkv", links[^1]);
    }

    [Theory]
    [InlineData("http://[::1]/file.bin")]
    [InlineData("http://[2001:db8::1]:8080/file.bin")]
    [InlineData("https://x/[a-Z].bin")]
    public void Brackets_that_are_not_a_range_are_left_alone(string url)
    {
        Assert.Equal(new[] { url }, BatchLinks.ExpandPattern(url));
    }

    [Fact]
    public void A_pattern_that_stands_for_too_many_links_is_refused()
    {
        // One typo away from a hundred million rows.
        Assert.Throws<ArgumentException>(() => BatchLinks.ExpandPattern("https://x/[1-99999999].jpg"));
        Assert.Throws<ArgumentException>(() => BatchLinks.ExpandPattern("https://x/[1-1000]/[1-1000].jpg"));
    }

    // ------------------------------------------------------------ extraction --

    [Fact]
    public void Links_are_found_one_per_line_in_order_and_once_each()
    {
        var links = BatchLinks.ExtractLinks("""
            https://example.com/a.zip
              ftp://files.example.com/b.iso

            https://example.com/a.zip
            not a link
            """);

        Assert.Equal(new[] { "https://example.com/a.zip", "ftp://files.example.com/b.iso" }, links);
    }

    [Fact]
    public void Links_are_found_inside_a_page_source_with_their_ampersands_unescaped()
    {
        const string html = """<a href="https://example.com/get?id=1&amp;file=a.zip">A</a> <a href='https://example.com/b.zip'>B</a>""";

        Assert.Equal(new[] { "https://example.com/get?id=1&file=a.zip", "https://example.com/b.zip" },
            BatchLinks.ExtractLinks(html));
    }

    [Fact]
    public void The_punctuation_a_sentence_puts_after_a_link_is_not_part_of_it()
    {
        var links = BatchLinks.ExtractLinks(
            "Get https://example.com/a.zip, or (https://example.com/b.zip). Also https://example.com/wiki/Foo_(bar).");

        Assert.Equal(new[]
        {
            "https://example.com/a.zip", "https://example.com/b.zip", "https://example.com/wiki/Foo_(bar)"
        }, links);
    }

    [Fact]
    public void Only_web_and_ftp_links_are_taken()
    {
        var links = BatchLinks.ExtractLinks("javascript:alert(1) file:///C:/x.exe mailto:a@b.c https://ok.example.com/f");

        Assert.Equal(new[] { "https://ok.example.com/f" }, links);
    }

    [Fact]
    public void A_pasted_pattern_is_expanded_where_it_stands()
    {
        var links = BatchLinks.ExtractLinks("https://x/first.bin\nhttps://x/part[1-3].rar\nhttps://x/last.bin");

        Assert.Equal(new[]
        {
            "https://x/first.bin", "https://x/part1.rar", "https://x/part2.rar", "https://x/part3.rar", "https://x/last.bin"
        }, links);
    }

    [Fact]
    public void Nothing_to_find_is_an_empty_list_not_an_error()
    {
        Assert.Empty(BatchLinks.ExtractLinks(""));
        Assert.Empty(BatchLinks.ExtractLinks(null!));
    }

    [Fact]
    public void An_import_file_too_big_to_be_a_list_of_links_is_refused_unread()
    {
        using var folder = new TempFolder();
        string path = folder.File("huge.txt");
        using (var stream = File.Create(path)) stream.SetLength(BatchLinks.MaxImportBytes + 1);

        Assert.Throws<ArgumentException>(() => BatchLinks.ReadImportFile(path));
    }
}
