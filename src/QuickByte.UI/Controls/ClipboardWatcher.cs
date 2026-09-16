using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QuickByte.UI.Controls;

/// <summary>
/// Tells the app when the clipboard changes, for "Watch the clipboard for
/// download links".
///
/// This is the second file in the repository that talks to Windows directly,
/// beside <see cref="TaskbarProgress"/>, and for the same kind of reason: there is
/// no managed way to be told the clipboard changed. The alternative is polling
/// <see cref="Clipboard"/> on a timer, and that is worse than it sounds — every
/// read opens the clipboard, and while it is open no other program can write to
/// it, which is how a "can't open clipboard" error ends up in someone's
/// spreadsheet. <c>AddClipboardFormatListener</c> costs nothing until something is
/// copied. All of it is confined here, and nothing is read: the event only says
/// that something changed, and the owner decides whether to look.
///
/// The listener is a message-only window, so it has no presence on screen or in
/// the taskbar, and it is registered only while the feature is switched on —
/// someone who never asked for it has nothing listening to their clipboard.
/// </summary>
public sealed class ClipboardWatcher : NativeWindow, IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private static readonly IntPtr HwndMessage = new(-3);

    /// <summary>
    /// A copy often arrives as several updates in a row, one per format, and the
    /// program that made it may still hold the clipboard open for the first. One
    /// short wait collapses the burst and lets go of the lock before anyone reads.
    /// </summary>
    private readonly System.Windows.Forms.Timer _settle = new() { Interval = 150 };

    private bool _listening;

    /// <summary>Raised on the UI thread, once a burst of changes has settled.</summary>
    public event EventHandler? Changed;

    public ClipboardWatcher()
    {
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public bool IsListening => _listening;

    /// <summary>Starts or stops listening. Best-effort: a refusal leaves the feature quietly off.</summary>
    public void SetListening(bool listen)
    {
        if (listen == _listening) return;

        try
        {
            if (listen)
            {
                if (Handle == IntPtr.Zero) CreateHandle(new CreateParams { Parent = HwndMessage });
                _listening = AddClipboardFormatListener(Handle);
            }
            else
            {
                if (Handle != IntPtr.Zero) RemoveClipboardFormatListener(Handle);
                _listening = false;
                _settle.Stop();
            }
        }
        catch
        {
            _listening = false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmClipboardUpdate && _listening)
        {
            _settle.Stop();
            _settle.Start();
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        SetListening(false);
        _settle.Dispose();
        if (Handle != IntPtr.Zero) DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
