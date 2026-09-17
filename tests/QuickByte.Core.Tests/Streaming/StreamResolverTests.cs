using System.Globalization;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Models;
using QuickByte.Core.Streaming;

namespace QuickByte.Core.Tests.Streaming;

/// <summary>
/// What the Add window offers for a playlist: the qualities, best first, with
/// the sound that goes with each and the extensions the files will be saved with —
/// and the streams it refuses before they are added.
/// </summary>
public sealed class StreamResolverTests
{
    private const string Root = "https://cdn.example.com/show/";

    public StreamResolverTests()
    {
        // Labels carry a decimal ("5.2 Mbps"), and this machine's separator is a comma.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    [Theory]
    [InlineData("https://x.example.com/a/master.m3u8?sig=1", null, true)]
    [InlineData("https://x.example.com/a/manifest.MPD", "text/plain", true)]
    [InlineData("https://x.example.com/play?id=4", "application/vnd.apple.mpegurl; charset=utf-8", true)]
    [InlineData("https://x.example.com/play?id=4", "application/dash+xml", true)]
    [InlineData("https://x.example.com/video.mp4", "video/mp4", false)]
    [InlineData("https://x.example.com/list.m3u", "audio/x-scpls", false)]
    public void Playlists_are_recognised_by_type_or_by_extension(string url, string? contentType, bool expected)
    {
        Assert.Equal(expected, StreamDetector.IsStream(url, contentType));
    }

    [Theory]
    [InlineData("https://cdn.example.com/talks/keynote-2026/master.m3u8", "master.m3u8", ".mp4", "keynote-2026.mp4")]
    [InlineData("https://cdn.example.com/talks/keynote-2026/hls/1080/index.m3u8", "index.m3u8", ".ts", "keynote-2026.ts")]
    [InlineData("https://cdn.example.com/v/3f9c0e1a2b4d6f80a1c3e5f7/manifest.mpd", "manifest.mpd", ".mp4", "video.mp4")]
    [InlineData("https://cdn.example.com/show/Launch%20Day/master.m3u8", "master.m3u8", ".mp4", "Launch Day.mp4")]
    [InlineData("https://cdn.example.com/show/master.m3u8", "Our Launch Day.m3u8", ".mp4", "Our Launch Day.mp4")]
    public void A_generic_playlist_name_gives_way_to_the_folder_the_video_sits_in(string url, string name, string extension, string expected)
    {
        Assert.Equal(expected, StreamDetector.SuggestFileName(url, name, extension));
    }

    [Fact]
    public async Task A_master_playlist_offers_each_quality_best_first_with_its_separate_sound()
    {
        using var web = new StubWeb();
        web.Add(Root + "master.m3u8", """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",NAME="Commentary",URI="audio/commentary.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",NAME="Main",DEFAULT=YES,URI="audio/main.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,AUDIO="a"
            360.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=5200000,RESOLUTION=1920x1080,AUDIO="a"
            1080.m3u8
            """);
        web.Add(Root + "1080.m3u8", Fmp4Media("v"));
        web.Add(Root + "audio/main.m3u8", Fmp4Media("a"));

        var description = await new StreamResolver(web.Client).DescribeAsync(Root + "master.m3u8", null, default);

        Assert.Equal(StreamFormat.Hls, description.Format);
        Assert.Equal(new[] { "1080p · 5.2 Mbps", "360p · 800 kbps", "Audio only" }, description.Choices.Select(c => c.Label));

        var best = description.Choices[0];
        Assert.Equal(Root + "1080.m3u8", best.Source.MainPlaylistUrl);
        Assert.Equal(Root + "audio/main.m3u8", best.Source.AudioPlaylistUrl);
        Assert.Equal(".mp4", best.Extension);
        Assert.Equal(".m4a", best.Source.AudioExtension);
        Assert.True(best.Source.HasSeparateAudio);

        // Only the best quality and its sound are read; the rest are packaged alike.
        Assert.Equal(0, web.CountRequests(Root + "360.m3u8"));

        var sound = description.Choices[2];
        Assert.Equal(Root + "audio/main.m3u8", sound.Source.MainPlaylistUrl);
        Assert.False(sound.Source.HasSeparateAudio);
        Assert.Equal(".m4a", sound.Extension);
    }

    [Fact]
    public async Task The_same_picture_with_different_sound_is_labelled_by_its_sound()
    {
        using var web = new StubWeb();
        web.Add(Root + "master.m3u8", """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aac",NAME="English",DEFAULT=YES,URI="aac.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="ac3",NAME="English",DEFAULT=YES,URI="ac3.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=8000000,RESOLUTION=1920x1080,CODECS="avc1.640028,ac-3",AUDIO="ac3"
            1080-ac3.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=7900000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2",AUDIO="aac"
            1080-aac.m3u8
            """);
        web.Add(Root + "1080-ac3.m3u8", Fmp4Media("v"));
        web.Add(Root + "ac3.m3u8", Fmp4Media("a"));

        var description = await new StreamResolver(web.Client).DescribeAsync(Root + "master.m3u8", null, default);

        Assert.Equal(
            new[] { "1080p · 8 Mbps · AC-3", "1080p · 7.9 Mbps · AAC", "Audio only · AC-3" },
            description.Choices.Select(c => c.Label));
    }

    [Fact]
    public async Task A_single_media_playlist_is_offered_as_it_is()
    {
        using var web = new StubWeb();
        web.Add(Root + "index.m3u8", "#EXTM3U\n#EXTINF:4,\na.ts\n#EXTINF:4,\nb.ts\n#EXT-X-ENDLIST\n");

        var description = await new StreamResolver(web.Client).DescribeAsync(Root + "index.m3u8", null, default);

        var only = Assert.Single(description.Choices);
        Assert.Equal(Root + "index.m3u8", only.Source.MainPlaylistUrl);
        Assert.Equal(".ts", only.Extension);
        Assert.Equal("video/mp2t", only.ContentType);
        Assert.False(only.Source.HasSeparateAudio);
    }

    [Fact]
    public async Task A_live_stream_is_refused_before_it_is_added()
    {
        using var web = new StubWeb();
        web.Add(Root + "master.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nlive.m3u8\n");
        web.Add(Root + "live.m3u8", "#EXTM3U\n#EXTINF:4,\na.ts\n");

        var ex = await Assert.ThrowsAsync<StreamNotSupportedException>(
            () => new StreamResolver(web.Client).DescribeAsync(Root + "master.m3u8", null, default));
        Assert.Equal(StreamPlan.LiveMessage, ex.Message);
    }

    [Fact]
    public async Task A_dash_manifest_offers_each_video_with_the_best_sound_and_the_sound_alone()
    {
        using var web = new StubWeb();
        web.Add(Root + "manifest.mpd", """
            <MPD mediaPresentationDuration="PT8S"><Period>
              <AdaptationSet contentType="video" mimeType="video/mp4">
                <SegmentTemplate duration="4" media="$RepresentationID$/$Number$.m4s"/>
                <Representation id="v720" bandwidth="2500000" height="720"/>
                <Representation id="v1080" bandwidth="5000000" height="1080"/>
              </AdaptationSet>
              <AdaptationSet contentType="audio" mimeType="audio/mp4">
                <SegmentTemplate duration="4" media="$RepresentationID$/$Number$.m4s"/>
                <Representation id="a64" bandwidth="64000"/>
                <Representation id="a128" bandwidth="128000"/>
              </AdaptationSet>
            </Period></MPD>
            """, "application/dash+xml");

        var description = await new StreamResolver(web.Client).DescribeAsync(Root + "manifest.mpd", null, default);

        Assert.Equal(new[] { "1080p · 5 Mbps", "720p · 2.5 Mbps", "Audio only · 128 kbps" }, description.Choices.Select(c => c.Label));
        Assert.All(description.Choices.Take(2), c => Assert.Equal("a128", c.Source.AudioRepresentation));
        Assert.Equal("v1080", description.Choices[0].Source.MainRepresentation);

        var audioOnly = description.Choices[2];
        Assert.Equal("a128", audioOnly.Source.MainRepresentation);
        Assert.Null(audioOnly.Source.AudioRepresentation);
        Assert.Equal(".m4a", audioOnly.Extension);
    }

    [Fact]
    public async Task A_protected_dash_manifest_is_refused()
    {
        using var web = new StubWeb();
        web.Add(Root + "drm.mpd", """
            <MPD><Period><AdaptationSet mimeType="video/mp4">
              <ContentProtection schemeIdUri="urn:mpeg:dash:mp4protection:2011" value="cenc"/>
              <Representation id="v" bandwidth="1"><BaseURL>v.mp4</BaseURL></Representation>
            </AdaptationSet></Period></MPD>
            """, "application/dash+xml");

        await Assert.ThrowsAsync<StreamNotSupportedException>(
            () => new StreamResolver(web.Client).DescribeAsync(Root + "drm.mpd", null, default));
    }

    [Fact]
    public async Task A_page_where_a_playlist_should_be_is_not_a_stream()
    {
        using var web = new StubWeb();
        web.Add(Root + "expired.m3u8", "<html>Please sign in</html>", "text/html");

        await Assert.ThrowsAsync<FormatException>(
            () => new StreamResolver(web.Client).DescribeAsync(Root + "expired.m3u8", null, default));
    }

    [Fact]
    public async Task The_playlist_is_asked_for_with_the_pages_cookies()
    {
        using var web = new StubWeb();
        web.Add(Root + "index.m3u8", "#EXTM3U\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n");
        var options = new RequestOptions { Headers = new Dictionary<string, string> { ["Cookie"] = "session=42" } };

        await new StreamResolver(web.Client).DescribeAsync(Root + "index.m3u8", options, default);

        Assert.Equal("session=42", web.Requests.Single().Cookie);
    }

    private static string Fmp4Media(string prefix) => $"""
        #EXTM3U
        #EXT-X-MAP:URI="{prefix}-init.mp4"
        #EXTINF:4,
        {prefix}1.m4s
        #EXT-X-ENDLIST
        """;
}
