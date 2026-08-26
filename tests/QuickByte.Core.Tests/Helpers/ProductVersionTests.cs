using QuickByte.Core.Helpers;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// The comparison the update checker turns into "there is a new version".
/// Getting the pre-release ordering wrong is what would offer a beta user an
/// update to the build they are already running, every launch, forever.
/// </summary>
public sealed class ProductVersionTests
{
    [Theory]
    [InlineData("1.4.0", "1.3.0")]
    [InlineData("1.3.1", "1.3.0")]
    [InlineData("2.0.0", "1.9.9")]
    [InlineData("1.3.0.1", "1.3.0")]
    public void IsNewer_is_true_for_a_higher_number(string candidate, string current) =>
        Assert.True(ProductVersion.IsNewer(candidate, current));

    [Theory]
    [InlineData("1.3.0", "1.4.0")]
    [InlineData("1.3.0", "1.3.0")]
    [InlineData("1.2.9", "1.3.0")]
    public void IsNewer_is_false_for_the_same_or_a_lower_number(string candidate, string current) =>
        Assert.False(ProductVersion.IsNewer(candidate, current));

    [Theory]
    [InlineData("v1.4.0", "1.3.0")]
    [InlineData("V1.4.0", "1.3.0")]
    [InlineData("1.4.0", "v1.3.0")]
    public void IsNewer_tolerates_the_v_a_release_note_is_written_with(string candidate, string current) =>
        Assert.True(ProductVersion.IsNewer(candidate, current));

    [Fact]
    public void IsNewer_treats_a_missing_component_as_zero()
    {
        // "1.3" and "1.3.0" are two spellings of one release, which is what a
        // person hand-writing either into a manifest means by them.
        Assert.False(ProductVersion.IsNewer("1.3", "1.3.0"));
        Assert.False(ProductVersion.IsNewer("1.3.0", "1.3"));
        Assert.True(ProductVersion.IsNewer("1.4", "1.3.0"));
    }

    [Fact]
    public void IsNewer_offers_a_release_to_someone_on_its_pre_release() =>
        Assert.True(ProductVersion.IsNewer("1.4.0", "1.4.0-beta.1"));

    [Fact]
    public void IsNewer_never_offers_a_pre_release_of_a_version_already_installed()
    {
        // The one that matters: a beta build must not be offered an "update" to
        // itself on every launch.
        Assert.False(ProductVersion.IsNewer("1.4.0-beta.1", "1.4.0"));
        Assert.False(ProductVersion.IsNewer("1.4.0-beta.1", "1.4.0-beta.1"));
    }

    [Fact]
    public void IsNewer_still_compares_the_numbers_of_two_pre_releases() =>
        Assert.True(ProductVersion.IsNewer("1.5.0-beta.1", "1.4.0-beta.1"));

    [Fact]
    public void IsNewer_ignores_build_metadata() =>
        Assert.False(ProductVersion.IsNewer("1.3.0+build.99", "1.3.0"));

    [Theory]
    [InlineData(null, "1.3.0")]
    [InlineData("", "1.3.0")]
    [InlineData("   ", "1.3.0")]
    [InlineData("not a version", "1.3.0")]
    [InlineData("1.3.0", null)]
    [InlineData("1.3.0", "garbage")]
    public void IsNewer_answers_no_to_anything_unparseable(string? candidate, string? current)
    {
        // The alternative is prompting on every launch forever.
        Assert.False(ProductVersion.IsNewer(candidate, current));
    }

    [Fact]
    public void TryParse_splits_the_number_from_the_pre_release_label()
    {
        Assert.True(ProductVersion.TryParse("v1.4.0-beta.2+sha.abc", out var version, out string prerelease));

        Assert.Equal(new Version(1, 4, 0, 0), version);
        Assert.Equal("beta.2", prerelease);
    }

    [Fact]
    public void TryParse_pads_a_missing_component_to_zero_rather_than_minus_one()
    {
        // Version leaves an omitted component at -1, which would order "1.3"
        // before "1.3.0".
        Assert.True(ProductVersion.TryParse("1.3", out var version, out _));
        Assert.Equal(new Version(1, 3, 0, 0), version);
    }

    [Fact]
    public void TryParse_accepts_a_single_component()
    {
        Assert.True(ProductVersion.TryParse("2", out var version, out _));
        Assert.Equal(new Version(2, 0, 0, 0), version);
    }

    [Fact]
    public void TryParse_reports_failure_without_throwing()
    {
        Assert.False(ProductVersion.TryParse("nonsense", out var version, out string prerelease));
        Assert.Equal(new Version(0, 0), version);
        Assert.Equal(string.Empty, prerelease);
    }
}
