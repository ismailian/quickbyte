using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace QuickByte.Core.Streaming;

/// <summary>One piece of a DASH representation: a URL, and a byte range within it when the manifest names one.</summary>
public sealed record DashSegment(Uri Uri, ByteRange? Range);

public sealed record DashRepresentation(
    string Id,
    string ContentType,
    string MimeType,
    long Bandwidth,
    int Width,
    int Height,
    string? Codecs,
    DashSegment? Initialization,
    IReadOnlyList<DashSegment> Segments);

/// <summary>
/// A DASH manifest (MPD), reduced to what a download needs: the representations
/// of the first period and, for each, the exact list of segment URLs to fetch.
///
/// Segments are addressed three ways in the wild, and all three are handled —
/// <c>SegmentTemplate</c> (by number, or by time with a <c>SegmentTimeline</c>),
/// <c>SegmentList</c>, and a plain <c>BaseURL</c> holding the whole representation.
/// Two kinds of manifest are recognised and refused rather than half-downloaded:
/// a live one (<c>type="dynamic"</c>), which never ends, and one whose content is
/// protected (<c>ContentProtection</c>), which cannot be played once downloaded.
/// </summary>
public sealed partial class DashManifest
{
    public bool IsLive { get; private init; }
    public bool IsProtected { get; private init; }
    public IReadOnlyList<DashRepresentation> Representations { get; private init; } = Array.Empty<DashRepresentation>();

    /// <summary>More segments than this in one representation is a malformed or hostile manifest, not a video.</summary>
    public const int MaxSegments = 100_000;

    public static bool LooksLikeManifest(string text) =>
        text.Contains("<MPD", StringComparison.Ordinal);

    /// <exception cref="FormatException">Not a DASH manifest, or one that cannot be read.</exception>
    public static DashManifest Parse(string xml, Uri manifestUri)
    {
        XDocument document;
        try
        {
            // No DTDs and no external entities: this is a document from the internet.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FormatException($"The DASH manifest is not valid XML: {ex.Message}", ex);
        }

        var mpd = document.Root;
        if (mpd is null || mpd.Name.LocalName != "MPD") throw new FormatException("This is not a DASH manifest.");

        bool live = string.Equals((string?)mpd.Attribute("type"), "dynamic", StringComparison.OrdinalIgnoreCase);
        var period = Children(mpd, "Period").FirstOrDefault();
        if (period is null) return new DashManifest { IsLive = live };

        var periodDuration = Duration((string?)period.Attribute("duration"))
                             ?? Duration((string?)mpd.Attribute("mediaPresentationDuration"));

        var baseUri = BaseUrl(BaseUrl(manifestUri, mpd), period);
        bool isProtected = false;
        var representations = new List<DashRepresentation>();

        foreach (var set in Children(period, "AdaptationSet"))
        {
            if (Children(set, "ContentProtection").Any()) isProtected = true;

            var setBase = BaseUrl(baseUri, set);
            foreach (var representation in Children(set, "Representation"))
            {
                if (Children(representation, "ContentProtection").Any()) isProtected = true;

                string id = (string?)representation.Attribute("id") ?? string.Empty;
                string mime = Inherited(representation, set, "mimeType") ?? string.Empty;
                string contentType = (string?)set.Attribute("contentType") ?? mime.Split('/')[0];
                long bandwidth = (long?)representation.Attribute("bandwidth") ?? 0;
                var repBase = BaseUrl(setBase, representation);

                var (init, segments) = SegmentsOf(representation, set, repBase, id, bandwidth, periodDuration);
                representations.Add(new DashRepresentation(
                    id, contentType, mime, bandwidth,
                    (int?)representation.Attribute("width") ?? 0,
                    (int?)representation.Attribute("height") ?? 0,
                    Inherited(representation, set, "codecs"),
                    init, segments));
            }
        }

        return new DashManifest { IsLive = live, IsProtected = isProtected, Representations = representations };
    }

    private static (DashSegment? Init, IReadOnlyList<DashSegment> Segments) SegmentsOf(
        XElement representation, XElement set, Uri baseUri, string id, long bandwidth, TimeSpan? periodDuration)
    {
        var template = Children(representation, "SegmentTemplate").FirstOrDefault()
                       ?? Children(set, "SegmentTemplate").FirstOrDefault();
        if (template is not null)
            return FromTemplate(template, baseUri, id, bandwidth, periodDuration);

        var list = Children(representation, "SegmentList").FirstOrDefault()
                   ?? Children(set, "SegmentList").FirstOrDefault();
        if (list is not null)
        {
            var init = Children(list, "Initialization").FirstOrDefault() is { } initialization
                ? new DashSegment(
                    (string?)initialization.Attribute("sourceURL") is { } source ? new Uri(baseUri, source) : baseUri,
                    RangeAttribute((string?)initialization.Attribute("range")))
                : null;

            var segments = Children(list, "SegmentURL")
                .Select(url => new DashSegment(
                    (string?)url.Attribute("media") is { } media ? new Uri(baseUri, media) : baseUri,
                    RangeAttribute((string?)url.Attribute("mediaRange"))))
                .ToList();
            return (init, segments);
        }

        // SegmentBase, or nothing at all: the representation is one file. Its
        // initialization and index live inside it, so the whole file is the download.
        return (null, new[] { new DashSegment(baseUri, null) });
    }

