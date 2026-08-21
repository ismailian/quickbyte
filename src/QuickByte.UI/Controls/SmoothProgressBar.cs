using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace QuickByte.UI.Controls;

/// <summary>
/// Flat, double-buffered progress bar that animates itself toward the last
/// reported value.
///
/// Replaces <see cref="ProgressBar"/> for two reasons: the themed Windows bar
/// runs its own lagging animation (and a jarring "rewind" flash when the value
/// drops), and it can't render the status text the details window wants inside
/// the track. Motion is driven by a private ~60 fps timer that only runs while
/// there is distance left to cover, so an idle window costs nothing.
/// </summary>
public sealed class SmoothProgressBar : Control
{
    private const double Easing = ProgressAnimation.Easing;
    private const double SnapThreshold = ProgressAnimation.SnapThreshold;

    private readonly System.Windows.Forms.Timer _animator;
    private double _displayed;
    private double _target;
    private Color _barColor = Theme.Accent;
    private string? _overlayText;

    public SmoothProgressBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 22;
        BackColor = Theme.Surface;
        Font = Theme.UiSmallBold;

        _animator = new System.Windows.Forms.Timer { Interval = ProgressAnimation.FrameIntervalMilliseconds };
        _animator.Tick += (_, _) => AdvanceFrame();
    }

    /// <summary>Fill color; changing it repaints immediately.</summary>
    public Color BarColor
    {
        get => _barColor;
        set { if (_barColor == value) return; _barColor = value; Invalidate(); }
    }

    /// <summary>Text drawn centered in the track. Null shows the percentage.</summary>
    public string? OverlayText
    {
        get => _overlayText;
        set { if (_overlayText == value) return; _overlayText = value; Invalidate(); }
    }

    public double Value => _displayed;

    /// <summary>Sets the value the bar eases toward (0-100).</summary>
    public void SetValue(double percentage, bool immediate = false)
    {
        _target = Math.Clamp(percentage, 0, 100);

        // Backwards jumps mean "restarted", not "regressed" — never animate in reverse.
        if (immediate || _target < _displayed - 5)
        {
            _animator.Stop();
            _displayed = _target;
            Invalidate();
            return;
        }

        if (Math.Abs(_target - _displayed) <= SnapThreshold)
        {
            _displayed = _target;
            Invalidate();
            return;
        }

        if (!_animator.Enabled) _animator.Start();
    }

    private void AdvanceFrame()
    {
        double gap = _target - _displayed;
        if (Math.Abs(gap) <= SnapThreshold)
        {
            _displayed = _target;
            _animator.Stop();
        }
        else
        {
            _displayed += gap * Easing;
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // Square corners: no curves to smooth, and antialiasing would only blur
        // the 1px border and the fill edge.
        g.SmoothingMode = SmoothingMode.None;
        g.Clear(BackColor);

        var track = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));

        using (var trackBrush = new SolidBrush(Theme.Track))
            g.FillRectangle(trackBrush, track);

        float fillWidth = (float)(track.Width * _displayed / 100.0);
        if (fillWidth > 0.5f)
        {
            using var fillBrush = new LinearGradientBrush(
                new RectangleF(track.X, track.Y, track.Width, track.Height + 1),
                ControlPaint.Light(_barColor, 0.15f), _barColor, LinearGradientMode.Vertical);
            g.FillRectangle(fillBrush, track.X, track.Y, fillWidth, track.Height);
        }

        using (var borderPen = new Pen(Theme.Border))
            g.DrawRectangle(borderPen, track);

        string text = _overlayText ?? $"{_displayed:0.0}%";
        if (string.IsNullOrEmpty(text)) return;

        // Draw the label twice, clipped to each side of the fill edge, so it
        // stays readable whether it sits on the fill or on the empty track.
        // PreserveGraphicsClipping is load-bearing: TextRenderer draws through GDI,
        // which ignores the GDI+ clip region unless this flag pushes it into the DC.
        // Without it the second (dark) pass overpaints the first across the whole
        // track and the label never turns white over the fill.
        var textBounds = Rectangle.Round(track);
        const TextFormatFlags Flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                      TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                                      TextFormatFlags.PreserveGraphicsClipping;

        using var clip = g.Clip;
        g.SetClip(new RectangleF(track.X, track.Y, fillWidth, track.Height), CombineMode.Intersect);
        TextRenderer.DrawText(g, text, Font, textBounds, Theme.TextOnAccent, Flags);
        g.Clip = clip;
        g.SetClip(new RectangleF(track.X + fillWidth, track.Y, track.Width - fillWidth + 1, track.Height),
            CombineMode.Intersect);
        TextRenderer.DrawText(g, text, Font, textBounds, Theme.Text, Flags);
        g.Clip = clip;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _animator.Dispose();
        base.Dispose(disposing);
    }
}
