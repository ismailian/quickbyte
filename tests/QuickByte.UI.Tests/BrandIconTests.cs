using System.Drawing;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Tests;

/// <summary>
/// The product mark, and the one image file in the repository.
///
/// Everything else is drawn at run time, but the compiler needs a real .ico to
/// stamp into the executable's Win32 resources — so <c>Assets/quickbyte.ico</c>
/// is generated from <see cref="BrandIcon"/> through <see cref="IcoWriter"/>,
/// and is the only thing here that can silently fall out of step with the code.
/// Nothing rebuilds it: change the sizes and the shell keeps showing the old
/// icon, while the title bar (built from the same code at run time) shows the
/// new one. That drift is what the last test is for.
/// </summary>
public sealed class BrandIconTests
{
    /// <summary>The checked-in icon, copied beside the tests by the project file.</summary>
    private static string IconPath => Path.Combine(AppContext.BaseDirectory, "quickbyte.ico");

    [Fact]
    public void The_sizes_are_a_sensible_ascending_set()
    {
        var sizes = BrandIcon.IconSizes;

        Assert.Equal(sizes.OrderBy(size => size), sizes);
        Assert.Equal(sizes.Distinct(), sizes);
        Assert.All(sizes, size => Assert.InRange(size, 1, 256));
    }

    [Fact]
    public void The_sizes_Windows_actually_asks_for_are_among_them()
    {
        // 16 is the title bar, 32 the taskbar and Alt+Tab, 256 the shell's
        // largest view. Supplying each is what avoids one bitmap being scaled.
        Assert.Contains(16, BrandIcon.IconSizes);
        Assert.Contains(32, BrandIcon.IconSizes);
        Assert.Contains(256, BrandIcon.IconSizes);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(256)]
    public void The_mark_is_drawn_at_whatever_size_is_asked_for(int size)
    {
        using var bitmap = BrandIcon.CreateBitmap(size);

        Assert.Equal(size, bitmap.Width);
        Assert.Equal(size, bitmap.Height);
    }

    [Fact]
    public void The_mark_is_a_shape_on_transparency_rather_than_a_filled_square()
    {
        // The tile is rounded, so the corners have to stay clear -- an icon
        // drawn onto an opaque background is a square blob on a dark taskbar.
        using var bitmap = BrandIcon.CreateBitmap(32);

        var pixels = Enumerable.Range(0, 32)
            .SelectMany(x => Enumerable.Range(0, 32).Select(y => bitmap.GetPixel(x, y)))
            .ToList();

        Assert.Contains(pixels, pixel => pixel.A == 0);
        Assert.Contains(pixels, pixel => pixel.A == 255);
    }

    [Fact]
    public void The_window_icon_carries_more_than_one_size()
    {
        // One Icon shared by every form -- if it held a single bitmap, Windows
        // would scale it for the title bar and the result is visibly soft.
        using var small = new Icon(BrandIcon.App, 16, 16);
        using var large = new Icon(BrandIcon.App, 48, 48);

        Assert.Equal(16, small.Width);
        Assert.Equal(48, large.Width);
    }

    [Fact]
    public void The_window_icon_is_built_once_and_shared()
    {
        // Cached deliberately: a copy per window is a GDI handle per window.
        Assert.Same(BrandIcon.App, BrandIcon.App);
    }

    [Fact]
    public void The_checked_in_icon_still_carries_the_sizes_the_code_draws()
    {
        // The drift this whole class is about. If this fails, the .ico is stale:
        // regenerate it by writing BrandIcon.IconSizes.Select(BrandIcon.CreateBitmap)
        // through IcoWriter.Write over src/QuickByte.UI/Assets/quickbyte.ico.
        var ico = IcoFile.Read(IconPath);

        Assert.Equal(BrandIcon.IconSizes, ico.Sizes);
    }

    [Fact]
    public void The_checked_in_icon_is_a_well_formed_icon_directory()
    {
        var ico = IcoFile.Read(IconPath);

        Assert.Equal(0, ico.Reserved);
        Assert.Equal(1, ico.Type);
        Assert.Equal(ico.Count, ico.Entries.Count);
        Assert.All(ico.Entries, entry =>
        {
            Assert.Equal(entry.Width, entry.Height);
            Assert.Equal(32, entry.BitsPerPixel);
            Assert.True(entry.ByteCount > 0);
        });
    }

    [Fact]
    public void The_largest_entry_of_the_checked_in_icon_is_a_png()
    {
        // Anything else at 256 px is a quarter of a megabyte of raw pixels, and
        // the shell is the only consumer of that size.
        var ico = IcoFile.Read(IconPath);

        Assert.True(ico.Entries.Single(entry => entry.Width == 256).IsPng);
    }
}
