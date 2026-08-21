namespace QuickByte.Core.Helpers;

/// <summary>Filename derivation and collision-avoidance utilities.</summary>
public static class FileNameHelper
{
    public static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "download" : name;
    }

    public static string FileNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            string name = Path.GetFileName(uri.LocalPath);
            return string.IsNullOrWhiteSpace(name) ? "download" : SanitizeFileName(Uri.UnescapeDataString(name));
        }
        catch
        {
            return "download";
        }
    }

    /// <summary>Appends " (1)", " (2)"... to avoid overwriting an existing file.</summary>
    public static string GetAvailableFilePath(string folder, string fileName)
    {
        string fullPath = Path.Combine(folder, fileName);
        if (!File.Exists(fullPath)) return fullPath;

        string nameOnly = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);

        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(folder, $"{nameOnly} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
