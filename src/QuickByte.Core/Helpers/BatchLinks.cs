using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace QuickByte.Core.Helpers;

/// <summary>
/// Turns what a user pastes into a batch download — a list of links, a web page's
/// source, or a numbered pattern — into the links themselves.
///
/// A pattern is a URL with a bracketed range in it:
/// <c>https://example.com/photo[001-250].jpg</c> is 250 links, zero-padded to the
/// width of the first number, and <c>…/disc[a-c].iso</c> three. Several ranges in
/// one URL multiply. Only a range of two numbers or two single letters counts,
/// which is what keeps an IPv6 host such as <c>http://[::1]/</c> from being read as
/// one.
/// </summary>
public static partial class BatchLinks
{
    /// <summary>
    /// The most links one batch will produce. A pattern is one typo away from
    /// <c>[1-99999999]</c>, and a hundred million rows is a frozen window rather
    /// than a download list.
    /// </summary>
    public const int MaxLinks = 5000;

    /// <summary>The largest file <see cref="ExtractLinks"/> is asked to read; a page source is far smaller.</summary>
    public const long MaxImportBytes = 10 * 1024 * 1024;

    [GeneratedRegex(@"\[(?:(?<from>\d{1,9})-(?<to>\d{1,9})|(?<fromLetter>[a-zA-Z])-(?<toLetter>[a-zA-Z]))\]")]
    private static partial Regex RangePattern();

    [GeneratedRegex(@"(?:https?|ftps?)://[^\s""'<>`]+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    /// <summary>
    /// Every link in <paramref name="text"/>, in the order they first appear, each
    /// once, with patterns expanded. Anything that is not an http(s) or ftp(s) URL
    /// is ignored — a pasted page source has plenty of those.
    /// </summary>
    /// <exception cref="ArgumentException">The text would produce more than <see cref="MaxLinks"/> links.</exception>
    public static IReadOnlyList<string> ExtractLinks(string text)
    {
        var links = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in LinkPattern().Matches(text ?? string.Empty))
        {
            string candidate = TrimTrailingPunctuation(WebUtilityDecode(match.Value));
            foreach (string link in ExpandPattern(candidate))
            {
                if (!IsSupported(link) || !seen.Add(link)) continue;
                links.Add(link);
                if (links.Count > MaxLinks)
                    throw new ArgumentException($"That is more than {MaxLinks:N0} links. Split it into smaller batches.");
            }
        }

        return links;
    }

    /// <summary>
    /// The links a pattern stands for; a URL with no range in it is just itself.
    /// </summary>
    /// <exception cref="ArgumentException">The pattern stands for more than <see cref="MaxLinks"/> links.</exception>
    public static IReadOnlyList<string> ExpandPattern(string template)
    {
        var match = RangePattern().Match(template);
        if (!match.Success) return new[] { template };

        IReadOnlyList<string> values = RangeValues(match);
        string head = template[..match.Index];
        string tailTemplate = template[(match.Index + match.Length)..];

        // The tail may hold further ranges; each value of this one multiplies them.
        IReadOnlyList<string> tails = ExpandPattern(tailTemplate);
        long count = (long)values.Count * tails.Count;
        if (count > MaxLinks)
            throw new ArgumentException($"That pattern stands for {count:N0} links; the most a batch takes is {MaxLinks:N0}.");

        var expanded = new List<string>((int)count);
        foreach (string value in values)
        {
            foreach (string tail in tails)
                expanded.Add(head + value + tail);
        }
        return expanded;
    }

    /// <summary>Whether <paramref name="text"/> holds a range a pattern would expand.</summary>
    public static bool HasPattern(string text) => RangePattern().IsMatch(text ?? string.Empty);

    private static IReadOnlyList<string> RangeValues(Match match)
    {
        if (match.Groups["from"].Success)
        {
            string fromText = match.Groups["from"].Value;
            long from = long.Parse(fromText, CultureInfo.InvariantCulture);
            long to = long.Parse(match.Groups["to"].Value, CultureInfo.InvariantCulture);

            // "[001-250]" pads to three digits; "[1-250]" does not pad at all.
            int width = fromText.Length > 1 && fromText[0] == '0' ? fromText.Length : 0;
            long step = from <= to ? 1 : -1;
            long count = Math.Abs(to - from) + 1;
            if (count > MaxLinks)
                throw new ArgumentException($"A range of {count:N0} is more than a batch takes ({MaxLinks:N0}).");

            var numbers = new List<string>((int)count);
            for (long n = from; ; n += step)
            {
                numbers.Add(n.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'));
                if (n == to) break;
            }
            return numbers;
        }

        char first = match.Groups["fromLetter"].Value[0];
        char last = match.Groups["toLetter"].Value[0];

        // A range that changes case half way ("[a-Z]") has no obvious meaning, and
        // the ASCII table between them is punctuation.
        if (char.IsUpper(first) != char.IsUpper(last))
            return new[] { match.Value };

        var letters = new List<string>();
        int direction = first <= last ? 1 : -1;
        for (char c = first; ; c = (char)(c + direction))
        {
            letters.Add(c.ToString());
            if (c == last) break;
        }
        return letters;
    }

    private static bool IsSupported(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" or "ftp" or "ftps"
        && !string.IsNullOrEmpty(uri.Host);

    /// <summary>
    /// A link at the end of a sentence, or inside parentheses, picks up the
    /// punctuation around it. Trailing <c>.</c>, <c>,</c>, <c>;</c> and <c>:</c>
    /// are never the end of a file URL worth keeping; a closing bracket is kept
    /// only when the link opened one.
    /// </summary>
    private static string TrimTrailingPunctuation(string link)
    {
        while (link.Length > 0)
        {
            char last = link[^1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?')
            {
                link = link[..^1];
                continue;
            }

            if ((last == ')' && link.Count(c => c == '(') < link.Count(c => c == ')'))
                || (last == ']' && link.Count(c => c == '[') < link.Count(c => c == ']')))
            {
                link = link[..^1];
                continue;
            }
            break;
        }
        return link;
    }

    /// <summary>A page source writes <c>&amp;</c> for the ampersand between query parameters.</summary>
    private static string WebUtilityDecode(string link) =>
        link.Contains('&', StringComparison.Ordinal) ? System.Net.WebUtility.HtmlDecode(link) : link;

    /// <summary>
    /// Reads a file of links, refusing one too large to be a list of links or a
    /// page source — see <see cref="MaxImportBytes"/>.
    /// </summary>
    public static string ReadImportFile(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxImportBytes)
            throw new ArgumentException($"That file is {ByteFormatter.FormatBytes(info.Length)}; a list of links is never that big.");

        return File.ReadAllText(path, Encoding.UTF8);
    }
}
