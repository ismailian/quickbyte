using System.Drawing;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Tests;

/// <summary>
/// The .ICO container QuickByte writes for itself.
///
/// This code exists because the BCL can only ever <em>read</em> an
/// <see cref="Icon"/>: the alternative was one bitmap scaled down by Windows
/// for the title bar, which is visibly soft. Writing the container by hand
/// means writing a binary layout by hand — byte-packed sizes, an offset table,
/// a bitmap header whose height is deliberately wrong — and none of that fails
/// loudly. A broken directory is an icon that is blank, or one Windows quietly
/// declines to draw.
/// </summary>
public sealed class IcoWriterTests
{
    private static byte[] Write(params int[] sizes)
    {
        var bitmaps = sizes.Select(Bitmap).ToList();
        try
        {
            using var stream = new MemoryStream();
            IcoWriter.Write(stream, bitmaps);
            return stream.ToArray();
        }
        finally
        {
            foreach (var bitmap in bitmaps) bitmap.Dispose();
        }
    }

    private static Bitmap Bitmap(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(200, 10, 120, 200));
        return bitmap;
    }

    // ------------------------------------------------------- directory --

    [Fact]
    public void The_directory_says_it_is_an_icon_and_how_many_images_it_holds()
    {
        var ico = IcoFile.Read(Write(16, 32, 48));

        Assert.Equal(0, ico.Reserved);
        Assert.Equal(1, ico.Type);          // 1 = icon, 2 = cursor
        Assert.Equal(3, ico.Count);
        Assert.Equal(new[] { 16, 32, 48 }, ico.Sizes);
    }

    [Fact]
    public void Images_are_kept_in_the_order_they_were_given()
    {
        Assert.Equal(new[] { 48, 16, 32 }, IcoFile.Read(Write(48, 16, 32)).Sizes);
    }

    [Fact]
    public void Every_entry_is_truecolor_with_no_palette()
    {
        var ico = IcoFile.Read(Write(16, 32));

        Assert.All(ico.Entries, entry =>
        {
            Assert.Equal(0, entry.PaletteSize);   // 0 = not palettized
            Assert.Equal(1, entry.Planes);
            Assert.Equal(32, entry.BitsPerPixel);
        });
    }

    [Fact]
    public void A_256_pixel_image_is_recorded_as_a_zero()
    {
        // One byte per dimension, so 256 cannot be written literally -- 0 is the
        // agreed stand-in, and getting it wrong makes the largest icon a 0x0.
        byte[] bytes = Write(256);

        Assert.Equal(0, (int)bytes[IcoFile.DirectorySize]);       // width byte
        Assert.Equal(0, (int)bytes[IcoFile.DirectorySize + 1]);   // height byte
        Assert.Equal(256, IcoFile.Read(bytes).Entries[0].Width);
    }

    [Fact]
    public void Image_data_starts_after_the_directory_and_every_entry_follows_the_last()
    {
        // An offset table that drifts by one entry is an icon that draws
        // garbage, and nothing anywhere reports it.
        byte[] bytes = Write(16, 32, 48);
        var ico = IcoFile.Read(bytes);

        int expected = IcoFile.DirectorySize + IcoFile.EntrySize * ico.Count;
        foreach (var entry in ico.Entries)
        {
            Assert.Equal(expected, entry.Offset);
            expected += entry.ByteCount;
        }

        Assert.Equal(bytes.Length, expected);
    }

    // -------------------------------------------------- image encoding --

    [Fact]
    public void An_ordinary_size_is_a_bitmap_whose_header_covers_the_mask_as_well()
    {
        var entry = IcoFile.Read(Write(32)).Entries[0];
        var header = entry.BitmapHeader;

        Assert.False(entry.IsPng);
        Assert.Equal(40, header.HeaderSize);      // BITMAPINFOHEADER
        Assert.Equal(32, header.Width);
        Assert.Equal(64, header.Height);          // colour bitmap + AND mask
        Assert.Equal(1, header.Planes);
        Assert.Equal(32, header.BitsPerPixel);
        Assert.Equal(0, header.Compression);      // BI_RGB
    }

    [Fact]
    public void The_bitmap_entry_carries_its_colour_rows_and_a_mask()
    {
        var entry = IcoFile.Read(Write(16)).Entries[0];

        int colorBytes = 16 * 16 * 4;
        int maskBytes = (16 + 31) / 32 * 4 * 16;
        Assert.Equal(40 + colorBytes + maskBytes, entry.ByteCount);
    }

    [Fact]
    public void The_bitmap_rows_run_bottom_up()
    {
        // Icons are stored upside down. Getting this wrong is not an error --
        // it is an icon drawn upside down, which is why it is worth a test.
        using var bitmap = new Bitmap(2, 2);
        bitmap.SetPixel(0, 0, Color.FromArgb(255, 255, 0, 0));   // top-left: red
        bitmap.SetPixel(0, 1, Color.FromArgb(255, 0, 0, 255));   // bottom-left: blue

        using var stream = new MemoryStream();
        IcoWriter.Write(stream, new[] { bitmap });
        var entry = IcoFile.Read(stream.ToArray()).Entries[0];

        // First pixel of the first stored row is the bitmap's bottom-left, BGRA.
        Assert.Equal(255, (int)entry.Data[40]);      // blue channel
        Assert.Equal(0, (int)entry.Data[41]);        // green
        Assert.Equal(0, (int)entry.Data[42]);        // red
    }

    [Fact]
    public void The_largest_size_is_a_png()
    {
        // The only form the shell takes at 256 without a quarter-megabyte of
        // uncompressed pixels.
        var entries = IcoFile.Read(Write(48, 256)).Entries;

        Assert.False(entries[0].IsPng);
        Assert.True(entries[1].IsPng);
        Assert.True(entries[1].ByteCount < 256 * 256 * 4, "a 256 px entry was written uncompressed");
    }

    // ---------------------------------------------------- what it is for --

    [Fact]
    public void What_it_writes_is_something_the_BCL_reads_back()
    {
        using var stream = new MemoryStream(Write(16, 32, 48));

        using var icon = new Icon(stream);
        using var small = new Icon(icon, 16, 16);
        using var large = new Icon(icon, 48, 48);

        Assert.Equal(16, small.Width);
        Assert.Equal(48, large.Width);
    }

    [Fact]
    public void The_images_it_was_given_are_still_the_callers()
    {
        // Documented, and BrandIcon.BuildIcon relies on it: it disposes them
        // itself in a finally.
        using var bitmap = Bitmap(32);
        using var stream = new MemoryStream();

        IcoWriter.Write(stream, new[] { bitmap });

        Assert.Equal(32, bitmap.Width);   // throws if Write disposed it
    }

    [Fact]
    public void The_stream_is_left_open_for_the_caller()
    {
        using var stream = new MemoryStream();

        IcoWriter.Write(stream, new[] { Bitmap(16) });

        Assert.True(stream.CanWrite, "the stream was closed under the caller");
    }

    // -------------------------------------------------------- refusals --

    [Fact]
    public void An_icon_with_no_images_is_refused()
    {
        Assert.Throws<ArgumentException>(() => IcoWriter.Write(new MemoryStream(), Array.Empty<Bitmap>()));
    }

    [Fact]
    public void A_bitmap_that_is_not_square_is_refused()
    {
        using var bitmap = new Bitmap(32, 16);

        var error = Assert.Throws<ArgumentException>(() => IcoWriter.Write(new MemoryStream(), new[] { bitmap }));
        Assert.Contains("square", error.Message);
    }

    [Fact]
    public void A_bitmap_larger_than_the_format_allows_is_refused()
    {
        using var bitmap = new Bitmap(512, 512);

        var error = Assert.Throws<ArgumentException>(() => IcoWriter.Write(new MemoryStream(), new[] { bitmap }));
        Assert.Contains("256", error.Message);
    }
}
