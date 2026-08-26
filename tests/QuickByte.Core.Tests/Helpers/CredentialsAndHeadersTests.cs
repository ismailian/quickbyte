using System.Net.Http;
using System.Text;
using QuickByte.Core.Helpers;
using QuickByte.Core.Models;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// Everything that decides how a request identifies itself to the server. The
/// probe and every one of up to 32 connections have to present exactly the same
/// thing, or the probe resolves a real file size and the connections fetch
/// copies of a login page.
/// </summary>
public sealed class UrlCredentialsTests
{
    [Fact]
    public void Extract_pulls_the_login_out_of_the_url()
    {
        var split = UrlCredentials.Extract("ftp://alice:hunter2@example.com/disk.iso");

        Assert.NotNull(split.Credentials);
        Assert.Equal("alice", split.Credentials!.UserName);
        Assert.Equal("hunter2", split.Credentials.Password);
    }

    [Fact]
    public void Extract_leaves_no_password_in_the_url_that_gets_persisted()
    {
        var split = UrlCredentials.Extract("ftp://alice:hunter2@example.com/disk.iso");

        // DownloadItem.Url is persisted, displayed, and put in tooltips. The
        // credential field is the only one that knows how to protect itself.
        Assert.DoesNotContain("hunter2", split.Url);
        Assert.DoesNotContain("alice", split.Url);
        Assert.Contains("example.com/disk.iso", split.Url);
    }

    [Fact]
    public void Extract_handles_a_user_with_no_password()
    {
        var split = UrlCredentials.Extract("ftp://alice@example.com/disk.iso");

        Assert.NotNull(split.Credentials);
        Assert.Equal("alice", split.Credentials!.UserName);
        Assert.Equal(string.Empty, split.Credentials.Password);
    }

    [Fact]
    public void Extract_decodes_percent_escapes_in_the_login()
    {
        var split = UrlCredentials.Extract("ftp://user%40corp:p%40ss@example.com/x.iso");

        Assert.Equal("user@corp", split.Credentials!.UserName);
        Assert.Equal("p@ss", split.Credentials.Password);
    }

    [Fact]
    public void Extract_keeps_a_password_containing_a_colon()
    {
        // Split on the first colon only — the rest belongs to the password.
        var split = UrlCredentials.Extract("ftp://alice:a:b:c@example.com/x.iso");

        Assert.Equal("a:b:c", split.Credentials!.Password);
    }

    [Theory]
    [InlineData("https://example.com/file.zip")]
    [InlineData("ftp://example.com/file.zip")]
    public void Extract_returns_the_url_untouched_when_there_is_no_login(string url)
    {
        var split = UrlCredentials.Extract(url);

        Assert.Null(split.Credentials);
        Assert.Equal(url, split.Url);
    }

    [Fact]
    public void Extract_leaves_something_that_is_not_a_url_alone()
    {
        var split = UrlCredentials.Extract("this is not a url");

        Assert.Null(split.Credentials);
        Assert.Equal("this is not a url", split.Url);
    }
}

public sealed class HttpRequestDecoratorTests
{
    [Fact]
    public void Apply_sends_basic_credentials_up_front()
    {
        // Not after a 401: every connection shares one static HttpClient, so
        // HttpClientHandler.Credentials is not available — and it saves a round
        // trip on each of up to 32 connections.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/f.bin");

        HttpRequestDecorator.Apply(request, new RequestOptions
        {
            Credentials = new DownloadCredentials { UserName = "alice", Password = "hunter2" }
        });

        Assert.NotNull(request.Headers.Authorization);
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:hunter2")),
            request.Headers.Authorization.Parameter);
    }

    [Fact]
    public void Apply_does_nothing_without_options()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/f.bin");

        HttpRequestDecorator.Apply(request, null);

        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public void Apply_ignores_empty_credentials()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/f.bin");

        HttpRequestDecorator.Apply(request, new RequestOptions { Credentials = new DownloadCredentials() });

        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public void Apply_carries_the_browser_headers_that_make_a_link_resolve()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/f.bin");

        HttpRequestDecorator.Apply(request, new RequestOptions
        {
            Headers = new Dictionary<string, string>
            {
                ["Cookie"] = "session=abc; theme=dark",
                ["Referer"] = "https://example.com/downloads",
                ["User-Agent"] = "Mozilla/5.0 (a browser)"
            }
        });

        Assert.Equal("session=abc; theme=dark", string.Join(string.Empty, request.Headers.GetValues("Cookie")));
        Assert.Equal("https://example.com/downloads", string.Join(string.Empty, request.Headers.GetValues("Referer")));
    }

    [Fact]
    public void Apply_never_lets_a_captured_range_header_through()
    {
        // This request owns its Range. A stale captured one would silently
        // truncate the segment.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/f.bin");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(100, 200);

        HttpRequestDecorator.Apply(request, new RequestOptions
        {
            Headers = new Dictionary<string, string> { ["Range"] = "bytes=0-5", ["range"] = "bytes=0-5" }
        });

        Assert.Equal("bytes=100-200", request.Headers.Range!.ToString());
    }

    [Fact]
    public void Apply_drops_a_captured_authorization_when_it_has_credentials_of_its_own()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/f.bin");

        HttpRequestDecorator.Apply(request, new RequestOptions
        {
            Credentials = new DownloadCredentials { UserName = "alice", Password = "hunter2" },
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer stale-token" }
        });

        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:hunter2")),
            request.Headers.Authorization!.Parameter);
        Assert.Single(request.Headers.GetValues("Authorization"));
    }

    [Fact]
    public void Apply_keeps_a_captured_authorization_when_there_is_nothing_to_replace_it_with()
    {
        // A bearer token the extension captured is the only credential this
        // download has; dropping it unconditionally would break the request.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/f.bin");

        HttpRequestDecorator.Apply(request, new RequestOptions
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer captured" }
        });

        Assert.Contains("Bearer captured", request.Headers.GetValues("Authorization"));
    }

    [Fact]
    public void Apply_does_not_validate_what_the_browser_was_happy_to_send()
    {
        // HttpClient's parsing is stricter than a browser's. A cookie it refuses
        // is still the reason the link resolves to a file.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/f.bin");

        HttpRequestDecorator.Apply(request, new RequestOptions
        {
            Headers = new Dictionary<string, string> { ["Cookie"] = "weird=\"quoted, value\"; a=b" }
        });

        Assert.True(request.Headers.Contains("Cookie"));
    }
}
