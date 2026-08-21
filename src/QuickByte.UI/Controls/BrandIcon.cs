using System.Drawing;
using System.Drawing.Drawing2D;

namespace QuickByte.UI.Controls;

/// <summary>
/// The QuickByte product mark: a rounded accent tile carrying a download arrow
/// over a three-segment tray — the segments being the one visual cue that this
/// is a <em>multi-connection</em> downloader rather than a generic one.
///
/// Drawn with GDI+ at whatever size is asked for, like everything in
/// <see cref="IconFactory"/>, so the logo stays resolution-independent and the
/// repo stays free of image assets. The one exception is
/// <c>Assets/quickbyte.ico</c>, which has to exist as a real file for the
/// compiler to stamp into the executable's Win32 resources — that file is
/// generated from this same code (see <see cref="IcoWriter"/>), so the shell
/// icon and the window icon can never drift apart.
/// </summary>
public static class BrandIcon
{
    /// <summary>
    /// Sizes baked into the multi-resolution icon. Windows picks from these for
    /// the title bar (16), the taskbar (24-32), Alt+Tab (32-48) and Explorer's
    /// larger views, so supplying each one avoids the mush of a single bitmap
    /// being scaled down.
    /// </summary>
    public static readonly int[] IconSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    private static Icon? _appIcon;

    /// <summary>
    /// The shared multi-resolution window icon, built once and reused by every
    /// form. Cached because it lives for the life of the process anyway, and
    /// because handing each window its own <see cref="Icon"/> would leak one
    /// GDI handle per window.
    /// </summary>
    public static Icon App => _appIcon ??= BuildIcon();

    private static Icon BuildIcon()
    {
        // Only the sizes Windows actually asks a window for — the 128/256 entries
        // matter for the shell (i.e. the .ico file), not for a title bar.
        var bitmaps = new[] { 16, 20, 24, 32, 40, 48, 64 }.Select(CreateBitmap).ToArray();
        try
        {
            using var stream = new MemoryStream();
            IcoWriter.Write(stream, bitmaps);
            stream.Position = 0;
            return new Icon(stream);
        }
        finally
        {
            foreach (var bitmap in bitmaps) bitmap.Dispose();
        }
    }

    /// <summary>Renders the mark into a fresh transparent bitmap.</summary>
    public static Bitmap CreateBitmap(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        Draw(g, size);
        return bitmap;
    }

    /// <summary>
    /// Paints the mark at <paramref name="size"/> into the current origin of
    /// <paramref name="g"/>. Everything is expressed as a fraction of the size,
    /// so the proportions hold from a 16 px title bar up to a 256 px shell tile.
    /// </summary>
    public static void Draw(Graphics g, int size)
    {
        float f(float fraction) => size * fraction;

        // Below ~24 px the shadow and the tray gaps land on sub-pixel boundaries
        // and turn into grey mush, so the small variant drops both.
        bool detailed = size >= 24;

        float inset = f(0.045f);
        var tile = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);

        using (var tilePath = Theme.RoundedRect(tile, f(0.225f)))
        {
            using (var fill = new LinearGradientBrush(
                       new RectangleF(tile.X - 1, tile.Y - 1, tile.Width + 2, tile.Height + 2),
                       Theme.AccentLight, Theme.AccentDark, LinearGradientMode.ForwardDiagonal))
                g.FillPath(fill, tilePath);

            // A gloss over the top half keeps the tile from reading as a flat
            // block of blue at large sizes without tinting the mark itself. The
            // blend has to reach full transparency *before* the rectangle ends —
            // a gradient that is still faintly visible at its last row leaves a
            // hard seam straight across the tile.
            var gloss = new RectangleF(tile.X, tile.Y, tile.Width, tile.Height * 0.60f);
            using (var glossBrush = new LinearGradientBrush(
                       new RectangleF(gloss.X, gloss.Y - 1, gloss.Width, gloss.Height + 2),
                       Color.FromArgb(58, 255, 255, 255), Color.FromArgb(0, 255, 255, 255),
                       LinearGradientMode.Vertical))
            {
                glossBrush.WrapMode = WrapMode.TileFlipXY;
                glossBrush.Blend = new Blend
                {
                    Positions = new[] { 0f, 0.45f, 0.85f, 1f },
                    Factors = new[] { 0f, 0.70f, 1f, 1f }
                };

                var clip = g.Clip;
                g.SetClip(tilePath, CombineMode.Intersect);
                g.FillRectangle(glossBrush, gloss);
                g.Clip = clip;
            }

            using var border = new Pen(Color.FromArgb(64, 0, 30, 60), Math.Max(1f, f(0.02f)));
            g.DrawPath(border, tilePath);
        }

        if (detailed)
        {
            using var shadow = new SolidBrush(Color.FromArgb(48, 0, 24, 48));
            DrawMark(g, size, shadow, f(0.022f), detailed);
        }

        using var white = new SolidBrush(Color.White);
        DrawMark(g, size, white, 0f, detailed);
    }

    /// <summary>
    /// The arrow and its tray, painted in one brush so the drop shadow is the
    /// same call with an offset rather than a second, drifting definition.
    /// </summary>
    private static void DrawMark(Graphics g, int size, Brush brush, float offset, bool detailed)
    {
        float f(float fraction) => size * fraction + offset;

        using (var stem = Theme.RoundedRect(
                   new RectangleF(f(0.422f), f(0.210f), size * 0.156f, size * 0.290f), size * 0.055f))
            g.FillPath(brush, stem);

        g.FillPolygon(brush, new PointF[]
        {
            new(f(0.275f), f(0.435f)),
            new(f(0.725f), f(0.435f)),
            new(f(0.500f), f(0.705f))
        });

        float trayTop = f(0.775f);
        float trayHeight = size * 0.085f;
        float trayRadius = trayHeight / 2f;

        if (!detailed)
        {
            using var tray = Theme.RoundedRect(
                new RectangleF(f(0.250f), trayTop, size * 0.500f, trayHeight), trayRadius);
            g.FillPath(brush, tray);
            return;
        }

        // Three segments, not one bar: the mark's only nod to what the app
        // actually does differently — split a file across parallel connections.
        const float gap = 0.028f;
        float segmentWidth = (0.500f - gap * 2) / 3f;
        for (int i = 0; i < 3; i++)
        {
            float x = f(0.250f + i * (segmentWidth + gap));
            using var segment = Theme.RoundedRect(
                new RectangleF(x, trayTop, size * segmentWidth, trayHeight), trayRadius);
            g.FillPath(brush, segment);
        }
    }
}
