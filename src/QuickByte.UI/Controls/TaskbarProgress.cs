using System.Runtime.InteropServices;
using System.Windows.Forms;
using QuickByte.Core.Enums;

namespace QuickByte.UI.Controls;

/// <summary>
/// The looks a taskbar button's progress indicator can wear. The values are the
/// shell's own <c>TBPFLAG</c> bits and are handed straight to
/// <c>ITaskbarList3::SetProgressState</c>.
/// </summary>
public enum TaskbarProgressState
{
    /// <summary>No indicator at all — the button paints as an ordinary one.</summary>
    None = 0x0,
    /// <summary>A marquee. Carries no value; use it when there is nothing to measure.</summary>
    Indeterminate = 0x1,
    /// <summary>The green bar.</summary>
    Normal = 0x2,
    /// <summary>The red bar.</summary>
    Error = 0x4,
    /// <summary>The yellow bar.</summary>
    Paused = 0x8,
}

/// <summary>
/// Paints a window's taskbar button with the download's progress — the bar
/// behind the icon that a browser, a copy dialog and every other download
/// manager show, so a download that is minimised or sitting behind the browser
/// can still be read at a glance.
///
/// This is the one place in the repository that talks to Windows directly, and
/// it is here because there is no managed alternative: the indicator is only
/// reachable through the shell's <c>ITaskbarList3</c>, and the moment it becomes
/// reachable is only announced through a message from
/// <c>RegisterWindowMessage</c>. All of it is confined to this file — a form
/// attaches one of these and then only ever calls <see cref="Report"/>.
///
/// Three things here are not obvious and all three are load-bearing:
///
/// <list type="bullet">
/// <item><description>
/// <b>The button does not exist when the window is created.</b> The shell builds
/// it after the window is first shown and announces it by broadcasting
/// <c>TaskbarButtonCreated</c>; every call made before that is silently dropped.
/// A download that is moving papers over this by reporting again 100 ms later,
/// but a paused, failed or queued one never sends a second report — its
/// indicator would simply never appear. So the last report is cached and
/// re-applied when the message arrives, which is also what restores the
/// indicator when Explorer restarts and re-broadcasts it.
/// </description></item>
/// <item><description>
/// <b>State before value.</b> <c>TBPF_NOPROGRESS</c> and
/// <c>TBPF_INDETERMINATE</c> discard the value, and writing a value while the
/// button is indeterminate turns it back into a determinate bar — so the value
/// is written only for the three states that have one.
/// </description></item>
/// <item><description>
/// <b>It is a decoration.</b> A shell that will not answer must not take the
/// window down with it, so every failure is swallowed and the object goes inert.
/// </description></item>
/// </list>
///
/// It subclasses the form's window rather than making the form override
/// <c>WndProc</c>, which keeps the one window message this needs out of a file
/// that otherwise contains no Windows at all.
/// </summary>
public sealed class TaskbarProgress : NativeWindow, IDisposable
{
    /// <summary>
    /// Progress is reported out of 1000 rather than 100: the button is wide
    /// enough that whole-percent steps read as jumps on a large file.
    /// </summary>
    private const ulong ProgressResolution = 1000;

    /// <summary>
    /// Broadcast to every top-level window once the shell has built its taskbar
    /// button. Zero if the message could not be registered, which needs no
    /// handling beyond never matching a real message.
    /// </summary>
    private static readonly int TaskbarButtonCreated = unchecked((int)RegisterWindowMessage("TaskbarButtonCreated"));

    // One shell object for the process — every method on it takes an HWND, so
    // there is nothing per-window about it. Touched only from the UI thread,
    // which is also the STA the app runs on (Program.Main is [STAThread]).
    private static ITaskbarList3 _shell;
    private static bool _shellUnavailable;

    private readonly Form _form;
    private TaskbarProgressState _state = TaskbarProgressState.None;
    private ulong _value;
    private bool _disposed;

    /// <summary>
    /// Starts driving <paramref name="form"/>'s taskbar button. Safe to call
    /// before the form is shown — the indicator is applied once the window, and
    /// then its button, exist.
    /// </summary>
    public static TaskbarProgress AttachTo(Form form) => new(form);

    private TaskbarProgress(Form form)
    {
        _form = form;
        form.HandleCreated += OnFormHandleCreated;
        form.HandleDestroyed += OnFormHandleDestroyed;
        form.VisibleChanged += OnFormVisibleChanged;
        form.Disposed += OnFormDisposed;

        if (form.IsHandleCreated) Attach();
    }

    /// <summary>
    /// Sets the indicator. A repeated identical report costs nothing, which
    /// matters because progress arrives ten times a second.
    /// </summary>
    public void Report(TaskbarProgressState state, double percentage)
    {
        ulong value = (ulong)Math.Round(Math.Clamp(percentage, 0, 100) * (ProgressResolution / 100));
        if (state == _state && value == _value) return;

        _state = state;
        _value = value;
        Apply();
    }

