using System.Runtime.InteropServices;

namespace QuickByte.UI.Controls;

/// <summary>
/// Keeps Windows from falling asleep while downloads are running.
///
/// A queue woken at 03:00 by <see cref="QueueWakeRegistration"/> is only half a
/// feature if the machine goes back to sleep two minutes later — which is exactly
/// what Windows does after a wake nobody touched the keyboard for. The same idle
/// timer ends an ordinary overnight download an hour in.
///
/// This is the fourth file in the repository that talks to Windows directly,
/// beside <see cref="TaskbarProgress"/>, <see cref="ClipboardWatcher"/> and
/// <see cref="DarkMode"/>: there is no managed way to ask the system to stay
/// awake. It uses a power request rather than <c>SetThreadExecutionState</c>,
/// because a request carries a reason — <c>powercfg /requests</c> names QuickByte
/// and says why, which is the first thing anyone asks when a PC will not sleep —
/// and is not tied to the thread that made it. It only holds off <em>idle</em>
/// sleep: closing the lid, the power button and Start &gt; Sleep still work.
/// </summary>
public sealed class KeepAwake : IDisposable
{
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x1;
    private const int PowerRequestSystemRequired = 1;

    private IntPtr _request;
    private bool _held;

    /// <summary>Whether a request is currently held.</summary>
    public bool IsHeld => _held;

    /// <summary>Holds the machine awake while <paramref name="downloading"/>, and lets it sleep again when not. Cheap when nothing changes.</summary>
    public void Update(bool downloading)
    {
        if (downloading == _held) return;

        try
        {
            if (downloading)
            {
                if (_request == IntPtr.Zero) _request = Create();
                if (_request != IntPtr.Zero && PowerSetRequest(_request, PowerRequestSystemRequired)) _held = true;
            }
            else
            {
                if (_request != IntPtr.Zero) PowerClearRequest(_request, PowerRequestSystemRequired);
                _held = false;
            }
        }
        catch
        {
            // Best-effort: a machine that sleeps is the behaviour there was before.
        }
    }

    private static IntPtr Create()
    {
        var context = new ReasonContext
        {
            Version = PowerRequestContextVersion,
            Flags = PowerRequestContextSimpleString,
            SimpleReasonString = "QuickByte is downloading"
        };
        IntPtr handle = PowerCreateRequest(ref context);
        return handle == new IntPtr(-1) ? IntPtr.Zero : handle;
    }

    public void Dispose()
    {
        Update(false);
        if (_request != IntPtr.Zero)
        {
            CloseHandle(_request);
            _request = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(IntPtr powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(IntPtr powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
