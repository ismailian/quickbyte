using QuickByte.Core.Streaming;

namespace QuickByte.Core.Tests.Streaming;

/// <summary>
/// The DASH manifest reader: three ways of addressing segments, the base-address
/// hierarchy they resolve against, and the manifests that must be refused.
/// </summary>
public sealed class DashManifestTests
{
    private static readonly Uri ManifestUri = new("https://media.example.com/show/ep1/manifest.mpd");

    private const string Ns = "urn:mpeg:dash:schema:mpd:2011";

    [Fact]
    public void A_numbered_template_is_expanded_over_the_period_with_padding()
    {
        var manifest = DashManifest.Parse($"""
            <MPD xmlns="{Ns}" type="static" mediaPresentationDuration="PT10S">
              <Period>
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <SegmentTemplate timescale="1000" duration="4000" startNumber="1"
                                   initialization="$RepresentationID$/init.mp4"
                                   media="$RepresentationID$/seg-$Number%05d$.m4s"/>
                  <Representation id="v1080" bandwidth="5000000" width="1920" height="1080" codecs="avc1.640028"/>
                </AdaptationSet>
              </Period>
            </MPD>
            """, ManifestUri);

        var video = Assert.Single(manifest.Representations);
        Assert.Equal("video", video.ContentType);
        Assert.Equal(1080, video.Height);
        Assert.Equal("https://media.example.com/show/ep1/v1080/init.mp4", video.Initialization!.Uri.AbsoluteUri);

        // 10 s in 4 s pieces is three pieces, the last one short.
        Assert.Equal(
            new[] { "v1080/seg-00001.m4s", "v1080/seg-00002.m4s", "v1080/seg-00003.m4s" },
            video.Segments.Select(s => s.Uri.AbsoluteUri.Replace("https://media.example.com/show/ep1/", "")));
    }

    [Fact]
    public void A_timeline_repeats_each_entry_and_addresses_pieces_by_time()
    {
        var manifest = DashManifest.Parse($"""
            <MPD xmlns="{Ns}" mediaPresentationDuration="PT20S">
              <Period>
                <AdaptationSet mimeType="audio/mp4">
                  <Representation id="a" bandwidth="128000">
                    <SegmentTemplate timescale="10" media="a/$Time$.m4s" startNumber="7">
                      <SegmentTimeline>
                        <S t="0" d="40" r="1"/>
                        <S d="30"/>
                        <S d="45" r="-1"/>
                      </SegmentTimeline>
                    </SegmentTemplate>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """, ManifestUri);

        var audio = Assert.Single(manifest.Representations);
        Assert.Equal("audio", audio.ContentType);

        // 0, 40 (r=1); 80 (d=30); then 45s from 110 until the 200 the period ends at: 110, 155.
        Assert.Equal(
            new[] { "a/0.m4s", "a/40.m4s", "a/80.m4s", "a/110.m4s", "a/155.m4s" },
            audio.Segments.Select(s => s.Uri.AbsoluteUri.Replace("https://media.example.com/show/ep1/", "")));
    }

    [Fact]
    public void A_segment_list_keeps_each_pieces_byte_range_and_base_addresses_nest()
    {
        var manifest = DashManifest.Parse($"""
            <MPD xmlns="{Ns}">
              <BaseURL>https://cdn.example.net/root/</BaseURL>
              <Period>
                <AdaptationSet mimeType="video/mp4">
                  <BaseURL>video/</BaseURL>
                  <Representation id="v" bandwidth="1">
                    <BaseURL>file.mp4</BaseURL>
                    <SegmentList>
                      <Initialization range="0-799"/>
                      <SegmentURL mediaRange="800-1799"/>
                      <SegmentURL media="other.mp4" mediaRange="0-99"/>
                    </SegmentList>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """, ManifestUri);

        var video = Assert.Single(manifest.Representations);
        Assert.Equal("https://cdn.example.net/root/video/file.mp4", video.Initialization!.Uri.AbsoluteUri);
        Assert.Equal(new ByteRange(0, 800), video.Initialization.Range);
        Assert.Equal(new ByteRange(800, 1000), video.Segments[0].Range);
        Assert.Equal("https://cdn.example.net/root/video/other.mp4", video.Segments[1].Uri.AbsoluteUri);
    }

    [Fact]
    public void A_representation_that_is_one_file_is_one_piece()
    {
        var manifest = DashManifest.Parse($"""
            <MPD xmlns="{Ns}">
              <Period>
                <AdaptationSet mimeType="audio/webm">
                  <Representation id="opus" bandwidth="160000">
                    <BaseURL>audio.webm</BaseURL>
                    <SegmentBase indexRange="100-400"/>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """, ManifestUri);

        var piece = Assert.Single(Assert.Single(manifest.Representations).Segments);
        Assert.Equal("https://media.example.com/show/ep1/audio.webm", piece.Uri.AbsoluteUri);
        Assert.Null(piece.Range);
    }

    [Fact]
    public void Live_and_protected_manifests_say_so()
    {
        var live = DashManifest.Parse($"""<MPD xmlns="{Ns}" type="dynamic"><Period/></MPD>""", ManifestUri);
        var drm = DashManifest.Parse($"""
            <MPD xmlns="{Ns}"><Period><AdaptationSet mimeType="video/mp4">
              <ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"/>
              <Representation id="v" bandwidth="1"><BaseURL>v.mp4</BaseURL></Representation>
            </AdaptationSet></Period></MPD>
            """, ManifestUri);

        Assert.True(live.IsLive);
        Assert.True(drm.IsProtected);
    }

    [Fact]
    public void A_document_type_declaration_is_refused_rather_than_resolved()
    {
        // An entity that reads a local file is the classic way to turn an XML
        // parser on a document from the internet against the machine reading it.
        const string hostile = """
            <?xml version="1.0"?>
            <!DOCTYPE MPD [<!ENTITY secret SYSTEM "file:///C:/Windows/win.ini">]>
            <MPD><Period><AdaptationSet><Representation id="&secret;"/></AdaptationSet></Period></MPD>
            """;

        Assert.Throws<FormatException>(() => DashManifest.Parse(hostile, ManifestUri));
    }

    [Fact]
    public void A_template_claiming_more_pieces_than_a_video_could_have_is_refused()
    {
        // A one-millisecond piece over a year would otherwise allocate for minutes.
        Assert.Throws<FormatException>(() => DashManifest.Parse($"""
            <MPD xmlns="{Ns}" mediaPresentationDuration="P365D">
              <Period><AdaptationSet mimeType="video/mp4">
                <SegmentTemplate timescale="1000" duration="1" media="$Number$.m4s"/>
                <Representation id="v" bandwidth="1"/>
              </AdaptationSet></Period>
            </MPD>
            """, ManifestUri));
    }

    [Theory]
    [InlineData("$RepresentationID$/$Bandwidth$/$Number$", "v/800/12")]
    [InlineData("t$Time%08d$.m4s", "t00004000.m4s")]
    [InlineData("price$$5-$Number%03d$", "price$5-012")]
    public void Template_identifiers_are_substituted(string template, string expected)
    {
        Assert.Equal(expected, DashManifest.Fill(template, "v", 800, 12, 4000));
    }
}
