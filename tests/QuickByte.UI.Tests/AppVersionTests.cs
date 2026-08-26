using System.Reflection;

namespace QuickByte.UI.Tests;

/// <summary>
/// The version the title bar and the About box show.
///
/// No window is allowed to hardcode one: the numbers are set in
/// Directory.Build.props, the compiler stamps them into the assembly, and this
/// reads them back. What the tests pin is the reading — in particular that the
/// build metadata SourceLink appends survives nowhere near a title bar.
/// </summary>
public sealed class AppVersionTests
{
    /// <summary>The assembly AppVersion reads, which is the app's, not this one's.</summary>
    private static readonly Assembly App = typeof(AppVersion).Assembly;

    [Fact]
    public void The_displayed_version_is_the_informational_one_without_its_build_metadata()
    {
        string informational = App.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        string expected = informational.Split('+')[0];

        Assert.Equal(expected, AppVersion.Display);
    }

    [Fact]
    public void A_commit_sha_never_reaches_a_title_bar()
    {
        // The SDK appends "+<sha>" to InformationalVersion. Useful in a log,
        // noise in a window title -- and this build has one, so the assertion
        // below is about something that is actually there.
        string informational = App.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Contains("+", informational);
        Assert.DoesNotContain("+", AppVersion.Display);
    }

    [Fact]
    public void The_displayed_version_is_the_three_part_number_a_person_would_say()
    {
        string[] parts = AppVersion.Display.Split('-')[0].Split('.');

        Assert.Equal(3, parts.Length);
        Assert.All(parts, part => Assert.True(int.TryParse(part, out _), $"'{part}' is not a number"));
    }

    [Fact]
    public void The_file_version_is_the_four_part_one_from_the_exes_properties_page()
    {
        // This is the string a user reads off the file when reporting a bug, so
        // it carries the build number a CI run stamped in.
        string file = AppVersion.File;

        Assert.Equal(4, file.Split('.').Length);
        Assert.Equal(App.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version, file);
    }

    [Fact]
    public void The_file_version_agrees_with_the_displayed_one()
    {
        // Both come from Directory.Build.props, so the first three parts match
        // -- a pre-release suffix aside, which only Display carries.
        string display = AppVersion.Display.Split('-')[0];

        Assert.StartsWith(display + ".", AppVersion.File);
    }

    [Fact]
    public void The_copyright_line_is_stamped_in_rather_than_written_in_a_form()
    {
        Assert.Equal(App.GetCustomAttribute<AssemblyCopyrightAttribute>()!.Copyright, AppVersion.Copyright);
        Assert.Contains("QuickByte", AppVersion.Copyright);
    }
}
