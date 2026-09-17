using QuickByte.Core.Streaming;

namespace QuickByte.Core.Tests.Streaming;

/// <summary>
/// The playlist parser. Every mistake here produces a file that looks complete
/// and will not play: a byte range taken from the wrong offset, an IV that is off
/// by one, a relative address resolved against the wrong base.
/// </summary>
public sealed class HlsPlaylistTests
{
    private static readonly Uri Base = new("https://cdn.example.com/video/master.m3u8?token=abc");

    [Fact]
    public void A_master_playlist_lists_its_qualities_with_attributes_whose_values_contain_commas()
    {
        var playlist = HlsPlaylist.Parse(
            """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aac",NAME="English",DEFAULT=YES,URI="audio/en.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=5200000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2",AUDIO="aac"
            1080/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS="avc1.4d401e,mp4a.40.2",AUDIO="aac"
            https://other.example.com/360/index.m3u8
            """, Base);

        Assert.True(playlist.IsMaster);
        Assert.Equal(2, playlist.Variants.Count);

        var best = playlist.Variants[0];
        Assert.Equal(5_200_000, best.Bandwidth);
        Assert.Equal("1920x1080", best.Resolution);
        Assert.Equal("avc1.640028,mp4a.40.2", best.Codecs);
        Assert.Equal("aac", best.AudioGroup);
        Assert.Equal("https://cdn.example.com/video/1080/index.m3u8", best.Uri.AbsoluteUri);
        Assert.Equal("https://other.example.com/360/index.m3u8", playlist.Variants[1].Uri.AbsoluteUri);

        var audio = Assert.Single(playlist.Renditions);
        Assert.Equal("AUDIO", audio.Type);
        Assert.True(audio.IsDefault);
        Assert.Equal("https://cdn.example.com/video/audio/en.m3u8", audio.Uri!.AbsoluteUri);
    }

    [Fact]
    public void A_media_playlist_lists_its_pieces_in_order_with_their_sequence_numbers()
    {
        var playlist = HlsPlaylist.Parse(
            """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:6
            #EXT-X-MEDIA-SEQUENCE:40
            #EXTINF:6.006,
            seg40.ts
            #EXTINF:5.5,title
            seg41.ts
            #EXT-X-ENDLIST
            """, Base);

        Assert.False(playlist.IsMaster);
        Assert.True(playlist.IsComplete);
        Assert.Equal(new[] { 40L, 41L }, playlist.Segments.Select(s => s.SequenceNumber));
        Assert.Equal(6.006, playlist.Segments[0].Duration, 3);
        Assert.Equal("https://cdn.example.com/video/seg41.ts", playlist.Segments[1].Uri.AbsoluteUri);
    }

    [Fact]
    public void A_playlist_without_an_end_is_live()
    {
        var playlist = HlsPlaylist.Parse("#EXTM3U\n#EXTINF:4,\na.ts\n", Base);

        Assert.False(playlist.IsComplete);
    }

    [Fact]
    public void A_byte_range_without_an_offset_starts_where_the_previous_one_ended()
    {
        var playlist = HlsPlaylist.Parse(
            """
            #EXTM3U
            #EXTINF:4,
            #EXT-X-BYTERANGE:1000@500
            all.ts
            #EXTINF:4,
            #EXT-X-BYTERANGE:2000
            all.ts
            #EXT-X-ENDLIST
            """, Base);

        Assert.Equal(new ByteRange(500, 1000), playlist.Segments[0].Range);
        Assert.Equal(new ByteRange(1500, 2000), playlist.Segments[1].Range);
        Assert.Equal(3499, playlist.Segments[1].Range!.Value.End);
    }

    [Fact]
    public void A_key_applies_to_every_piece_after_it_until_another_key_replaces_it()
    {
        var playlist = HlsPlaylist.Parse(
            """
            #EXTM3U
            #EXTINF:4,
            clear.ts
            #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example.com/k1",IV=0x000102030405060708090A0B0C0D0E0F
            #EXTINF:4,
            locked1.ts
            #EXTINF:4,
            locked2.ts
            #EXT-X-KEY:METHOD=NONE
            #EXTINF:4,
            clear2.ts
            #EXT-X-ENDLIST
            """, Base);

        Assert.Null(playlist.Segments[0].Key);
        Assert.True(playlist.Segments[1].Key!.IsAes128);
        Assert.Same(playlist.Segments[1].Key, playlist.Segments[2].Key);
        Assert.Equal("https://keys.example.com/k1", playlist.Segments[1].Key!.Uri!.AbsoluteUri);
        Assert.Equal(Convert.FromHexString("000102030405060708090A0B0C0D0E0F"), playlist.Segments[1].Key!.Iv);
        Assert.Null(playlist.Segments[3].Key);
    }

    [Fact]
    public void Without_an_iv_a_piece_is_decrypted_with_its_sequence_number_big_endian()
    {
        var playlist = HlsPlaylist.Parse(
            """
            #EXTM3U
            #EXT-X-MEDIA-SEQUENCE:258
            #EXT-X-KEY:METHOD=AES-128,URI="k"
            #EXTINF:4,
            a.ts
            #EXT-X-ENDLIST
            """, Base);

        byte[] iv = HlsPlaylist.IvFor(playlist.Segments[0]);

        Assert.Equal(16, iv.Length);
        Assert.Equal(new byte[14], iv[..14]);
        Assert.Equal(new byte[] { 0x01, 0x02 }, iv[14..]);
    }

    [Fact]
    public void An_initialization_section_travels_with_the_pieces_after_it()
    {
        var playlist = HlsPlaylist.Parse(
            """
            #EXTM3U
            #EXT-X-MAP:URI="init.mp4",BYTERANGE="720@0"
            #EXTINF:4,
            s1.m4s
            #EXTINF:4,
            s2.m4s
            #EXT-X-ENDLIST
            """, Base);

        Assert.True(playlist.IsFragmentedMp4);
        var init = playlist.Segments[0].Init!;
        Assert.Same(init, playlist.Segments[1].Init);
        Assert.Equal("https://cdn.example.com/video/init.mp4", init.Uri.AbsoluteUri);
        Assert.Equal(new ByteRange(0, 720), init.Range);
    }

    [Fact]
    public void Windows_line_endings_and_a_byte_order_mark_are_read_like_any_playlist()
    {
        var playlist = HlsPlaylist.Parse((char)0xFEFF + "#EXTM3U\r\n#EXTINF:4,\r\na.ts\r\n#EXT-X-ENDLIST\r\n", Base);

        Assert.Equal("https://cdn.example.com/video/a.ts", Assert.Single(playlist.Segments).Uri.AbsoluteUri);
        Assert.True(playlist.IsComplete);
    }

    [Fact]
    public void Anything_that_does_not_start_with_the_header_is_not_a_playlist()
    {
        Assert.False(HlsPlaylist.LooksLikePlaylist("<html>sign in</html>"));
        Assert.Throws<FormatException>(() => HlsPlaylist.Parse("<html>sign in</html>", Base));
    }
}
