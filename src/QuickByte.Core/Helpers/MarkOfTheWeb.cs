using System.Text;

namespace QuickByte.Core.Helpers;

/// <summary>
/// Tags a finished download as having come from the internet — the
/// <c>Zone.Identifier</c> alternate data stream Windows calls the Mark of the Web.
///
/// Every browser writes it, and a surprising amount of Windows security hangs
/// off it: SmartScreen checks an executable's reputation before running it,
/// Office opens a document in Protected View, PowerShell refuses an unsigned
/// script, and "Unblock" in a file's Properties exists only because of it. The
/// browser extension hands downloads over from Chrome, which means that before
/// this existed, clicking a link in Chrome produced a file with fewer protections
/// than letting Chrome download it would have. A download manager that launders
/// the mark away is a way around every one of those checks.
/// </summary>
/// <remarks>
/// <para>What goes in the stream follows what Chromium writes: the zone, the
/// referring page and the file's address, with any user name and password removed
/// from both. It is text that other programs parse as an INI section, and the
/// referrer is a header the browser extension was sent — so each URL is re-built
/// from a parsed <see cref="Uri"/>, restricted to the web and FTP schemes, and
/// refused outright if anything in it could start a new line.</para>
///
/// <para>Best-effort, like the rest of the engine's file handling: a FAT32 or
/// exFAT drive and many network shares have no alternate streams at all, and a
/// download that finished is not made to fail over a tag.</para>
/// </remarks>
public static class MarkOfTheWeb
{
    public const string StreamName = "Zone.Identifier";

    /// <summary>URLZONE_INTERNET. Deliberately fixed rather than mapped: a lower zone would mean fewer warnings, and being wrong in that direction is the expensive one.</summary>
    public const int InternetZone = 3;

    /// <summary>Tags <paramref name="filePath"/>. Returns whether the stream was written.</summary>
    public static bool Apply(string filePath, string? hostUrl, string? referrerUrl)
    {
        // Never create the file: a stream path opened for writing on a file that
        // is not there makes an empty one to hang the stream on.
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;

        try
        {
            File.WriteAllText($"{filePath}:{StreamName}", Build(hostUrl, referrerUrl), new UTF8Encoding(false));
            return true;
        }
        catch
        {
            return false; // best-effort — no alternate streams on this volume
        }
    }

    /// <summary>The stream's text: CRLF-separated, exactly the shape Windows and the browsers write.</summary>
    public static string Build(string? hostUrl, string? referrerUrl)
    {
        var text = new StringBuilder();
        text.Append("[ZoneTransfer]\r\n");
        text.Append("ZoneId=").Append(InternetZone).Append("\r\n");

        if (Sanitize(referrerUrl) is { } referrer) text.Append("ReferrerUrl=").Append(referrer).Append("\r\n");
        if (Sanitize(hostUrl) is { } host) text.Append("HostUrl=").Append(host).Append("\r\n");

        return text.ToString();
    }

    /// <summary>
    /// A URL fit to write into the stream, or null. The user name and password are
    /// removed — the file may be copied anywhere, and the stream goes with it —
    /// and anything that is not an absolute http(s) or ftp(s) address is dropped
    /// rather than guessed at.
    /// </summary>
    public static string? Sanitize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https" or "ftp" or "ftps")) return null;

        string clean = uri.GetComponents(
            UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped);

        // Belt and braces: an escaped URI cannot contain a raw control character,
        // but this text is parsed line by line by other programs, and a line
        // break smuggled into it would let a referrer write its own ZoneId.
        return clean.Any(char.IsControl) ? null : clean;
    }
}