    private static (DashSegment? Init, IReadOnlyList<DashSegment> Segments) FromTemplate(
        XElement template, Uri baseUri, string id, long bandwidth, TimeSpan? periodDuration)
    {
        long startNumber = (long?)template.Attribute("startNumber") ?? 1;
        long timescale = Math.Max(1, (long?)template.Attribute("timescale") ?? 1);
        string? media = (string?)template.Attribute("media");
        string? initialization = (string?)template.Attribute("initialization");

        DashSegment? init = initialization is null
            ? null
            : new DashSegment(new Uri(baseUri, Fill(initialization, id, bandwidth, number: null, time: null)), null);

        if (media is null) return (init, Array.Empty<DashSegment>());

        var segments = new List<DashSegment>();
        var timeline = Children(template, "SegmentTimeline").FirstOrDefault();

        if (timeline is not null)
        {
            long number = startNumber;
            long time = 0;
            long? periodEnd = periodDuration is { } period ? (long)(period.TotalSeconds * timescale) : null;

            foreach (var s in Children(timeline, "S"))
            {
                if ((long?)s.Attribute("t") is long t) time = t;
                long d = (long?)s.Attribute("d") ?? 0;
                long r = (long?)s.Attribute("r") ?? 0;
                if (d <= 0) break;

                // r = -1 repeats until the next S, or the end of the period.
                long repeats = r >= 0 ? r : periodEnd is long end ? Math.Max(0, (end - time) / d - 1) : 0;

                for (long i = 0; i <= repeats; i++)
                {
                    segments.Add(new DashSegment(new Uri(baseUri, Fill(media, id, bandwidth, number, time)), null));
                    if (segments.Count > MaxSegments) throw new FormatException("The DASH manifest lists more segments than a video could have.");
                    number++;
                    time += d;
                }
            }
            return (init, segments);
        }

        long duration = (long?)template.Attribute("duration") ?? 0;
        if (duration <= 0 || periodDuration is null) return (init, segments);

        long count = (long)Math.Ceiling(periodDuration.Value.TotalSeconds * timescale / duration);
        if (count > MaxSegments) throw new FormatException("The DASH manifest lists more segments than a video could have.");

        for (long i = 0; i < count; i++)
            segments.Add(new DashSegment(new Uri(baseUri, Fill(media, id, bandwidth, startNumber + i, i * duration)), null));

        return (init, segments);
    }

    [GeneratedRegex(@"\$(RepresentationID|Number|Time|Bandwidth)(?:%0(\d+)d)?\$")]
    private static partial Regex TemplateIdentifier();

    /// <summary>Substitutes a SegmentTemplate's identifiers — including the <c>%05d</c> width form, and <c>$$</c> for a literal dollar.</summary>
    internal static string Fill(string template, string id, long bandwidth, long? number, long? time)
    {
        string filled = TemplateIdentifier().Replace(template, match =>
        {
            string value = match.Groups[1].Value switch
            {
                "RepresentationID" => id,
                "Number" => number?.ToString(CultureInfo.InvariantCulture) ?? match.Value,
                "Time" => time?.ToString(CultureInfo.InvariantCulture) ?? match.Value,
                _ => bandwidth.ToString(CultureInfo.InvariantCulture)
            };
            return match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out int width)
                ? value.PadLeft(width, '0')
                : value;
        });
        return filled.Replace("$$", "$", StringComparison.Ordinal);
    }

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e => e.Name.LocalName == localName);

    private static string? Inherited(XElement representation, XElement set, string attribute) =>
        (string?)representation.Attribute(attribute) ?? (string?)set.Attribute(attribute);

    private static Uri BaseUrl(Uri parent, XElement element) =>
        Children(element, "BaseURL").FirstOrDefault() is { } baseUrl && !string.IsNullOrWhiteSpace(baseUrl.Value)
            ? new Uri(parent, baseUrl.Value.Trim())
            : parent;

    private static TimeSpan? Duration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return XmlConvert.ToTimeSpan(value); }
        catch (FormatException) { return null; }
    }

    private static ByteRange? RangeAttribute(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string[] parts = value.Split('-');
        return parts.Length == 2
               && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long start)
               && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long end)
               && end >= start
            ? new ByteRange(start, end - start + 1)
            : null;
    }
}
