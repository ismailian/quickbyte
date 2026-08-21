using System.Windows.Forms;

namespace QuickByte.UI.Controls;

/// <summary>
/// A Label that double-buffers its paint. The details window rewrites several
/// of these ~10 times a second (speed, ETA, downloaded bytes); the stock Label
/// erases its background before each redraw, which shows up as a shimmer at
/// that rate. (Assigning identical text is already a no-op in
/// <see cref="Control.Text"/>, so callers don't need to guard.)
/// </summary>
public sealed class BufferedLabel : Label
{
    public BufferedLabel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        UseCompatibleTextRendering = false;
    }
}