    /// <summary>Removes the indicator, leaving an ordinary taskbar button.</summary>
    public void Clear() => Report(TaskbarProgressState.None, 0);

    /// <summary>
    /// The indicator a download in <paramref name="status"/> should wear.
    ///
    /// Split out from <see cref="Report"/> so it can be tested: the mapping is
    /// the half of this that can be wrong without anything failing — an
    /// indicator that never clears, or a finished download still tinting its
    /// button red, is not an error anywhere.
    ///
    /// A size the server never gave leaves <paramref name="percentage"/>
    /// permanently zero, so those downloads get the marquee rather than a bar
    /// stuck at the left edge. Merging keeps the full bar for the reason the
    /// window's own bar does — every byte is already on disk, and rewinding to
    /// report the merge would be a lie.
    /// </summary>
    public static TaskbarProgressState StateFor(DownloadStatus status, bool sizeKnown, double percentage) => status switch
    {
        DownloadStatus.Failed => TaskbarProgressState.Error,
        DownloadStatus.Paused => TaskbarProgressState.Paused,
        DownloadStatus.Connecting when percentage <= 0 => TaskbarProgressState.Indeterminate,
        DownloadStatus.Connecting or DownloadStatus.Downloading or DownloadStatus.Merging =>
            sizeKnown ? TaskbarProgressState.Normal : TaskbarProgressState.Indeterminate,
        _ => TaskbarProgressState.None,
    };

    // -------------------------------------------------------------- Plumbing --

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        // Re-apply rather than apply: this arrives long after the first report,
        // and after an Explorer restart it arrives for a window whose download
        // may not have moved since.
        if (m.Msg == TaskbarButtonCreated && TaskbarButtonCreated != 0)
            Apply();
    }

    private void OnFormHandleCreated(object sender, EventArgs e) => Attach();

    private void OnFormHandleDestroyed(object sender, EventArgs e) => Detach();

    // Hiding a window takes its taskbar button away with it, and this app does
    // that wholesale every time MainForm.HideToTray runs. Re-assert on the way
    // back rather than trusting the shell to have remembered.
    private void OnFormVisibleChanged(object sender, EventArgs e)
    {
        if (_form.Visible) Apply();
    }

    private void OnFormDisposed(object sender, EventArgs e) => Dispose();

    private void Attach()
    {
        if (Handle != IntPtr.Zero) return;
        AssignHandle(_form.Handle);
        Apply();
    }

    private void Detach()
    {
        if (Handle != IntPtr.Zero) ReleaseHandle();
    }

    private void Apply()
    {
        if (Handle == IntPtr.Zero) return;

        var shell = Shell;
        if (shell is null) return;

        try
        {
            shell.SetProgressState(Handle, _state);
            if (_state is TaskbarProgressState.Normal or TaskbarProgressState.Paused or TaskbarProgressState.Error)
                shell.SetProgressValue(Handle, _value, ProgressResolution);
        }
        catch (COMException)
        {
            _shell = null;
            _shellUnavailable = true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _form.HandleCreated -= OnFormHandleCreated;
        _form.HandleDestroyed -= OnFormHandleDestroyed;
        _form.VisibleChanged -= OnFormVisibleChanged;
        _form.Disposed -= OnFormDisposed;

        if (Handle == IntPtr.Zero) return;

        _state = TaskbarProgressState.None;
        _value = 0;
        Apply();
        ReleaseHandle();
    }

    // -------------------------------------------------------- Shell interop --

    private static ITaskbarList3 Shell
    {
        get
        {
            if (_shell is not null || _shellUnavailable) return _shell;

            try
            {
                var shell = (ITaskbarList3)new TaskbarList();
                shell.HrInit();
                _shell = shell;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException or PlatformNotSupportedException)
            {
                _shellUnavailable = true;
            }

            return _shell;
        }
    }

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    /// <summary>
    /// The shell's TaskbarList coclass. <c>new</c> on a <c>ComImport</c> type is
    /// a <c>CoCreateInstance</c> of this CLSID.
    ///
    /// Not <c>sealed</c>, unlike everything else in this repository: a coclass
    /// declares none of the interfaces it actually implements, so the compiler
    /// rejects the cast to <see cref="ITaskbarList3"/> as provably impossible
    /// unless the type could still be derived from.
    /// </summary>
    [ComImport]
    [Guid("56fdf344-fd6d-11d0-958a-006097c9a090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList
    {
    }

    /// <summary>
    /// Only as much of <c>ITaskbarList3</c> as this app calls. The members above
    /// the two that matter are its inherited <c>ITaskbarList</c> and
    /// <c>ITaskbarList2</c> ones: a COM interface is a vtable, so they have to be
    /// declared, in order, for the two below them to land on the right slots.
    /// Everything after <c>SetProgressState</c> is left off — nothing calls it,
    /// and an undeclared tail costs nothing.
    /// </summary>
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, TaskbarProgressState state);
    }
}
