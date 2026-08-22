using System.Drawing;
using System.Drawing.Drawing2D;
using QuickByte.Core.Helpers;

namespace QuickByte.UI.Controls;

/// <summary>
/// Draws every small icon the UI needs (toolbar buttons, sidebar tree nodes,
/// file-type badges in the downloads list, dialog glyphs) with plain GDI+
/// vector shapes. Keeps the app a single, self-contained assembly with no
/// image resources to manage while still giving every window the iconography
/// an IDM-style UI needs. Colors come from <see cref="Theme"/> so the icons
/// stay in step with the rest of the chrome.
/// </summary>
public static class IconFactory
{
    public static readonly Color Accent = Theme.Accent;
    public static readonly Color AccentDark = Theme.AccentDark;
    public static readonly Color Green = Theme.Success;
    public static readonly Color Red = Theme.Danger;
    public static readonly Color Amber = Theme.Warning;
    public static readonly Color Gray = Color.FromArgb(122, 133, 146);

    private static Bitmap Canvas(int size, Action<Graphics> draw)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        draw(g);
        return bmp;
    }

    // ------------------------------------------------------------ Toolbar --

    public static Bitmap AddUrl(int size = 24) => Canvas(size, g =>
    {
        float inset = size * 0.08f;
        var globe = new RectangleF(inset, inset, size * 0.7f, size * 0.7f);
        using (var fill = new LinearGradientBrush(globe, Theme.AccentLight, Theme.Accent, LinearGradientMode.ForwardDiagonal))
            g.FillEllipse(fill, globe);

        using (var pen = new Pen(Color.FromArgb(140, Color.White), Math.Max(1f, size * 0.045f)))
        {
            g.DrawLine(pen, globe.Left + globe.Width * 0.06f, globe.Top + globe.Height / 2,
                            globe.Right - globe.Width * 0.06f, globe.Top + globe.Height / 2);
            g.DrawEllipse(pen, globe.X + globe.Width * 0.28f, globe.Y, globe.Width * 0.44f, globe.Height);
        }

        DrawBadge(g, size, Theme.Success, plus: true);
    });

    public static Bitmap Resume(int size = 24) => Canvas(size, g =>
    {
        var circle = new RectangleF(size * 0.06f, size * 0.06f, size * 0.88f, size * 0.88f);
        using (var fill = new LinearGradientBrush(circle, Theme.SuccessLight, Theme.Success, LinearGradientMode.Vertical))
            g.FillEllipse(fill, circle);
        using var brush = new SolidBrush(Color.White);
        g.FillPolygon(brush, new PointF[]
        {
            new(size * 0.40f, size * 0.29f),
            new(size * 0.40f, size * 0.71f),
            new(size * 0.73f, size * 0.50f)
        });
    });

    public static Bitmap Pause(int size = 24) => Canvas(size, g =>
    {
        var circle = new RectangleF(size * 0.06f, size * 0.06f, size * 0.88f, size * 0.88f);
        using (var fill = new LinearGradientBrush(circle, ControlPaint.Light(Theme.Warning, 0.3f), Theme.Warning, LinearGradientMode.Vertical))
            g.FillEllipse(fill, circle);
        using var brush = new SolidBrush(Color.White);
        float barWidth = size * 0.11f;
        g.FillRectangle(brush, size * 0.37f, size * 0.30f, barWidth, size * 0.40f);
        g.FillRectangle(brush, size * 0.53f, size * 0.30f, barWidth, size * 0.40f);
    });

    public static Bitmap Stop(int size = 24) => Canvas(size, g =>
    {
        var circle = new RectangleF(size * 0.06f, size * 0.06f, size * 0.88f, size * 0.88f);
        using (var fill = new LinearGradientBrush(circle, ControlPaint.Light(Theme.Danger, 0.3f), Theme.Danger, LinearGradientMode.Vertical))
            g.FillEllipse(fill, circle);
        using var brush = new SolidBrush(Color.White);
        using var path = Theme.RoundedRect(new RectangleF(size * 0.34f, size * 0.34f, size * 0.32f, size * 0.32f), size * 0.05f);
        g.FillPath(brush, path);
    });

    public static Bitmap StopAll(int size = 24) => Canvas(size, g =>
    {
        using var brush = new SolidBrush(Theme.Danger);
        using var light = new SolidBrush(ControlPaint.Light(Theme.Danger, 0.45f));
        using var back = Theme.RoundedRect(new RectangleF(size * 0.14f, size * 0.22f, size * 0.34f, size * 0.56f), size * 0.06f);
        using var front = Theme.RoundedRect(new RectangleF(size * 0.52f, size * 0.22f, size * 0.34f, size * 0.56f), size * 0.06f);
        g.FillPath(light, back);
        g.FillPath(brush, front);
    });

    public static Bitmap Delete(int size = 24) => Canvas(size, g =>
    {
        using var body = new SolidBrush(Color.FromArgb(224, 92, 92));
        using var lid = new SolidBrush(Color.FromArgb(196, 70, 70));
        using var linePen = new Pen(Color.FromArgb(200, Color.White), Math.Max(1f, size * 0.05f));

        using (var lidPath = Theme.RoundedRect(new RectangleF(size * 0.16f, size * 0.20f, size * 0.68f, size * 0.11f), size * 0.045f))
            g.FillPath(lid, lidPath);
        g.FillRectangle(lid, size * 0.40f, size * 0.12f, size * 0.20f, size * 0.08f);

        using (var bodyPath = Theme.RoundedRect(new RectangleF(size * 0.24f, size * 0.32f, size * 0.52f, size * 0.54f), size * 0.07f))
            g.FillPath(body, bodyPath);

        for (int i = 0; i < 3; i++)
        {
            float x = size * (0.36f + i * 0.14f);
            g.DrawLine(linePen, x, size * 0.44f, x, size * 0.74f);
        }
    });

    public static Bitmap DeleteCompleted(int size = 24) => Canvas(size, g =>
    {
        using var body = new SolidBrush(Color.FromArgb(224, 92, 92));
        using var lid = new SolidBrush(Color.FromArgb(196, 70, 70));
        using (var lidPath = Theme.RoundedRect(new RectangleF(size * 0.08f, size * 0.20f, size * 0.60f, size * 0.10f), size * 0.04f))
            g.FillPath(lid, lidPath);
        using (var bodyPath = Theme.RoundedRect(new RectangleF(size * 0.15f, size * 0.31f, size * 0.46f, size * 0.50f), size * 0.06f))
            g.FillPath(body, bodyPath);

        DrawBadge(g, size, Theme.Success, plus: false);
    });

    public static Bitmap Settings(int size = 24) => Canvas(size, g =>
    {
        var center = new PointF(size / 2f, size / 2f);
        float outerR = size * 0.42f, midR = size * 0.28f, innerR = size * 0.12f;

        using var teeth = new SolidBrush(Color.FromArgb(108, 122, 137));
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4;
            var toothCenter = new PointF(
                center.X + (float)(Math.Cos(angle) * (outerR - size * 0.06f)),
                center.Y + (float)(Math.Sin(angle) * (outerR - size * 0.06f)));
            var tooth = new RectangleF(toothCenter.X - size * 0.075f, toothCenter.Y - size * 0.075f, size * 0.15f, size * 0.15f);
            g.FillEllipse(teeth, tooth);
        }

        using var ring = new SolidBrush(Color.FromArgb(88, 102, 118));
        g.FillEllipse(ring, center.X - midR, center.Y - midR, midR * 2, midR * 2);
        using var hole = new SolidBrush(Color.White);
        g.FillEllipse(hole, center.X - innerR, center.Y - innerR, innerR * 2, innerR * 2);
    });

    public static Bitmap Retry(int size = 24) => Canvas(size, g =>
    {
        using var pen = new Pen(Theme.Accent, Math.Max(1.6f, size * 0.11f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var arc = new RectangleF(size * 0.16f, size * 0.16f, size * 0.68f, size * 0.68f);
        g.DrawArc(pen, arc, 40, 280);
        using var brush = new SolidBrush(Theme.Accent);
        g.FillPolygon(brush, new PointF[]
        {
            new(size * 0.80f, size * 0.10f),
            new(size * 0.86f, size * 0.44f),
            new(size * 0.52f, size * 0.36f)
        });
    });

    public static Bitmap OpenFolder(int size = 24) => Canvas(size, g =>
    {
        using var backBrush = new SolidBrush(Color.FromArgb(238, 186, 78));
        using var frontBrush = new SolidBrush(Color.FromArgb(252, 208, 108));
        g.FillRectangle(backBrush, size * 0.10f, size * 0.22f, size * 0.42f, size * 0.14f);
        using (var back = Theme.RoundedRect(new RectangleF(size * 0.10f, size * 0.28f, size * 0.80f, size * 0.50f), size * 0.06f))
            g.FillPath(backBrush, back);
        using (var front = Theme.RoundedRect(new RectangleF(size * 0.16f, size * 0.40f, size * 0.78f, size * 0.38f), size * 0.06f))
            g.FillPath(frontBrush, front);
    });

    public static Bitmap OpenFile(int size = 24) => Canvas(size, g =>
    {
        using var sheet = new SolidBrush(Color.White);
        using var border = new Pen(Theme.BorderStrong, Math.Max(1f, size * 0.055f));
        var body = new RectangleF(size * 0.22f, size * 0.10f, size * 0.50f, size * 0.68f);
        using (var path = Theme.RoundedRect(body, size * 0.05f))
        {
            g.FillPath(sheet, path);
            g.DrawPath(border, path);
        }
        using var linePen = new Pen(Theme.Accent, Math.Max(1f, size * 0.05f));
        for (int i = 0; i < 3; i++)
        {
            float y = body.Y + body.Height * (0.28f + i * 0.20f);
            g.DrawLine(linePen, body.X + body.Width * 0.18f, y, body.Right - body.Width * 0.18f, y);
        }
        DrawBadge(g, size, Theme.Accent, plus: false, arrow: true);
    });

    public static Bitmap CheckCircle(int size = 48) => Canvas(size, g =>
    {
        var circle = new RectangleF(size * 0.06f, size * 0.06f, size * 0.88f, size * 0.88f);
        using (var fill = new LinearGradientBrush(circle, Theme.SuccessLight, Theme.Success, LinearGradientMode.Vertical))
            g.FillEllipse(fill, circle);
        using var pen = new Pen(Color.White, Math.Max(2f, size * 0.10f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new PointF[]
        {
            new(size * 0.29f, size * 0.52f),
            new(size * 0.44f, size * 0.67f),
            new(size * 0.72f, size * 0.34f)
        });
    });

    public static Bitmap Properties(int size = 24) => Canvas(size, g =>
    {
        using var pen = new Pen(Theme.Accent, Math.Max(1.4f, size * 0.09f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var circle = new RectangleF(size * 0.12f, size * 0.12f, size * 0.76f, size * 0.76f);
        g.DrawEllipse(pen, circle);
        using var brush = new SolidBrush(Theme.Accent);
        g.FillEllipse(brush, size * 0.44f, size * 0.24f, size * 0.13f, size * 0.13f);
        g.FillRectangle(brush, size * 0.44f, size * 0.44f, size * 0.13f, size * 0.32f);
    });

    public static Bitmap Queue(int size = 24) => Canvas(size, g =>
    {
        using var brush = new SolidBrush(Theme.Accent);
        using var dim = new SolidBrush(Color.FromArgb(120, Theme.Accent));
        for (int i = 0; i < 3; i++)
        {
            float y = size * (0.24f + i * 0.24f);
            g.FillEllipse(brush, size * 0.16f, y, size * 0.12f, size * 0.12f);
            using var rowPath = Theme.RoundedRect(new RectangleF(size * 0.36f, y + size * 0.015f, size * 0.48f, size * 0.09f), size * 0.045f);
            g.FillPath(i == 0 ? brush : dim, rowPath);
        }
    });

    public static Bitmap Scheduler(int size = 24) => Canvas(size, g =>
    {
        using var pen = new Pen(Theme.Accent, Math.Max(1.4f, size * 0.09f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var clock = new RectangleF(size * 0.14f, size * 0.14f, size * 0.72f, size * 0.72f);
        g.DrawEllipse(pen, clock);
        var center = new PointF(clock.X + clock.Width / 2, clock.Y + clock.Height / 2);
        g.DrawLine(pen, center, new PointF(center.X, center.Y - clock.Height * 0.28f));
        g.DrawLine(pen, center, new PointF(center.X + clock.Width * 0.22f, center.Y));
    });

    /// <summary>Power glyph for the tray menu's Exit entry — the one command that really closes the app.</summary>
    public static Bitmap Exit(int size = 24) => Canvas(size, g =>
    {
        using var pen = new Pen(Theme.Danger, Math.Max(1.4f, size * 0.11f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        // The ring is left open at the top so the stem reads as breaking through
        // it rather than sitting on top of a closed circle.
        var ring = new RectangleF(size * 0.18f, size * 0.20f, size * 0.64f, size * 0.64f);
        g.DrawArc(pen, ring, -60, 300);
        g.DrawLine(pen, size * 0.50f, size * 0.14f, size * 0.50f, size * 0.46f);
    });

    /// <summary>
    /// Upgrade glyph for Help &gt; Check for Updates and the update window. An
    /// arrow pointing <em>up</em> out of a filled disc: the download arrows in
    /// this file all point down, and an update has to read as a different kind
    /// of transfer at 16 px.
    /// </summary>
    public static Bitmap Update(int size = 24) => Canvas(size, g =>
    {
        var circle = new RectangleF(size * 0.06f, size * 0.06f, size * 0.88f, size * 0.88f);
        using (var fill = new LinearGradientBrush(circle, Theme.AccentLight, Theme.Accent, LinearGradientMode.Vertical))
            g.FillEllipse(fill, circle);

        using var pen = new Pen(Color.White, Math.Max(1.5f, size * 0.10f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        float cx = size * 0.50f;
        g.DrawLine(pen, cx, size * 0.72f, cx, size * 0.32f);
        g.DrawLines(pen, new PointF[]
        {
            new(size * 0.30f, size * 0.51f),
            new(cx, size * 0.28f),
            new(size * 0.70f, size * 0.51f)
        });
    });

    private static void DrawBadge(Graphics g, int size, Color color, bool plus, bool arrow = false)
    {
        var badge = new RectangleF(size * 0.50f, size * 0.50f, size * 0.46f, size * 0.46f);
        using (var shadow = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
            g.FillEllipse(shadow, badge.X - 1, badge.Y - 1, badge.Width + 2, badge.Height + 2);
        using (var badgeBrush = new SolidBrush(color))
            g.FillEllipse(badgeBrush, badge);

        var c = new PointF(badge.X + badge.Width / 2, badge.Y + badge.Height / 2);
        using var pen = new Pen(Color.White, Math.Max(1.4f, size * 0.075f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        if (plus)
        {
            g.DrawLine(pen, c.X - badge.Width * 0.22f, c.Y, c.X + badge.Width * 0.22f, c.Y);
            g.DrawLine(pen, c.X, c.Y - badge.Height * 0.22f, c.X, c.Y + badge.Height * 0.22f);
        }
        else if (arrow)
        {
            g.DrawLine(pen, c.X - badge.Width * 0.20f, c.Y + badge.Height * 0.16f, c.X + badge.Width * 0.18f, c.Y - badge.Height * 0.18f);
            g.DrawLines(pen, new PointF[]
            {
                new(c.X - badge.Width * 0.02f, c.Y - badge.Height * 0.20f),
                new(c.X + badge.Width * 0.20f, c.Y - badge.Height * 0.20f),
                new(c.X + badge.Width * 0.20f, c.Y + badge.Height * 0.02f)
            });
        }
        else
        {
            g.DrawLines(pen, new PointF[]
            {
                new(c.X - badge.Width * 0.22f, c.Y),
                new(c.X - badge.Width * 0.05f, c.Y + badge.Height * 0.18f),
                new(c.X + badge.Width * 0.24f, c.Y - badge.Height * 0.20f)
            });
        }
    }

    // ---------------------------------------------------------- Sidebar tree --

    public static Bitmap Folder(int size = 16, Color? tint = null) => Canvas(size, g =>
    {
        var color = tint ?? Color.FromArgb(246, 196, 84);
        using var brush = new SolidBrush(color);
        using var dark = new SolidBrush(ControlPaint.Dark(color, 0.05f));
        g.FillRectangle(dark, size * 0.06f, size * 0.16f, size * 0.44f, size * 0.16f);
        using var body = Theme.RoundedRect(new RectangleF(size * 0.06f, size * 0.25f, size * 0.88f, size * 0.56f), size * 0.09f);
        g.FillPath(brush, body);
    });

    public static Bitmap CategoryIcon(FileCategory category, int size = 16) => Canvas(size, g =>
    {
        Color color = CategoryColor(category);
        using var brush = new SolidBrush(color);
        var rect = new RectangleF(size * 0.12f, size * 0.06f, size * 0.76f, size * 0.88f);
        using var path = Theme.RoundedRect(rect, size * 0.16f);
        g.FillPath(brush, path);

        using var glyphPen = new Pen(Color.White, Math.Max(1f, size * 0.075f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var glyphBrush = new SolidBrush(Color.White);

        switch (category)
        {
            case FileCategory.Music:
                g.DrawEllipse(glyphPen, rect.X + rect.Width * 0.22f, rect.Y + rect.Height * 0.56f, rect.Width * 0.24f, rect.Height * 0.22f);
                g.DrawLine(glyphPen, rect.X + rect.Width * 0.46f, rect.Y + rect.Height * 0.67f, rect.X + rect.Width * 0.46f, rect.Y + rect.Height * 0.24f);
                g.DrawLine(glyphPen, rect.X + rect.Width * 0.46f, rect.Y + rect.Height * 0.24f, rect.X + rect.Width * 0.72f, rect.Y + rect.Height * 0.32f);
                break;
            case FileCategory.Video:
                g.FillPolygon(glyphBrush, new PointF[]
                {
                    new(rect.X + rect.Width * 0.36f, rect.Y + rect.Height * 0.28f),
                    new(rect.X + rect.Width * 0.36f, rect.Y + rect.Height * 0.72f),
                    new(rect.X + rect.Width * 0.72f, rect.Y + rect.Height * 0.50f)
                });
                break;
            case FileCategory.Compressed:
                for (int i = 0; i < 4; i++)
                {
                    float y = rect.Y + rect.Height * (0.20f + i * 0.16f);
                    float offset = i % 2 == 0 ? -0.07f : 0.07f;
                    g.FillRectangle(glyphBrush, rect.X + rect.Width * (0.44f + offset), y, rect.Width * 0.14f, rect.Height * 0.10f);
                }
                break;
            case FileCategory.Documents:
                for (int i = 0; i < 3; i++)
                {
                    float y = rect.Y + rect.Height * (0.32f + i * 0.19f);
                    float right = i == 2 ? 0.58f : 0.72f;
                    g.DrawLine(glyphPen, rect.X + rect.Width * 0.28f, y, rect.X + rect.Width * right, y);
                }
                break;
            case FileCategory.Programs:
                g.FillRectangle(glyphBrush, rect.X + rect.Width * 0.28f, rect.Y + rect.Height * 0.30f, rect.Width * 0.44f, rect.Height * 0.40f);
                using (var holeBrush = new SolidBrush(color))
                    g.FillRectangle(holeBrush, rect.X + rect.Width * 0.40f, rect.Y + rect.Height * 0.42f, rect.Width * 0.20f, rect.Height * 0.16f);
                break;
            case FileCategory.Pictures:
                g.FillEllipse(glyphBrush, rect.X + rect.Width * 0.28f, rect.Y + rect.Height * 0.26f, rect.Width * 0.18f, rect.Height * 0.18f);
                g.FillPolygon(glyphBrush, new PointF[]
                {
                    new(rect.X + rect.Width * 0.22f, rect.Y + rect.Height * 0.76f),
                    new(rect.X + rect.Width * 0.48f, rect.Y + rect.Height * 0.46f),
                    new(rect.X + rect.Width * 0.78f, rect.Y + rect.Height * 0.76f)
                });
                break;
            default:
                g.DrawLine(glyphPen, rect.X + rect.Width * 0.30f, rect.Y + rect.Height * 0.38f, rect.X + rect.Width * 0.70f, rect.Y + rect.Height * 0.38f);
                g.DrawLine(glyphPen, rect.X + rect.Width * 0.30f, rect.Y + rect.Height * 0.60f, rect.X + rect.Width * 0.58f, rect.Y + rect.Height * 0.60f);
                break;
        }
    });

    public static Color CategoryColor(FileCategory category) => category switch
    {
        FileCategory.Compressed => Color.FromArgb(146, 110, 206),
        FileCategory.Documents => Color.FromArgb(64, 122, 196),
        FileCategory.Music => Color.FromArgb(210, 88, 148),
        FileCategory.Programs => Color.FromArgb(58, 166, 118),
        FileCategory.Video => Color.FromArgb(224, 104, 62),
        FileCategory.Pictures => Color.FromArgb(46, 168, 190),
        _ => Gray
    };

    public static Bitmap StatusDot(Color color, int size = 16) => Canvas(size, g =>
    {
        using var halo = new SolidBrush(Color.FromArgb(60, color));
        g.FillEllipse(halo, size * 0.14f, size * 0.14f, size * 0.72f, size * 0.72f);
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, size * 0.29f, size * 0.29f, size * 0.42f, size * 0.42f);
    });
}
