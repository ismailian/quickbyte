using System.Net;
using System.Text.RegularExpressions;

namespace QuickByte.Core.Helpers;

/// <summary>
/// Which addresses skip a manual proxy — the list typed into Options, in the
/// forms Windows' own proxy settings accept, so a list copied from there means
/// the same thing here.
/// </summary>
public static class ProxyBypass
{
    /// <summary>More entries than this is not a bypass list anyone typed.</summary>
    public const int MaxEntries = 200;

    /// <summary>Entries as typed, normalised: lower-case, no scheme, no trailing slash.</summary>
    public static IReadOnlyList<string> Parse(string? list) =>
        (list ?? string.Empty)
            .Split(new[] { ';', ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalise)
            .Where(entry => entry.Length > 0)
            .Distinct()
            .Take(MaxEntries)
            .ToList();

    /// <summary>
    /// This machine. Never sent through a proxy: nothing a proxy could reach there
    /// is this machine, and a local service asked through one answers for the
    /// proxy's host instead.
    /// </summary>
    public static bool IsLocal(Uri destination) =>
        destination.IsLoopback
        || destination.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || destination.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    public static bool Matches(IReadOnlyList<string> entries, Uri destination)
    {
        string host = destination.IdnHost.Trim('[', ']').ToLowerInvariant();

        foreach (string entry in entries)
        {
            if (entry == "<local>")
            {
                // Windows' meaning: a name with no dot in it, which only a local
                // network could resolve. An IP address is not such a name.
                if (!host.Contains('.') && !IPAddress.TryParse(host, out _)) return true;
                continue;
            }

            var (pattern, port) = SplitPort(entry);
            if (port is not null && port != destination.Port) continue;

            if (pattern.StartsWith('.'))
            {
                // ".example.com" — the domain and everything under it.
                if (host == pattern[1..] || host.EndsWith(pattern, StringComparison.Ordinal)) return true;
                continue;
            }

            if (pattern.Contains('*'))
            {
                string regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$";
                if (Regex.IsMatch(host, regex, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) return true;
                continue;
            }

            if (host == pattern) return true;
        }

        return false;
    }

    private static string Normalise(string entry)
    {
        string e = entry.Trim().ToLowerInvariant();
        int scheme = e.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) e = e[(scheme + 3)..];
        return e.TrimEnd('/');
    }

    /// <summary>"host:8080" → ("host", 8080). An IPv6 address's own colons are not a port.</summary>
    private static (string Pattern, int? Port) SplitPort(string entry)
    {
        if (entry.StartsWith('['))
        {
            int close = entry.IndexOf(']');
            if (close > 0)
            {
                string address = entry[1..close];
                return close + 1 < entry.Length && entry[close + 1] == ':' && int.TryParse(entry[(close + 2)..], out int v6Port)
                    ? (address, v6Port)
                    : (address, null);
            }
        }

        int colon = entry.LastIndexOf(':');
        if (colon > 0 && entry.IndexOf(':') == colon && int.TryParse(entry[(colon + 1)..], out int port))
            return (entry[..colon], port);

        return (entry, null);
    }
}
