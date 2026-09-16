using QuickByte.Core.Helpers;
using QuickByte.Core.Models;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// What the search box above the downloads list finds. People search a list they
/// half remember — a word from the name, the site it came from — in whatever order
/// the words occur to them.
/// </summary>
public sealed class DownloadSearchTests
{
    private static readonly DownloadItem Ubuntu = new()
    {
        FileName = "ubuntu-24.04-desktop-amd64.iso",
        Url = "https://releases.ubuntu.com/24.04/ubuntu-24.04-desktop-amd64.iso"
    };

    private static readonly DownloadItem Signed = new()
    {
        FileName = "report final.pdf",
        Url = "https://bucket.s3.amazonaws.com/docs/report%20final.pdf?X-Amz-Signature=deadbeef&X-Amz-Credential=secret"
    };

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_empty_search_finds_everything(string? query)
    {
        Assert.True(DownloadSearch.Matches(Ubuntu, query));
    }

    [Theory]
    [InlineData("ubuntu")]
    [InlineData("UBUNTU ISO")]
    [InlineData("iso ubuntu")]
    [InlineData("releases.ubuntu.com")]
    [InlineData("24.04")]
    public void Every_word_is_found_in_any_order_and_case(string query)
    {
        Assert.True(DownloadSearch.Matches(Ubuntu, query));
    }

    [Fact]
    public void A_word_that_is_not_there_rules_the_download_out()
    {
        Assert.False(DownloadSearch.Matches(Ubuntu, "ubuntu fedora"));
    }

    [Fact]
    public void An_escaped_address_is_searched_the_way_it_reads()
    {
        Assert.True(DownloadSearch.Matches(Signed, "docs/report final"));
        Assert.True(DownloadSearch.Matches(Signed, "amazonaws"));
    }

    [Fact]
    public void A_signed_link_s_query_string_is_not_searched()
    {
        // A page of random tokens that would match nearly anything.
        Assert.False(DownloadSearch.Matches(Signed, "Signature"));
        Assert.False(DownloadSearch.Matches(Signed, "deadbeef"));
    }

    [Fact]
    public void A_download_with_an_address_that_does_not_parse_is_still_found_by_name()
    {
        var odd = new DownloadItem { FileName = "thing.bin", Url = "not a url" };

        Assert.True(DownloadSearch.Matches(odd, "thing"));
        Assert.True(DownloadSearch.Matches(odd, "not"));
    }
}
