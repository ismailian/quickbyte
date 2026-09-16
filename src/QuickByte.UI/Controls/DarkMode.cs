using System.Runtime.InteropServices;

namespace QuickByte.UI.Controls;

/// <summary>
/// The two parts of a dark window that WinForms has no way to reach: the title bar,
/// which Windows draws, and the scroll bars of native list and text controls.
///
/// The third file in the repository that talks to Windows directly, beside
/// <see cref="TaskbarProgress"/> and <see cref="ClipboardWatcher"/>, and for the
/// same reason as both: there is no managed equivalent. A dark window under a
/// white title bar, with white scroll bars down its lists, reads as broken rather
/// than dark. Everything here is cosmetic, so every call is best-effort — a
/// Windows that does not understand the request simply keeps its light chrome.
/// </summary>
internal static class DarkMode
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE; builds of Windows 10 before 20H1 used 19 for the same thing.</summary>
    private const int ImmersiveDarkMode = 20;
    private const int ImmersiveDarkModeBefore20H1 = 19;

    public static void UseDarkTitleBar(IntPtr window)
    {
        try
        {
            int enabled = 1;
            if (DwmSetWindowAttribute(window, ImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(window, ImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }
        catch { /* cosmetic */ }
    }

    /// <summary>The Explorer dark theme for a control's own parts — its scroll bars, chiefly.</summary>
    public static void UseDarkControls(IntPtr control)
    {
        try { SetWindowTheme(control, "DarkMode_Explorer", null); }
        catch { /* cosmetic */ }
    }

    /// <summary>
    /// A drop-down list. Its visual style ignores BackColor entirely, so without this
    /// every combo box is a white bar; the common file dialog's dark theme is the one
    /// Windows itself uses for them.
    /// </summary>
    public static void UseDarkComboBox(IntPtr comboBox)
    {
        try { SetWindowTheme(comboBox, "DarkMode_CFD", null); }
        catch { /* cosmetic */ }
    }

    /// <summary>
    /// A list view's column header is a window of its own, and the strip of it past
    /// the last column is painted by the header's theme rather than by anyone's
    /// owner-draw code — white, on a dark list, until the header is themed too.
    /// </summary>
    public static void UseDarkListViewHeader(IntPtr listView)
    {
        try
        {
            IntPtr header = SendMessage(listView, ListViewGetHeader, IntPtr.Zero, IntPtr.Zero);
            if (header != IntPtr.Zero) SetWindowTheme(header, "DarkMode_ItemsView", null);
        }
        catch { /* cosmetic */ }
    }

    private const int ListViewGetHeader = 0x101F; // LVM_GETHEADER

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string? subIdList);
}
