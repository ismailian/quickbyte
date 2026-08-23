using QuickByte.Core.Models;

namespace QuickByte.Core.Helpers;

/// <summary>
/// Pulls the <c>user:password@</c> part out of a URL.
///
/// <c>ftp://alice:hunter2@example.com/disk.iso</c> is still the way FTP links
/// are pasted around, and the whole point of
/// <see cref="SecretProtector"/> is lost if that password is then persisted as
/// part of <see cref="DownloadItem.Url"/> — where it would sit in plain text in
/// downloads.json, in the details window, and in the main list's tooltip.
/// Splitting it at the point the download is created keeps the secret in the
/// one field that knows how to protect itself.
/// </summary>
public static class UrlCredentials
{
    public readonly record struct Split(string Url, DownloadCredentials? Credentials);

    public static Split Extract(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
            return new Split(url, null);

        string[] parts = uri.UserInfo.Split(':', 2);
        var credentials = new DownloadCredentials
        {
            UserName = Uri.UnescapeDataString(parts[0]),
            Password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty
        };

        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        return new Split(builder.Uri.ToString(), credentials.IsEmpty ? null : credentials);
    }
}
