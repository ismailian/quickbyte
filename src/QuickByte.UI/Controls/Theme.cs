using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;
using QuickByte.Core.Enums;

namespace QuickByte.UI.Controls;

/// <summary>
/// The single source of truth for the app's palette, fonts and chrome
/// rendering. Every form pulls its colors from here instead of hard-coding
/// SystemColors, which is what keeps the windows looking like one flat,
/// deliberate product rather than a stack of default WinForms controls.
///
/// There are two palettes, light and dark, and every color below reads from
/// whichever <see cref="Apply"/> chose. It is chosen once, at startup, before any
/// window exists: a WinForms window takes its colors when it is built, so a
/// palette switched under open windows would leave half of each one in the old
/// colors. Options says the change applies at the next start.
/// </summary>
public static class Theme
{
    private static Palette _palette = Palette.Light;

    /// <summary>Whether the dark palette is in use.</summary>
    public static bool IsDark { get; private set; }

    // --- Surfaces -----------------------------------------------------------
    public static Color Window => _palette.Window;
    public static Color Surface => _palette.Surface;
    public static Color SurfaceAlt => _palette.SurfaceAlt;
    public static Color Chrome => _palette.Chrome;
    public static Color Sidebar => _palette.Sidebar;
    public static Color HeaderBack => _palette.HeaderBack;
    public static Color Track => _palette.Track;
    public static Color Border => _palette.Border;
    public static Color BorderStrong => _palette.BorderStrong;

    // --- Text ---------------------------------------------------------------
    public static Color Text => _palette.Text;
    public static Color TextMuted => _palette.TextMuted;
    public static Color TextDisabled => _palette.TextDisabled;
    public static Color TextOnAccent => _palette.TextOnAccent;

    // --- Accents ------------------------------------------------------------
    public static Color Accent => _palette.Accent;
    public static Color AccentDark => _palette.AccentDark;
    public static Color AccentLight => _palette.AccentLight;
    public static Color AccentSoft => _palette.AccentSoft;

    /// <summary>A toolbar button held down — a step deeper than <see cref="AccentSoft"/>.</summary>
    public static Color AccentPressed => _palette.AccentPressed;

    public static Color Success => _palette.Success;
    public static Color SuccessLight => _palette.SuccessLight;
    public static Color Warning => _palette.Warning;
    public static Color Danger => _palette.Danger;

    // --- Rows ---------------------------------------------------------------
    public static Color RowSelected => _palette.RowSelected;
    public static Color RowSelectedInactive => _palette.RowSelectedInactive;
    public static Color RowAlt => _palette.RowAlt;

    // --- Choosing the palette -------------------------------------------------

    /// <summary>Where Windows keeps "Choose your app mode". Settable only for the tests.</summary>
    internal static string PersonalizeKeyPath { get; set; } = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// Picks the palette for this run from the user's <see cref="AppTheme"/>
    /// preference. Call once, before the first window is built.
    /// </summary>
    public static void Apply(AppTheme preference) => Use(Resolve(preference, WindowsPrefersDark()));

    internal static void Use(bool dark)
    {
        IsDark = dark;
        _palette = dark ? Palette.Dark : Palette.Light;
    }

    /// <summary>
    /// Light or dark. "System" follows Windows' app mode, and a machine that does
    /// not say — older Windows, a locked-down profile — gets the light palette the
    /// app always had.
    /// </summary>
    internal static bool Resolve(AppTheme preference, bool? windowsPrefersDark) => preference switch
    {
        AppTheme.Light => false,
        AppTheme.Dark => true,
        _ => windowsPrefersDark == true
    };

    /// <summary>
    /// Windows' "Choose your default app mode": <c>AppsUseLightTheme</c> is 0 for
    /// dark. Null when the value is missing or unreadable.
    /// </summary>
    internal static bool? WindowsPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            return key?.GetValue("AppsUseLightTheme") is int light ? light == 0 : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finishes a window for the palette in use. Call at the end of a form's
    /// constructor. On the light palette there is nothing to do; on the dark one
    /// it darkens the title bar and the scroll bars, which WinForms cannot reach,
    /// and gives the native input controls that were built without explicit
    /// colours (spinners, drop-downs, date pickers) the palette's instead of
    /// Windows' white.
    /// </summary>
    public static void Attach(Form form)
    {
        if (!IsDark) return;

        form.ForeColor = Text;
        if (form.IsHandleCreated) DarkMode.UseDarkTitleBar(form.Handle);
        else form.HandleCreated += (_, _) => DarkMode.UseDarkTitleBar(form.Handle);

        StyleTree(form);
        form.ControlAdded += (_, e) => { if (e.Control is not null) StyleTree(e.Control); };
    }

    private static void StyleTree(Control root)
    {
        StyleControl(root);
        foreach (Control child in root.Controls) StyleTree(child);
    }

