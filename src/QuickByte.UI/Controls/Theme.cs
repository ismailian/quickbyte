using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace QuickByte.UI.Controls;

/// <summary>
/// The single source of truth for the app's palette, fonts and chrome
/// rendering. Every form pulls its colors from here instead of hard-coding
/// SystemColors, which is what keeps the windows looking like one flat,
/// deliberate product rather than a stack of default WinForms controls.
/// </summary>
public static class Theme
{
    // --- Surfaces -----------------------------------------------------------
    public static readonly Color Window = Color.FromArgb(243, 246, 249);
    public static readonly Color Surface = Color.White;
    public static readonly Color SurfaceAlt = Color.FromArgb(250, 251, 253);
    public static readonly Color Chrome = Color.FromArgb(252, 253, 254);
    public static readonly Color Sidebar = Color.FromArgb(248, 250, 252);
    public static readonly Color HeaderBack = Color.FromArgb(249, 250, 252);
    public static readonly Color Track = Color.FromArgb(232, 237, 242);
    public static readonly Color Border = Color.FromArgb(223, 229, 236);
    public static readonly Color BorderStrong = Color.FromArgb(200, 209, 219);

    // --- Text ---------------------------------------------------------------
    public static readonly Color Text = Color.FromArgb(30, 37, 46);
    public static readonly Color TextMuted = Color.FromArgb(112, 123, 136);
    public static readonly Color TextDisabled = Color.FromArgb(163, 172, 182);
    public static readonly Color TextOnAccent = Color.White;

    // --- Accents ------------------------------------------------------------
    public static readonly Color Accent = Color.FromArgb(0, 114, 198);
    public static readonly Color AccentDark = Color.FromArgb(0, 84, 152);
    public static readonly Color AccentLight = Color.FromArgb(74, 163, 231);
    public static readonly Color AccentSoft = Color.FromArgb(232, 242, 252);
    public static readonly Color Success = Color.FromArgb(32, 152, 86);
    public static readonly Color SuccessLight = Color.FromArgb(72, 190, 122);
    public static readonly Color Warning = Color.FromArgb(216, 152, 26);
    public static readonly Color Danger = Color.FromArgb(205, 68, 68);

    // --- Rows ---------------------------------------------------------------
    public static readonly Color RowSelected = Color.FromArgb(0, 114, 198);
    public static readonly Color RowSelectedInactive = Color.FromArgb(224, 233, 242);
    public static readonly Color RowAlt = Color.FromArgb(250, 251, 253);

    // --- Fonts --------------------------------------------------------------
    public static readonly Font Ui = new("Segoe UI", 9f);
    public static readonly Font UiBold = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font UiSmall = new("Segoe UI", 8.25f);
    public static readonly Font UiSmallBold = new("Segoe UI", 8.25f, FontStyle.Bold);
    public static readonly Font Title = new("Segoe UI", 12f, FontStyle.Regular);
    public static readonly Font TitleBold = new("Segoe UI", 12f, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 9f);

    /// <summary>Rounded-rectangle path helper shared by every custom painter.</summary>
    public static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0 || rect.Width <= 0 || rect.Height <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Applies the flat dialog-button look used across every form. Flat buttons
    /// don't grey themselves out the way system-drawn ones do, so the disabled
    /// palette is applied explicitly whenever Enabled flips.
    /// </summary>
    public static Button StyleButton(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.Font = Ui;
        button.AutoSize = false;
        button.Height = 30;
        if (button.Width < 92) button.Width = 92;
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderSize = 1;

        if (primary)
        {
            button.FlatAppearance.MouseOverBackColor = AccentLight;
            button.FlatAppearance.MouseDownBackColor = AccentDark;
        }
        else
        {
            button.FlatAppearance.MouseOverBackColor = AccentSoft;
            button.FlatAppearance.MouseDownBackColor = Track;
        }

        void ApplyState()
        {
            if (!button.Enabled)
            {
                button.BackColor = Track;
                button.ForeColor = TextDisabled;
                button.FlatAppearance.BorderColor = Border;
                button.Cursor = Cursors.Default;
                return;
            }

            button.BackColor = primary ? Accent : Surface;
            button.ForeColor = primary ? TextOnAccent : Text;
            button.FlatAppearance.BorderColor = primary ? AccentDark : BorderStrong;
            button.Cursor = Cursors.Hand;
        }

        button.EnabledChanged += (_, _) => ApplyState();
        ApplyState();
        return button;
    }

