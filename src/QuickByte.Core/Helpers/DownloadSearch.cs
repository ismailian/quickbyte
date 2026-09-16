using QuickByte.Core.Models;

namespace QuickByte.Core.Helpers;

/// <summary>
/// The search box above the downloads list: which downloads a query finds.
///
/// Every word of the query has to appear somewhere in the file name or in the
/// address — its host and path, unescaped, so "my file" finds
/// <c>my%20file.zip</c>. Case is ignored and word order does not matter, which is
/// how people search a list they half remember ("ubuntu iso", "github setup").
/// A query string is left out: on a signed link it is a page of random tokens,
/// and every S3 download would match "amz".
/// </summary>
public static class DownloadSearch
{
    /// <summary>The words of a query; an empty query has none and matches everything.</summary>
    public static IReadOnlyList<string> Terms(string? query) =>
        (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    public static bool Matches(DownloadItem item, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return true;

        string haystack = $"{item.FileName}\n{SearchableAddress(item.Url)}";
        foreach (string term in terms)
        {
            if (haystack.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) < 0) return false;
        }
        return true;
    }

    public static bool Matches(DownloadItem item, string? query) => Matches(item, Terms(query));

    private static string SearchableAddress(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

        try { return uri.Host + Uri.UnescapeDataString(uri.AbsolutePath); }
        catch { return uri.Host + uri.AbsolutePath; }
    }
}
