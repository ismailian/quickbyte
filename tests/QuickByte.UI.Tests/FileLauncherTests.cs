using QuickByte.UI.Controls;

namespace QuickByte.UI.Tests;

/// <summary>
/// The shell hand-offs — open a downloaded file, reveal it in Explorer, run a
/// downloaded installer.
///
/// Only the paths that <em>don't</em> start anything are exercised, for the
/// obvious reason: a test that opened a file would launch whatever the user has
/// associated with it. That is the half worth pinning anyway. A download whose
/// file has been moved or deleted is ordinary, and it must not throw out of a
/// double-click handler — except for the installer, which is the one caller
/// that needs to be told, because the app closes itself on the strength of the
/// answer.
/// </summary>
public sealed class FileLauncherTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "QuickByte.UI.Tests", Guid.NewGuid().ToString("N"));

    /// <summary>A path inside a folder that is never created, so nothing can open it.</summary>
    private string Missing(string name) => Path.Combine(_folder, name);

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Opening_a_file_that_is_gone_does_nothing_at_all()
    {
        // The user deleted the download outside the app; the row is still there.
        FileLauncher.OpenFile(Missing("gone.zip"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Opening_a_path_that_is_not_one_does_nothing(string path)
    {
        FileLauncher.OpenFile(path);
    }

    [Fact]
    public void Revealing_something_that_is_gone_does_not_open_a_window()
    {
        // Neither the file nor its folder exists, so there is nothing to show --
        // opening the user's Documents folder instead would be worse than
        // nothing.
        FileLauncher.RevealInExplorer(Missing("gone.zip"), Missing("no-folder"));
    }

    [Fact]
    public void An_installer_that_is_not_there_reports_failure_rather_than_swallowing_it()
    {
        // This answer decides whether QuickByte exits to let setup replace its
        // files. Returning true here would leave the user with neither the old
        // app nor the new one.
        Assert.False(FileLauncher.RunInstaller(Missing("QuickByte-9.9.9-x64.exe")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Z:\nowhere\setup.exe")]
    public void A_nonsense_installer_path_is_a_false_not_an_exception(string path)
    {
        Assert.False(FileLauncher.RunInstaller(path));
    }
}
