using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using QuickByte.Core.Enums;
using QuickByte.Core.Models;

namespace QuickByte.UI.Controls;

/// <summary>
/// Renders the "start positions and download progress by connections" bar
/// from the Download Details window: the full file width is divided into one
/// segment per connection (proportional to its byte range), each segment
/// fills with a color reflecting that connection's status, and a thin marker
/// separates adjacent connections' start positions.
///
/// Fills are interpolated on a ~60 fps timer between the ~100 ms snapshots the
/// engine publishes, so the segments creep forward continuously instead of
/// stepping, and they never move backwards.
/// </summary>
public sealed class ConnectionSegmentsBar : Control
{
    private const double Easing = ProgressAnimation.Easing;
    private const double SnapThreshold = 0.0005;

    private readonly System.Windows.Forms.Timer _animator;
    private readonly Dictionary<int, double> _displayedFractions = new();
    private IReadOnlyList<ConnectionInfo> _connections = Array.Empty<ConnectionInfo>();
    private long _totalBytes;

    public ConnectionSegmentsBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 22;
        BackColor = Theme.Surface;

        _animator = new System.Windows.Forms.Timer { Interval = ProgressAnimation.FrameIntervalMilliseconds };
        _animator.Tick += (_, _) => AdvanceFrame();
    }

    public void UpdateData(IReadOnlyList<ConnectionInfo> connections, long totalBytes)
    {
        _connections = connections;
        _totalBytes = totalBytes;

        bool needsAnimation = false;
        foreach (var connection in connections)
        {
            double target = TargetFraction(connection);
            if (!_displayedFractions.TryGetValue(connection.ConnectionId, out double displayed))
            {
                _displayedFractions[connection.ConnectionId] = target;
                continue;
            }

            if (target < displayed - 0.05) _displayedFractions[connection.ConnectionId] = target; // restarted
            else if (Math.Abs(target - displayed) > SnapThreshold) needsAnimation = true;
        }

        if (needsAnimation && !_animator.Enabled) _animator.Start();
        Invalidate();
    }

    private static double TargetFraction(ConnectionInfo connection) =>
        connection.TotalBytes <= 0 ? 0 : Math.Clamp(connection.BytesDownloaded / (double)connection.TotalBytes, 0, 1);

    private void AdvanceFrame()
    {
        bool stillMoving = false;

        foreach (var connection in _connections)
        {
            double target = TargetFraction(connection);
            double displayed = _displayedFractions.TryGetValue(connection.ConnectionId, out double value) ? value : target;
            double gap = target - displayed;

            if (Math.Abs(gap) <= SnapThreshold) displayed = target;
            else { displayed += gap * Easing; stillMoving = true; }

            _displayedFractions[connection.ConnectionId] = displayed;
        }

        if (!stillMoving) _animator.Stop();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // Square corners throughout, matching the progress bars.
        g.SmoothingMode = SmoothingMode.None;
        g.Clear(BackColor);

        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using (var back = new SolidBrush(Theme.Track))
            g.FillRectangle(back, bounds);

        if (_connections.Count > 0 && _totalBytes > 0)
        {
            var inner = new RectangleF(bounds.X + 1, bounds.Y + 1, bounds.Width - 1, bounds.Height - 1);
            using var clip = g.Clip;
            g.SetClip(inner);
            DrawSegments(g, inner);
            g.Clip = clip;
        }

        using (var pen = new Pen(Theme.Border))
            g.DrawRectangle(pen, bounds);
    }

    private void DrawSegments(Graphics g, RectangleF inner)
    {
        float x = inner.X;

        foreach (var connection in _connections.OrderBy(c => c.RangeStart))
        {
            float segmentWidth = (float)connection.TotalBytes / _totalBytes * inner.Width;
            var segmentRect = new RectangleF(x, inner.Y, Math.Max(1, segmentWidth), inner.Height);

            double fraction = _displayedFractions.TryGetValue(connection.ConnectionId, out double value)
                ? value
                : TargetFraction(connection);

            float fillWidth = (float)(segmentRect.Width * fraction);
            if (fillWidth > 0.5f)
            {
                var fillColor = ColorFor(connection.Status);
                using var fillBrush = new LinearGradientBrush(
                    new RectangleF(segmentRect.X, segmentRect.Y, Math.Max(1, fillWidth), segmentRect.Height + 1),
                    ControlPaint.Light(fillColor, 0.2f), fillColor, LinearGradientMode.Vertical);
                g.FillRectangle(fillBrush, segmentRect.X, segmentRect.Y, fillWidth, segmentRect.Height);
            }

            x += segmentWidth;
            if (x < inner.Right)
            {
                using var markerPen = new Pen(Color.FromArgb(120, Theme.BorderStrong));
                g.DrawLine(markerPen, x, inner.Y, x, inner.Bottom);
            }
        }
    }

    private static Color ColorFor(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Finished => Theme.Success,
        ConnectionStatus.Failed => Theme.Danger,
        ConnectionStatus.Paused => Theme.Warning,
        ConnectionStatus.Idle => Theme.BorderStrong,
        _ => Theme.Accent
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _animator.Dispose();
        base.Dispose(disposing);
    }
}
