using QuickByte.UI.Controls;

namespace QuickByte.UI.Tests;

/// <summary>
/// The clipboard listener's switch. Nothing here copies anything — a test run has
/// no business replacing whatever the user last copied — so what is checked is
/// the half that matters for privacy: it listens only while switched on, and
/// switching it off really stops it.
/// </summary>
public sealed class ClipboardWatcherTests
{
    [Fact]
    public void A_new_watcher_is_not_listening()
    {
        using var watcher = new ClipboardWatcher();

        Assert.False(watcher.IsListening);
    }

    [Fact]
    public void Listening_can_be_switched_on_and_off_again()
    {
        using var watcher = new ClipboardWatcher();

        watcher.SetListening(true);
        Assert.True(watcher.IsListening);

        watcher.SetListening(false);
        Assert.False(watcher.IsListening);
    }

    [Fact]
    public void Switching_it_on_twice_registers_once_and_off_once_is_enough()
    {
        using var watcher = new ClipboardWatcher();

        watcher.SetListening(true);
        watcher.SetListening(true);
        watcher.SetListening(false);

        Assert.False(watcher.IsListening);
    }

    [Fact]
    public void Disposing_a_listening_watcher_stops_it()
    {
        var watcher = new ClipboardWatcher();
        watcher.SetListening(true);

        watcher.Dispose();

        Assert.False(watcher.IsListening);
    }
}