    private static void StyleControl(Control control)
    {
        // Only controls still wearing Windows' own colours: anything a form chose
        // on purpose — a read-only field on HeaderBack, a warning in amber — keeps it.
        if (control is TextBoxBase or NumericUpDown or ListBox or ComboBox or DateTimePicker or TreeView or ListView)
        {
            if (control.BackColor.ToArgb() == SystemColors.Window.ToArgb()) control.BackColor = Surface;
            if (control.ForeColor.ToArgb() == SystemColors.WindowText.ToArgb()) control.ForeColor = Text;
        }

        // The parts WinForms cannot colour: scroll bars, a combo box's own face, a
        // list view's header strip.
        Action<IntPtr>? native = control switch
        {
            ComboBox => DarkMode.UseDarkComboBox,

            // Only an owner-drawn list gets the header theme: it paints its own
            // column captions and just needs the strip past them dark. A plain
            // list's captions are drawn by that theme, dark on dark.
            ListView { OwnerDraw: true } => handle =>
            {
                DarkMode.UseDarkControls(handle);
                DarkMode.UseDarkListViewHeader(handle);
            },
            TextBoxBase or ListBox or TreeView or ListView => DarkMode.UseDarkControls,
            _ => null
        };
        if (native is null) return;

        if (control.IsHandleCreated) native(control.Handle);
        else control.HandleCreated += (_, _) => native(control.Handle);
    }

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
/// The colours of one theme. Light is the palette QuickByte has always had; dark
/// keeps the same roles and the same accent hue, lifted so blue text and bars stay
/// readable on a dark surface. <c>ThemeTests</c> holds both to readable contrast.
/// </summary>
internal sealed record Palette
{
    public required Color Window { get; init; }
    public required Color Surface { get; init; }
    public required Color SurfaceAlt { get; init; }
    public required Color Chrome { get; init; }
    public required Color Sidebar { get; init; }
    public required Color HeaderBack { get; init; }
    public required Color Track { get; init; }
    public required Color Border { get; init; }
    public required Color BorderStrong { get; init; }
    public required Color Text { get; init; }
    public required Color TextMuted { get; init; }
    public required Color TextDisabled { get; init; }
    public required Color TextOnAccent { get; init; }
    public required Color Accent { get; init; }
    public required Color AccentDark { get; init; }
    public required Color AccentLight { get; init; }
    public required Color AccentSoft { get; init; }
    public required Color AccentPressed { get; init; }
    public required Color Success { get; init; }
    public required Color SuccessLight { get; init; }
    public required Color Warning { get; init; }
    public required Color Danger { get; init; }
    public required Color RowSelected { get; init; }
    public required Color RowSelectedInactive { get; init; }
    public required Color RowAlt { get; init; }

    public static Palette Light { get; } = new()
    {
        Window = Color.FromArgb(243, 246, 249),
        Surface = Color.White,
        SurfaceAlt = Color.FromArgb(250, 251, 253),
        Chrome = Color.FromArgb(252, 253, 254),
        Sidebar = Color.FromArgb(248, 250, 252),
        HeaderBack = Color.FromArgb(249, 250, 252),
        Track = Color.FromArgb(232, 237, 242),
        Border = Color.FromArgb(223, 229, 236),
        BorderStrong = Color.FromArgb(200, 209, 219),
        Text = Color.FromArgb(30, 37, 46),
        TextMuted = Color.FromArgb(112, 123, 136),
        TextDisabled = Color.FromArgb(163, 172, 182),
        TextOnAccent = Color.White,
        Accent = Color.FromArgb(0, 114, 198),
        AccentDark = Color.FromArgb(0, 84, 152),
        AccentLight = Color.FromArgb(74, 163, 231),
        AccentSoft = Color.FromArgb(232, 242, 252),
        AccentPressed = Color.FromArgb(212, 231, 249),
        Success = Color.FromArgb(32, 152, 86),
        SuccessLight = Color.FromArgb(72, 190, 122),
        Warning = Color.FromArgb(216, 152, 26),
        Danger = Color.FromArgb(205, 68, 68),
        RowSelected = Color.FromArgb(0, 114, 198),
        RowSelectedInactive = Color.FromArgb(224, 233, 242),
        RowAlt = Color.FromArgb(250, 251, 253)
    };

    public static Palette Dark { get; } = new()
    {
        Window = Color.FromArgb(27, 29, 33),
        Surface = Color.FromArgb(34, 36, 41),
        SurfaceAlt = Color.FromArgb(39, 42, 47),
        Chrome = Color.FromArgb(30, 32, 37),
        Sidebar = Color.FromArgb(31, 33, 38),
        HeaderBack = Color.FromArgb(40, 43, 49),
        Track = Color.FromArgb(52, 56, 63),
        Border = Color.FromArgb(55, 59, 66),
        BorderStrong = Color.FromArgb(78, 83, 92),
        Text = Color.FromArgb(230, 233, 237),
        TextMuted = Color.FromArgb(158, 166, 176),
        TextDisabled = Color.FromArgb(108, 115, 124),
        TextOnAccent = Color.White,
        // Balanced between its two jobs: text on a dark surface (4.7:1) and the
        // background of a primary button under white text (3.3:1).
        Accent = Color.FromArgb(56, 146, 220),
        AccentDark = Color.FromArgb(24, 104, 170),
        AccentLight = Color.FromArgb(104, 180, 238),
        AccentSoft = Color.FromArgb(37, 56, 77),
        AccentPressed = Color.FromArgb(45, 72, 101),
        Success = Color.FromArgb(76, 187, 118),
        SuccessLight = Color.FromArgb(108, 208, 146),
        Warning = Color.FromArgb(232, 176, 64),
        Danger = Color.FromArgb(236, 104, 104),
        RowSelected = Color.FromArgb(24, 104, 170),
        RowSelectedInactive = Color.FromArgb(52, 60, 71),
        RowAlt = Color.FromArgb(38, 40, 45)
    };
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
    public override Color ButtonPressedGradientBegin => Theme.AccentPressed;
    public override Color ButtonPressedGradientMiddle => Theme.AccentPressed;
    public override Color ButtonPressedGradientEnd => Theme.AccentPressed;
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
