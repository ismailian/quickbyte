using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using QuickByte.Core.Enums;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Tests;

/// <summary>
/// The light and dark palettes, and choosing between them.
///
/// Nobody notices a palette that is subtly unreadable until they squint at it, so
/// the contrast of the pairs that carry text is held to WCAG ratios here — a colour
/// tweaked later cannot quietly make the dark theme's muted text illegible. Every
/// test leaves the light palette in place: <see cref="Theme"/> is static, and the
/// other tests draw with it.
/// </summary>
public sealed class ThemeTests : IDisposable
{
    private const string ScratchRoot = @"Software\QuickByte.Tests";
    private readonly string _previousPersonalizePath = Theme.PersonalizeKeyPath;

    public void Dispose()
    {
        Theme.Use(false);
        Theme.PersonalizeKeyPath = _previousPersonalizePath;
        try { Registry.CurrentUser.DeleteSubKeyTree(ScratchRoot, throwOnMissingSubKey: false); }
        catch { /* best-effort */ }
    }

    // ------------------------------------------------------------- choosing --

    [Theory]
    [InlineData(AppTheme.Light, true, false)]
    [InlineData(AppTheme.Dark, false, true)]
    [InlineData(AppTheme.System, true, true)]
    [InlineData(AppTheme.System, false, false)]
    public void The_preference_decides_unless_it_defers_to_Windows(AppTheme preference, bool windowsDark, bool expected)
    {
        Assert.Equal(expected, Theme.Resolve(preference, windowsDark));
    }

    [Fact]
    public void A_Windows_that_does_not_say_gets_the_light_palette_the_app_always_had()
    {
        Assert.False(Theme.Resolve(AppTheme.System, null));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void Windows_app_mode_is_read_from_its_own_setting(int appsUseLightTheme, bool expectedDark)
    {
        Theme.PersonalizeKeyPath = $@"{ScratchRoot}\{Guid.NewGuid():N}\Personalize";
        using (var key = Registry.CurrentUser.CreateSubKey(Theme.PersonalizeKeyPath, writable: true)!)
            key.SetValue("AppsUseLightTheme", appsUseLightTheme, RegistryValueKind.DWord);

        Assert.Equal(expectedDark, Theme.WindowsPrefersDark());
    }

    [Fact]
    public void A_missing_app_mode_setting_is_no_answer_at_all()
    {
        Theme.PersonalizeKeyPath = $@"{ScratchRoot}\{Guid.NewGuid():N}\Personalize";

        Assert.Null(Theme.WindowsPrefersDark());
    }

    [Fact]
    public void Every_colour_follows_the_palette_in_use_including_the_icons()
    {
        Theme.Use(true);
        Assert.True(Theme.IsDark);
        Assert.Equal(Palette.Dark.Surface, Theme.Surface);
        Assert.Equal(Theme.Accent, IconFactory.Accent);

        Theme.Use(false);
        Assert.False(Theme.IsDark);
        Assert.Equal(Palette.Light.Surface, Theme.Surface);
        Assert.Equal(Theme.Accent, IconFactory.Accent);
    }

    // ------------------------------------------------------------ contrast --

    public static IEnumerable<object[]> BothPalettes() => new[]
    {
        new object[] { "light" }, new object[] { "dark" }
    };

    private static Palette Named(string name) => name == "dark" ? Palette.Dark : Palette.Light;

    [Theory]
    [MemberData(nameof(BothPalettes))]
    public void Body_text_is_comfortably_readable_on_every_background_it_sits_on(string name)
    {
        var p = Named(name);

        Assert.True(Contrast(p.Text, p.Surface) >= 7, $"{name} text/surface");
        Assert.True(Contrast(p.Text, p.Window) >= 7, $"{name} text/window");
        Assert.True(Contrast(p.Text, p.HeaderBack) >= 7, $"{name} text/header");
        Assert.True(Contrast(p.TextOnAccent, p.RowSelected) >= 4.5, $"{name} selected row");
    }

    [Fact]
    public void The_dark_palette_keeps_its_secondary_text_and_status_colours_readable()
    {
        var p = Palette.Dark;

        Assert.True(Contrast(p.TextMuted, p.Surface) >= 4.5, "muted");
        Assert.True(Contrast(p.Accent, p.Surface) >= 4.5, "accent text");
        Assert.True(Contrast(p.Success, p.Surface) >= 4.5, "success");
        Assert.True(Contrast(p.Warning, p.Surface) >= 4.5, "warning");
        Assert.True(Contrast(p.Danger, p.Surface) >= 4.5, "danger");
        Assert.True(Contrast(p.TextOnAccent, p.Accent) >= 3, "primary button");
    }

    [Fact]
    public void The_dark_palette_is_actually_dark()
    {
        Assert.True(Luminance(Palette.Dark.Surface) < 0.05);
        Assert.True(Luminance(Palette.Dark.Text) > 0.7);
    }

    // ------------------------------------------------------------- attaching --

    [Fact]
    public void Attaching_on_the_light_palette_changes_nothing()
    {
        using var form = new Form();
        var spinner = new NumericUpDown();
        form.Controls.Add(spinner);
        var before = spinner.BackColor;

        Theme.Attach(form);

        Assert.Equal(before, spinner.BackColor);
    }

    [Fact]
    public void Attaching_on_the_dark_palette_darkens_controls_left_in_Windows_colours_and_no_others()
    {
        Theme.Use(true);
        using var form = new Form();
        var spinner = new NumericUpDown();
        var chosen = new TextBox { BackColor = Color.Goldenrod };
        form.Controls.Add(spinner);
        form.Controls.Add(chosen);

        Theme.Attach(form);

        Assert.Equal(Theme.Surface, spinner.BackColor);
        Assert.Equal(Theme.Text, spinner.ForeColor);
        Assert.Equal(Color.Goldenrod, chosen.BackColor);
    }

    [Fact]
    public void A_control_added_after_attaching_is_darkened_too()
    {
        Theme.Use(true);
        using var form = new Form();
        Theme.Attach(form);

        var late = new ComboBox();
        form.Controls.Add(late);

        Assert.Equal(Theme.Surface, late.BackColor);
    }

    // ------------------------------------------------------------- plumbing --

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(int v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
