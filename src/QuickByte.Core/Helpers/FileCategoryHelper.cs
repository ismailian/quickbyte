namespace QuickByte.Core.Helpers;

public enum FileCategory
{
    Compressed,
    Documents,
    Music,
    Programs,
    Video,
    Pictures,
    Other
}

/// <summary>
/// Classifies a file by extension into a coarse category, used purely for
/// UI grouping (the sidebar's "All Downloads" tree and per-row icons) —
/// it has no effect on download behavior.
/// </summary>
public static class FileCategoryHelper
{
    private static readonly Dictionary<string, FileCategory> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".zip"] = FileCategory.Compressed, [".rar"] = FileCategory.Compressed, [".7z"] = FileCategory.Compressed,
        [".tar"] = FileCategory.Compressed, [".gz"] = FileCategory.Compressed,

        [".pdf"] = FileCategory.Documents, [".doc"] = FileCategory.Documents, [".docx"] = FileCategory.Documents,
        [".xls"] = FileCategory.Documents, [".xlsx"] = FileCategory.Documents, [".ppt"] = FileCategory.Documents,
        [".pptx"] = FileCategory.Documents, [".txt"] = FileCategory.Documents, [".csv"] = FileCategory.Documents,

        [".mp3"] = FileCategory.Music, [".wav"] = FileCategory.Music, [".flac"] = FileCategory.Music,
        [".aac"] = FileCategory.Music, [".ogg"] = FileCategory.Music, [".m4a"] = FileCategory.Music,

        [".exe"] = FileCategory.Programs, [".msi"] = FileCategory.Programs, [".dmg"] = FileCategory.Programs,
        [".apk"] = FileCategory.Programs, [".appimage"] = FileCategory.Programs,

        [".mp4"] = FileCategory.Video, [".mkv"] = FileCategory.Video, [".avi"] = FileCategory.Video,
        [".mov"] = FileCategory.Video, [".wmv"] = FileCategory.Video, [".webm"] = FileCategory.Video,
        [".ogv"] = FileCategory.Video,

        [".jpg"] = FileCategory.Pictures, [".jpeg"] = FileCategory.Pictures, [".png"] = FileCategory.Pictures,
        [".gif"] = FileCategory.Pictures, [".bmp"] = FileCategory.Pictures, [".webp"] = FileCategory.Pictures
    };

    public static FileCategory GetCategory(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && ExtensionMap.TryGetValue(ext, out var category)
            ? category
            : FileCategory.Other;
    }
}
