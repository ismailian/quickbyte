using QuickByte.Core.Helpers;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// The User-Agent the engine presents, and the one thing that must never reach a
/// request header: a control character. The value is persisted in settings.json
/// and the whole point of it is to be sent unvalidated, so the check that stops a
/// line break from splitting the request is the only thing standing in the way.
/// </summary>
/// <remarks>
/// <see cref="AppUserAgent.Configure"/> writes process-wide state and the Core
/// suite runs its classes in parallel, so the tests that call it live here
/// together — xunit runs the tests within one class in sequence — and the class
/// restores the default afterwards. Everything that only needs to *read* a
/// User-Agent goes through the explicit overload of
/// <c>HttpRequestDecorator.Apply</c> instead, and touches none of this.
/// </remarks>
public sealed class AppUserAgentTests : IDisposable
{
    public void Dispose() => AppUserAgent.Configure(null);

    [Fact]
    public void A_browser_user_agent_is_kept_exactly_as_typed()
    {
        // Parses as a header value, but only just: HttpClient's own strict
        // parsing is not what decides this, servers are.
        const string chrome =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
            + "Chrome/120.0.0.0 Safari/537.36";

        Assert.Equal(chrome, AppUserAgent.Normalize(chrome));
    }

    [Fact]
    public void Surrounding_space_is_trimmed()
    {
        Assert.Equal("curl/8.4.0", AppUserAgent.Normalize("  curl/8.4.0\t "));
    }

    [Fact]
    public void A_trailing_newline_is_trimmed_rather_than_refused()
    {
        // Trim takes CR and LF with the rest of the whitespace, and what is left
        // is a perfectly good value — a pasted line is the ordinary way this
        // field gets filled in. Only a break *inside* the value is a refusal.
        Assert.Equal("curl/8.4.0", AppUserAgent.Normalize("curl/8.4.0\r\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Nothing_typed_is_nothing_chosen(string? blank)
    {
        Assert.Null(AppUserAgent.Normalize(blank));

        // And Options accepts it: blank is how the user asks for QuickByte's own.
        Assert.True(AppUserAgent.IsAcceptable(blank));
    }

    [Theory]
    [InlineData("Mozilla/5.0\r\nX-Injected: yes")]
    [InlineData("Mozilla/5.0\nX-Injected: yes")]
    [InlineData("Mozilla/\0 5.0")]
    [InlineData("Mozilla/5.0 \u001b[31m")]
    public void A_control_character_is_refused_outright(string injected)
    {
        // This is request splitting, and the value is written to the request
        // without validation — so it cannot be allowed to get this far.
        Assert.Null(AppUserAgent.Normalize(injected));
        Assert.False(AppUserAgent.IsAcceptable(injected));
    }

    [Fact]
    public void A_control_character_inside_an_otherwise_fine_value_is_still_refused()
    {
        // Not trimmed away and not stripped: refusing is what tells the user,
        // and a silently stripped header is one they would believe they had set.
        Assert.Null(AppUserAgent.Normalize("QuickByte\r\n/1.0"));
    }

    [Fact]
    public void The_length_cap_is_inclusive()
    {
        Assert.NotNull(AppUserAgent.Normalize(new string('a', AppUserAgent.MaxLength)));
        Assert.Null(AppUserAgent.Normalize(new string('a', AppUserAgent.MaxLength + 1)));
    }

    [Fact]
    public void The_cap_is_measured_after_trimming()
    {
        string padded = "  " + new string('a', AppUserAgent.MaxLength) + "  ";

        Assert.NotNull(AppUserAgent.Normalize(padded));
    }

    [Fact]
    public void Nothing_configured_is_QuickBytes_own()
    {
        Assert.Equal(AppUserAgent.Default, AppUserAgent.Current);
        Assert.False(AppUserAgent.IsCustom);
    }

    [Fact]
    public void Configure_takes_the_users_value()
    {
        AppUserAgent.Configure("  Wget/1.21.4  ");

        Assert.Equal("Wget/1.21.4", AppUserAgent.Current);
        Assert.True(AppUserAgent.IsCustom);
    }

    [Fact]
    public void Clearing_the_setting_puts_the_default_back()
    {
        // The bug this pins: a Configure that only wrote a non-empty value left
        // the old one in place for the rest of the process, so emptying the box
        // in Options did nothing until a restart.
        AppUserAgent.Configure("Wget/1.21.4");
        AppUserAgent.Configure(null);

        Assert.Equal(AppUserAgent.Default, AppUserAgent.Current);
        Assert.False(AppUserAgent.IsCustom);
    }

    [Fact]
    public void An_unusable_value_leaves_the_default_rather_than_sending_nothing()
    {
        // settings.json is a file the user can edit, and a request with no
        // User-Agent at all is the failure the setting exists to fix.
        AppUserAgent.Configure("Mozilla/5.0\r\nX-Injected: yes");

        Assert.Equal(AppUserAgent.Default, AppUserAgent.Current);
    }

    [Fact]
    public void The_default_is_what_the_shared_clients_carry()
    {
        // The clients' DefaultRequestHeaders are stamped with this constant and
        // never re-stamped — see AppUserAgent's remarks on why the live value is
        // written per request instead.
        Assert.Equal(AppUserAgent.Default, Core.Services.HttpConnectionFactory.UserAgent);
    }
}
