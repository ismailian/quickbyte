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
