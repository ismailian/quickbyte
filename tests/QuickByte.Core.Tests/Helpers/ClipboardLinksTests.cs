using QuickByte.Core.Helpers;
using QuickByte.Core.Models;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// What a copied piece of text has to be before it is offered as a download.
///
/// The clipboard carries passwords, messages and whole documents, so the rule is
/// narrow on purpose: one link, nothing else, to a file type on the list. Most of
/// these tests are about everything that must <em>not</em> be offered.
/// </summary>
public sealed class ClipboardLinksTests
{
    private static readonly IReadOnlySet<string> Types = ClipboardLinks.ParseFileTypes("zip iso exe");

    [Theory]
    [InlineData("https://example.com/files/setup.exe")]
    [InlineData("  https://example.com/files/archive.ZIP  ")]
    [InlineData("ftp://mirror.example.org/pub/disk.iso")]
    [InlineData("https://example.com/get/archive.zip?token=abc#part")]
    [InlineData("https://example.com/my%20files/setup.exe")]
    public void A_single_link_to_a_listed_file_type_is_offered(string copied)
    {
        Assert.Equal(copied.Trim(), ClipboardLinks.FindDownloadLink(copied, Types));
    }

    [Theory]
    [InlineData("https://example.com/page.html")]
    [InlineData("https://example.com/download")]
    [InlineData("https://example.com/files/setup.exe and some words")]
    [InlineData("see https://example.com/setup.exe")]
    [InlineData("https://example.com/a.zip\nhttps://example.com/b.zip")]
    [InlineData("file:///C:/Windows/setup.exe")]
    [InlineData("javascript:alert('x.exe')")]
    [InlineData("my password is hunter2.exe")]
    [InlineData("setup.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not_looked_at_twice(string? copied)
    {
        Assert.Null(ClipboardLinks.FindDownloadLink(copied, Types));
    }

    [Fact]
    public void A_pasted_document_is_never_scanned_for_a_link()
    {
        string huge = "https://example.com/" + new string('a', ClipboardLinks.MaxLinkLength) + ".zip";

        Assert.Null(ClipboardLinks.FindDownloadLink(huge, Types));
    }

    [Fact]
    public void An_empty_list_offers_nothing()
    {
        Assert.Null(ClipboardLinks.FindDownloadLink("https://example.com/a.zip", ClipboardLinks.ParseFileTypes("")));
    }

    [Fact]
    public void The_list_takes_dots_stars_commas_and_any_case()
    {
        var types = ClipboardLinks.ParseFileTypes(" .ZIP, *.iso;exe  mp4 ");

        Assert.Equal(new[] { "exe", "iso", "mp4", "zip" }, types.Order());
    }

    [Fact]
    public void Entries_that_are_not_plain_extensions_are_dropped()
    {
        var types = ClipboardLinks.ParseFileTypes("zip ../evil exe* verylongextension tar.gz");

        Assert.Equal(new[] { "zip" }, types.Order());
    }

    [Fact]
    public void Watching_the_clipboard_is_off_until_the_user_turns_it_on()
    {
        var settings = new DownloadSettings();

        Assert.False(settings.MonitorClipboard);
        Assert.Contains("iso", ClipboardLinks.ParseFileTypes(settings.ClipboardFileTypes));
    }
}
