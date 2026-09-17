using QuickByte.Core.Exceptions;
using QuickByte.Core.Streaming;

namespace QuickByte.Core.Tests.Streaming;

/// <summary>
/// Where a stream is judged downloadable. Each refusal here is a download that
/// would otherwise run to 100% and produce a file that does not play — or, for a
/// live stream, never finish.
/// </summary>
public sealed class StreamPlanTests
{
    private static readonly Uri Base = new("https://cdn.example.com/v/index.m3u8");

    private static HlsPlaylist Hls(string text) => HlsPlaylist.Parse(text, Base);

    [Fact]
    public void A_live_playlist_is_refused()
    {
        var ex = Assert.Throws<StreamNotSupportedException>(() => StreamPlan.ForHls(Hls("#EXTM3U\n#EXTINF:4,\na.ts\n")));
        Assert.Equal(StreamPlan.LiveMessage, ex.Message);
    }

    [Theory]
    [InlineData("#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"https://keys.example.com/k\"")]
    [InlineData("#EXT-X-KEY:METHOD=AES-128,URI=\"skd://fairplay-asset\"")]
    [InlineData("#EXT-X-KEY:METHOD=AES-128")]
    public void Protected_pieces_are_refused(string keyTag)
    {
        var ex = Assert.Throws<StreamNotSupportedException>(() =>
            StreamPlan.ForHls(Hls($"#EXTM3U\n{keyTag}\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n")));
        Assert.Equal(StreamPlan.ProtectedMessage, ex.Message);
    }

    [Fact]
    public void A_piece_that_is_not_on_the_web_is_refused()
    {
        // A playlist is a document from the internet; it must not be able to have
        // the download read the user's own files.
        Assert.Throws<FormatException>(() =>
            StreamPlan.ForHls(Hls("#EXTM3U\n#EXTINF:4,\nfile:///C:/Users/me/secret.txt\n#EXT-X-ENDLIST\n")));
    }

    [Fact]
    public void An_initialization_section_goes_in_front_of_the_pieces_it_applies_to_once()
    {
        var track = StreamPlan.ForHls(Hls(
            """
            #EXTM3U
            #EXT-X-MAP:URI="init-a.mp4"
            #EXTINF:4,
            a1.m4s
            #EXTINF:4,
            a2.m4s
            #EXT-X-DISCONTINUITY
            #EXT-X-MAP:URI="init-b.mp4"
            #EXTINF:4,
            b1.m4s
            #EXT-X-ENDLIST
            """));

        Assert.Equal(
            new[] { "init-a.mp4", "a1.m4s", "a2.m4s", "init-b.mp4", "b1.m4s" },
            track.Pieces.Select(p => Path.GetFileName(p.Uri.AbsolutePath)));
        Assert.Equal(new[] { true, false, false, true, false }, track.Pieces.Select(p => p.IsInitialization));
    }

    [Fact]
    public void An_encrypted_piece_carries_its_key_and_the_iv_for_its_own_sequence_number()
    {
        var track = StreamPlan.ForHls(Hls(
            """
            #EXTM3U
            #EXT-X-MEDIA-SEQUENCE:5
            #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example.com/k"
            #EXTINF:4,
            a.ts
            #EXTINF:4,
            b.ts
            #EXT-X-ENDLIST
            """));

        Assert.All(track.Pieces, p => Assert.Equal("https://keys.example.com/k", p.Key!.Uri.AbsoluteUri));
        Assert.Equal(5, track.Pieces[0].Key!.Iv[15]);
        Assert.Equal(6, track.Pieces[1].Key!.Iv[15]);
    }

    [Fact]
    public void A_dash_representation_starts_with_its_initialization()
    {
        var manifest = DashManifest.Parse(
            """
            <MPD mediaPresentationDuration="PT8S"><Period><AdaptationSet mimeType="video/mp4">
              <SegmentTemplate duration="4" initialization="init.mp4" media="$Number$.m4s"/>
              <Representation id="v" bandwidth="1"/>
            </AdaptationSet></Period></MPD>
            """, new Uri("https://cdn.example.com/m.mpd"));

        var track = StreamPlan.ForDash(manifest, "v");

        Assert.Equal(new[] { "init.mp4", "1.m4s", "2.m4s" }, track.Pieces.Select(p => Path.GetFileName(p.Uri.AbsolutePath)));
        Assert.True(track.Pieces[0].IsInitialization);
    }

    [Fact]
    public void A_quality_the_manifest_no_longer_offers_is_reported_as_such()
    {
        var manifest = DashManifest.Parse("<MPD><Period/></MPD>", new Uri("https://cdn.example.com/m.mpd"));

        var ex = Assert.Throws<FormatException>(() => StreamPlan.ForDash(manifest, "gone"));
        Assert.Contains("no longer offers", ex.Message);
    }
}
