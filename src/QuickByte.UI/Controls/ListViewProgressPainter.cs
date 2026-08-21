using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace QuickByte.UI.Controls;

/// <summary>
/// Draws the inline progress pill (rounded track + fill + centered
/// percentage) into a ListView subitem's cell, matching
/// <see cref="SmoothProgressBar"/> so the list and the details window read as
/// the same component at two sizes. Kept as a static helper rather than a
/// control since it only runs inside an existing OwnerDraw handler.
/// </summary>
public static class ListViewProgressPainter
{
    public static void Draw(Graphics g, Rectangle bounds, double percentage, Color fillColor, Color textColor)
    {
        percentage = Math.Clamp(percentage, 0, 100);

        var track = Rectangle.Inflate(bounds, -6, -4);
        if (track.Width <= 2 || track.Height <= 2) return;

        // Square corners: no curves to smooth, and antialiasing would only blur
        // the 1px border and the fill edge.
        var previousMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;

        using (var trackBrush = new SolidBrush(Theme.Track))
            g.FillRectangle(trackBrush, track);

        float fillWidth = (float)(track.Width * percentage / 100.0);
        if (fillWidth > 0.5f)
        {
            using var fillBrush = new LinearGradientBrush(
                new RectangleF(track.X, track.Y, track.Width, track.Height + 1),
                ControlPaint.Light(fillColor, 0.15f), fillColor, LinearGradientMode.Vertical);
            g.FillRectangle(fillBrush, track.X, track.Y, fillWidth, track.Height);
        }

        using (var borderPen = new Pen(Theme.Border))
            g.DrawRectangle(borderPen, track);

        g.SmoothingMode = previousMode;

        // PreserveGraphicsClipping is load-bearing: TextRenderer draws through GDI,
        // which ignores the GDI+ clip region unless this flag pushes it into the DC.
        // Without it the second (dark) pass below overpaints the first across the
        // whole track and the percentage never turns white over the fill.
        const TextFormatFlags Flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                      TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
                                      TextFormatFlags.PreserveGraphicsClipping;

        string text = $"{percentage:0.0}%";

        // Each pass intersects the caller's clip rather than replacing it, so a
        // partially scrolled row can't paint text outside its update region.
        using var clip = g.Clip;
        g.SetClip(new RectangleF(track.X, track.Y, fillWidth, track.Height), CombineMode.Intersect);
        TextRenderer.DrawText(g, text, Theme.UiSmallBold, track, Theme.TextOnAccent, Flags);
        g.Clip = clip;
        g.SetClip(new RectangleF(track.X + fillWidth, track.Y, track.Width - fillWidth + 1, track.Height),
            CombineMode.Intersect);
        TextRenderer.DrawText(g, text, Theme.UiSmallBold, track, textColor, Flags);
        g.Clip = clip;
    }

    public static Color ColorForStatus(bool failed, bool paused, bool completed) => (failed, paused, completed) switch
    {
        (true, _, _) => Theme.Danger,
        (_, true, _) => Theme.Warning,
        (_, _, true) => Theme.Success,
        _ => Theme.Accent
    };
}