    /// <summary>A hairline separator, docked wherever it is added.</summary>
    public static Panel Separator(DockStyle dock = DockStyle.Top) =>
        new() { Dock = dock, Height = 1, Width = 1, BackColor = Border };
}

/// <summary>
/// Flat repaint of the ToolStrip/MenuStrip color scheme — no gradients, no
/// 3D bevels, accent-tinted hover. Paired with <see cref="FlatToolStripRenderer"/>.
/// </summary>
public sealed class FlatColorTable : ProfessionalColorTable
{
    public override Color ToolStripGradientBegin => Theme.Chrome;
    public override Color ToolStripGradientMiddle => Theme.Chrome;
    public override Color ToolStripGradientEnd => Theme.Chrome;
    public override Color ToolStripContentPanelGradientBegin => Theme.Chrome;
    public override Color ToolStripContentPanelGradientEnd => Theme.Chrome;
    public override Color ToolStripPanelGradientBegin => Theme.Chrome;
    public override Color ToolStripPanelGradientEnd => Theme.Chrome;
    public override Color MenuStripGradientBegin => Theme.Chrome;
    public override Color MenuStripGradientEnd => Theme.Chrome;
    public override Color StatusStripGradientBegin => Theme.Chrome;
    public override Color StatusStripGradientEnd => Theme.Chrome;
    public override Color ToolStripBorder => Theme.Border;
    public override Color ToolStripDropDownBackground => Theme.Surface;
    public override Color ImageMarginGradientBegin => Theme.Surface;
    public override Color ImageMarginGradientMiddle => Theme.Surface;
    public override Color ImageMarginGradientEnd => Theme.Surface;
    public override Color MenuBorder => Theme.Border;
    public override Color MenuItemBorder => Theme.AccentLight;
    public override Color MenuItemSelected => Theme.AccentSoft;
    public override Color MenuItemSelectedGradientBegin => Theme.AccentSoft;
    public override Color MenuItemSelectedGradientEnd => Theme.AccentSoft;
    public override Color MenuItemPressedGradientBegin => Theme.Surface;
    public override Color MenuItemPressedGradientMiddle => Theme.Surface;
    public override Color MenuItemPressedGradientEnd => Theme.Surface;
    public override Color ButtonSelectedHighlight => Theme.AccentSoft;
    public override Color ButtonSelectedHighlightBorder => Theme.AccentLight;
    public override Color ButtonPressedHighlight => Theme.AccentSoft;
    public override Color ButtonPressedHighlightBorder => Theme.Accent;
    public override Color ButtonSelectedGradientBegin => Theme.AccentSoft;
    public override Color ButtonSelectedGradientMiddle => Theme.AccentSoft;
    public override Color ButtonSelectedGradientEnd => Theme.AccentSoft;
    public override Color ButtonSelectedBorder => Theme.AccentLight;
    public override Color ButtonPressedGradientBegin => Color.FromArgb(212, 231, 249);
    public override Color ButtonPressedGradientMiddle => Color.FromArgb(212, 231, 249);
    public override Color ButtonPressedGradientEnd => Color.FromArgb(212, 231, 249);
    public override Color ButtonPressedBorder => Theme.Accent;
    public override Color CheckBackground => Theme.AccentSoft;
    public override Color CheckSelectedBackground => Theme.AccentSoft;
    public override Color SeparatorDark => Theme.Border;
    public override Color SeparatorLight => Theme.Surface;
    public override Color GripDark => Theme.Border;
    public override Color GripLight => Theme.Surface;
}

/// <summary>
/// Draws the toolbar/menu chrome flat: a single hairline under the strip
/// instead of the default etched border, and themed (rather than system)
/// text colors so disabled buttons read as disabled without going muddy.
/// </summary>
public sealed class FlatToolStripRenderer : ToolStripProfessionalRenderer
{
    public FlatToolStripRenderer() : base(new FlatColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            base.OnRenderToolStripBorder(e);
            return;
        }

        using var pen = new Pen(Theme.Border);
        var bounds = e.AffectedBounds;
        e.Graphics.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextDisabled;
        base.OnRenderItemText(e);
    }
}
