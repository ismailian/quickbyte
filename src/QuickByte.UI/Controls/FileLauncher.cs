using System.Diagnostics;

namespace QuickByte.UI.Controls;

/// <summary>
/// Shell hand-offs (open a downloaded file, reveal it in Explorer) shared by
/// the main window, the details window and the completion window. Every call
/// is best-effort: a missing file or a shell association the user hasn't set
/// is not worth an error dialog in a download manager.
/// </summary>
public static class FileLauncher
{
    public static void OpenFile(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath)) return;
            Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Hands a downloaded installer to the shell. <c>UseShellExecute</c> is
    /// load-bearing rather than incidental here: it is what lets Windows raise
    /// the elevation prompt setup needs.
    ///
    /// Unlike the rest of this class this one reports whether it worked, because
    /// the caller closes QuickByte on the strength of it — and a user who
    /// dismisses the UAC prompt (a Win32Exception, not a silent no-op) must not
    /// be left with neither the old app nor the new one.
    /// </summary>
    public static bool RunInstaller(string installerPath)
    {
        try
        {
            if (!File.Exists(installerPath)) return false;
            using var process = Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Opens a web page in the default browser. Only <c>http</c> and <c>https</c>:
    /// the address comes from a captured <c>Referer</c> header, which is whatever
    /// the browser extension was sent, and handing the shell a <c>file:</c> or
    /// custom-protocol URL from there would run whatever it names.
    /// </summary>
    /// <returns>Whether the page was handed to the shell.</returns>
    public static bool OpenWebPage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void RevealInExplorer(string fullPath, string fallbackFolder)
    {
        try
        {
            if (File.Exists(fullPath))
                Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
            else if (Directory.Exists(fallbackFolder))
                Process.Start("explorer.exe", $"\"{fallbackFolder}\"");
        }
        catch { /* best-effort — folder may not exist yet */ }
    }
}
