using QuickByte.Core.Helpers;

namespace QuickByte.Core.Services.Ftp;

/// <summary>
/// Turns an <c>ftp://</c> URL into the two strings the protocol actually wants:
/// the path to hand to <c>RETR</c>, and a file name to save under.
/// </summary>
internal static class FtpUrl
{
    /// <summary>
    /// True for the schemes <see cref="FtpConnectionFactory"/> and
    /// <see cref="FtpFileInfoProvider"/> handle.
    /// </summary>
    public static bool IsFtp(Uri uri) =>
        uri.Scheme.Equals("ftp", StringComparison.OrdinalIgnoreCase) ||
        uri.Scheme.Equals("ftps", StringComparison.OrdinalIgnoreCase);

    public static bool IsFtp(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsFtp(uri);

    /// <summary>
    /// The path as the server expects to see it. <see cref="Uri.AbsolutePath"/>
    /// is percent-encoded — a file with a space in its name arrives as
    /// <c>%20</c>, and FTP has no such encoding, so it must be undone before the
    /// path goes on the wire or the server looks for a file whose name really
    /// does contain a percent sign.
    /// </summary>
    public static string PathOf(Uri uri)
    {
        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        return string.IsNullOrEmpty(path) ? "/" : path;
    }

    public static string FileNameOf(Uri uri)
    {
        string name = Path.GetFileName(PathOf(uri));
        return string.IsNullOrWhiteSpace(name) ? "download" : FileNameHelper.SanitizeFileName(name);
    }
}
