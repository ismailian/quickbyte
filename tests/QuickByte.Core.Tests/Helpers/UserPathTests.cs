using QuickByte.Core.Helpers;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// The <c>~\…</c> abbreviation Add Download's "Save to" box shows.
///
/// Two things here are not cosmetic. The prefix match has to respect folder
/// boundaries, or a path under <c>C:\Users\bobby</c> is displayed as though it
/// were under <c>C:\Users\bob</c>'s profile — a wrong answer about where a file
/// is going. And the round trip has to hold: the shortened text sits in an
/// editable box whose value becomes the download's save folder, so a <c>~</c>
/// that reaches <c>Directory.CreateDirectory</c> unexpanded creates a folder
/// literally named <c>~</c> beside the executable.
/// </summary>
public sealed class UserPathTests
{
    private static readonly string Profile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void A_folder_in_the_profile_is_shown_short()
    {
        string full = Path.Combine(Profile, "Downloads", "QuickByte");

        Assert.Equal(Path.Combine("~", "Downloads", "QuickByte"), UserPath.Shorten(full));
    }

    [Fact]
    public void The_profile_itself_is_just_the_tilde() =>
        Assert.Equal("~", UserPath.Shorten(Profile));

    [Fact]
    public void A_trailing_separator_does_not_defeat_the_match() =>
        Assert.Equal(Path.Combine("~", "Documents"),
            UserPath.Shorten(Path.Combine(Profile, "Documents") + Path.DirectorySeparatorChar));

    [Fact]
    public void The_match_is_case_insensitive_like_the_file_system() =>
        Assert.Equal(Path.Combine("~", "Music"),
            UserPath.Shorten(Path.Combine(Profile.ToUpperInvariant(), "Music")));

    [Fact]
    public void Another_users_profile_is_left_alone()
    {
        // The failure this guards: without the separator in the comparison,
        // C:\Users\bobby starts with C:\Users\bob and is abbreviated into a
        // folder that belongs to somebody else.
        string neighbour = Profile + "2";
        string full = Path.Combine(neighbour, "Downloads");

        Assert.Equal(full, UserPath.Shorten(full));
    }

    [Theory]
    [InlineData(@"D:\Media\ISOs")]
    [InlineData(@"E:\")]
    [InlineData(@"\\nas\share\downloads")]
    [InlineData(@"C:\Temp")]
    public void A_folder_the_user_deliberately_chose_is_shown_in_full(string path)
    {
        // Somewhere off the profile is somewhere the user picked on purpose, and
        // the whole path is the information.
        Assert.Equal(Path.TrimEndingDirectorySeparator(path), UserPath.Shorten(path));
    }

    [Fact]
    public void Expand_puts_the_profile_back()
    {
        Assert.Equal(Path.Combine(Profile, "Downloads", "QuickByte"),
            UserPath.Expand(Path.Combine("~", "Downloads", "QuickByte")));

        Assert.Equal(Profile, UserPath.Expand("~"));
    }

    [Fact]
    public void Expand_accepts_the_forward_slash_a_user_may_type() =>
        Assert.Equal(Path.Combine(Profile, "Documents"), UserPath.Expand("~/Documents"));

    [Fact]
    public void Expand_leaves_a_real_folder_that_starts_with_a_tilde_alone()
    {
        // "~work" is a legal folder name, and it is not an abbreviation. Only a
        // tilde at a folder boundary means the profile.
        Assert.Equal(@"D:\~work", UserPath.Expand(@"D:\~work"));
        Assert.Equal("~work", UserPath.Expand("~work"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Nothing_in_nothing_out(string? text)
    {
        // The box can be empty while the user is retyping it; neither direction
        // may throw on the way to the validation that reports it.
        Assert.Equal(text ?? string.Empty, UserPath.Shorten(text));
        Assert.Equal(text ?? string.Empty, UserPath.Expand(text));
    }

    [Theory]
    [InlineData("Downloads")]
    [InlineData("Documents")]
    [InlineData(@"Downloads\QuickByte")]
    public void Shorten_and_Expand_round_trip(string relative)
    {
        string full = Path.Combine(Profile, relative);

        Assert.Equal(full, UserPath.Expand(UserPath.Shorten(full)));
    }
}
