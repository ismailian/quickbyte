using System.Drawing;
using System.Reflection;
using QuickByte.Core.Helpers;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Tests;

/// <summary>
/// Every icon in the app is GDI+ drawing code rather than an image file, which
/// keeps the repository free of assets and the icons sharp at any DPI — and
/// means each one is a small program that can throw.
///
/// Nothing here judges how they look. What it checks is that each draws, at the
/// sizes the UI actually asks for, into a bitmap of the requested size, and
/// puts something in it: a path built with a negative radius (which is what a
/// hardcoded inset does at 16 px) throws at paint time, in a toolbar, on a
/// user's machine.
/// </summary>
public sealed class IconFactoryTests
{
    /// <summary>Every icon that takes nothing but a size — the toolbar and menu set.</summary>
    public static IEnumerable<object[]> SimpleIcons() =>
        typeof(IconFactory).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method =>
                method.ReturnType == typeof(Bitmap) &&
                method.GetParameters().Length == 1 &&
                method.GetParameters()[0].ParameterType == typeof(int))
            .Select(method => new object[] { method.Name })
            .OrderBy(row => (string)row[0]);

    public static IEnumerable<object[]> Categories() =>
        Enum.GetValues<FileCategory>().Select(category => new object[] { category });

    /// <summary>16 px is a list cell, 24 a toolbar button, 48 a dialog glyph.</summary>
    private static readonly int[] Sizes = { 16, 24, 48 };

    [Theory]
    [MemberData(nameof(SimpleIcons))]
    public void An_icon_draws_at_every_size_the_app_asks_for(string name)
    {
        var method = typeof(IconFactory).GetMethod(name, new[] { typeof(int) })!;

        foreach (int size in Sizes)
        {
            using var bitmap = (Bitmap)method.Invoke(null, new object[] { size })!;

            Assert.Equal(size, bitmap.Width);
            Assert.Equal(size, bitmap.Height);
            Assert.True(HasInk(bitmap), $"{name}({size}) drew nothing");
        }
    }

    [Theory]
    [MemberData(nameof(Categories))]
    public void Every_file_category_has_a_badge_and_a_colour(FileCategory category)
    {
        // The list draws one of these per row, so a category added to Core
        // without a case here is a blank cell rather than a compile error.
        using var bitmap = IconFactory.CategoryIcon(category, 16);

        Assert.Equal(16, bitmap.Width);
        Assert.True(HasInk(bitmap), $"{category} drew nothing");
        Assert.NotEqual(Color.Empty, IconFactory.CategoryColor(category));
    }

    [Fact]
    public void A_status_dot_is_drawn_in_the_colour_it_is_given()
    {
        using var bitmap = IconFactory.StatusDot(Color.Red, 16);

        Assert.Equal(16, bitmap.Width);
        Assert.True(HasInk(bitmap));
    }

    [Fact]
    public void A_folder_can_be_tinted_or_left_alone()
    {
        using var plain = IconFactory.Folder(16);
        using var tinted = IconFactory.Folder(16, Color.Red);

        Assert.True(HasInk(plain));
        Assert.True(HasInk(tinted));
    }

    [Fact]
    public void The_shared_colours_are_the_themes()
    {
        // IconFactory exposes them so icon code does not reach for a raw Color;
        // they must stay the same objects the rest of the chrome uses.
        Assert.Equal(Theme.Accent, IconFactory.Accent);
        Assert.Equal(Theme.Success, IconFactory.Green);
        Assert.Equal(Theme.Danger, IconFactory.Red);
        Assert.Equal(Theme.Warning, IconFactory.Amber);
    }

    private static bool HasInk(Bitmap bitmap)
    {
        for (int x = 0; x < bitmap.Width; x++)
            for (int y = 0; y < bitmap.Height; y++)
                if (bitmap.GetPixel(x, y).A != 0) return true;

        return false;
    }
}
