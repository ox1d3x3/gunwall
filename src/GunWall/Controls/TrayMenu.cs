using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace GunWall.Controls;

/// <summary>Colour role for a tray menu item's icon, matching the app's state
/// colours: Normal is the muted icon colour; the others are 'ok', 'warn' and
/// 'brand' (blocked).</summary>
internal enum TrayTone { Normal, Allow, Warn, Block }

/// <summary>
/// The tray icon's right-click menu, drawn in GunWall's own palette (0.99.179).
///
/// The tray menu is a Windows Forms menu, because a NotifyIcon belongs to
/// Windows Forms and its menu closes properly when the user clicks elsewhere -
/// which a WPF ContextMenu opened from a hidden window does not reliably do. It
/// used the stock grey Windows menu: light grey on a dark-themed app, a
/// checkbox tick for lockdown, no icons. Reported from use as not matching the
/// app.
///
/// What this draws instead: the window's elevated surface colour, its border and
/// text colours, a rounded highlight on hover, a Fluent icon per item, a status
/// line at the top with the same coloured dot as the sidebar, and on Windows 11
/// rounded corners with the border drawn by Windows. The palette is read from the
/// app's theme each time the menu opens, so it follows a light/dark switch
/// without rebuilding anything.
///
/// Icons are glyphs from the Windows icon font (Segoe Fluent Icons on Windows 11,
/// Segoe MDL2 Assets on Windows 10 - the code points used here exist in both),
/// drawn in the current palette rather than shipped as images, so they are sharp
/// at any scale and recolour with the theme.
/// </summary>
internal sealed class TrayMenu
{
    /// <summary>Icon font code points (present in both Segoe Fluent Icons and Segoe MDL2 Assets).</summary>
    public static class Glyphs
    {
        public const string Open = "";         // OpenInNewWindow
        public const string Applications = ""; // AllApps
        public const string Connections = "";  // Globe
        public const string Activity = "";     // History
        public const string Security = "";     // Shield
        public const string Settings = "";     // Setting
        public const string Lock = "";         // Lock
        public const string Unlock = "";       // Unlock
        public const string Exit = "";         // PowerButton
    }

    /// <summary>How one item is drawn. Kept in the item's Tag.</summary>
    private sealed class Look
    {
        public string Glyph = "";
        public TrayTone Tone = TrayTone.Normal;
        public bool IsStatus;
    }

    private readonly Renderer _renderer;
    private readonly float _scale;
    private readonly Bitmap _iconSlot;

    public WinForms.ContextMenuStrip Strip { get; }

    public TrayMenu()
    {
        Strip = new WinForms.ContextMenuStrip();
        _scale = Math.Max(1f, Strip.DeviceDpi / 96f);
        _renderer = new Renderer(_scale);
        Strip.Renderer = _renderer;
        Strip.ShowCheckMargin = false;
        Strip.ShowImageMargin = true;
        Strip.Font = new Font("Segoe UI", 9f);

        // Every item carries this empty image so the menu reserves an icon column
        // of the right width. The glyph is drawn into that column by the renderer;
        // the bitmap itself is never shown.
        // 16 is the menu's own default icon size, so the column stays at its
        // stock width rather than widening the menu (0.99.180: compact).
        int slot = Px(16);
        _iconSlot = new Bitmap(slot, slot);

        Strip.HandleCreated += (_, _) => ApplyWindowFrame();
        Strip.Opening += (_, _) => RefreshPalette();
    }

    private int Px(float logical) => (int)Math.Round(logical * _scale);

    /// <summary>The status line at the top: a coloured dot and the posture in
    /// semibold. Not clickable - it is a reading, like the sidebar's.</summary>
    public WinForms.ToolStripMenuItem AddStatus()
    {
        var item = NewItem("", new Look { IsStatus = true });
        item.Enabled = false;
        item.Font = new Font("Segoe UI Semibold", 9f);
        Strip.Items.Add(item);
        return item;
    }

    public WinForms.ToolStripMenuItem Add(string text, string glyph, Action onClick)
    {
        var item = NewItem(text, new Look { Glyph = glyph });
        item.Click += (_, _) => onClick();
        Strip.Items.Add(item);
        return item;
    }

    public void AddSeparator() =>
        Strip.Items.Add(new WinForms.ToolStripSeparator { AutoSize = false, Height = Px(7) });

    /// <summary>Changes an item's label, icon and colour role, e.g. for the
    /// lockdown item when the menu opens.</summary>
    public static void Set(WinForms.ToolStripItem item, string text, string glyph, TrayTone tone)
    {
        item.Text = text;
        if (item.Tag is Look look) { look.Glyph = glyph; look.Tone = tone; }
        item.Invalidate();
    }

    /// <summary>Sets the status line's text and dot colour.</summary>
    public static void SetStatus(WinForms.ToolStripItem item, string text, TrayTone tone)
    {
        item.Text = text;
        if (item.Tag is Look look) look.Tone = tone;
        item.Invalidate();
    }

    private WinForms.ToolStripMenuItem NewItem(string text, Look look) => new(text)
    {
        Tag = look,
        Image = _iconSlot,
        ImageScaling = WinForms.ToolStripItemImageScaling.None,
        // Item padding adds to the height the menu computes for every row. 0.99.179
        // used 5 (rows about 32 px, as Windows 11's own menus) and it was reported
        // as huge for a tray menu; 2 gives about 24 px, one notch above stock.
        Padding = new WinForms.Padding(0, Px(2), 0, Px(2)),
    };

    // ---- Palette -------------------------------------------------------------

    private void RefreshPalette()
    {
        try
        {
            _renderer.Palette = Palette.FromTheme();
            ApplyWindowFrame();
        }
        catch { /* cosmetic: keep the last palette */ }
    }

    // ---- Windows 11 frame ----------------------------------------------------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUNDSMALL = 3;

    /// <summary>Windows 11 (build 22000) is where windows can ask for rounded
    /// corners and a border colour. Earlier, the renderer draws a square border.</summary>
    internal static readonly bool WindowsRoundsCorners = Environment.OSVersion.Version.Build >= 22000;

    private void ApplyWindowFrame()
    {
        if (!WindowsRoundsCorners || !Strip.IsHandleCreated) return;
        try
        {
            int corner = DWMWCP_ROUNDSMALL;    // the radius Windows uses for its own menus
            DwmSetWindowAttribute(Strip.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            var b = _renderer.Palette.Border;
            int colorref = b.R | (b.G << 8) | (b.B << 16);
            DwmSetWindowAttribute(Strip.Handle, DWMWA_BORDER_COLOR, ref colorref, sizeof(int));
        }
        catch { /* older Windows: the drawn square border stays */ }
    }

    // ---- Palette from the WPF theme -------------------------------------------

    internal sealed class Palette
    {
        public Color Back, Border, Text, Muted, Hover, Allow, Warn, Block;

        /// <summary>Used only if the app's theme cannot be read: the system's
        /// own menu colours, so the menu still reads, just unthemed.</summary>
        public static Palette Fallback => new()
        {
            Back = SystemColors.Menu,
            Border = SystemColors.ControlDark,
            Text = SystemColors.MenuText,
            Muted = SystemColors.GrayText,
            Hover = SystemColors.MenuHighlight,
            Allow = SystemColors.MenuText,
            Warn = SystemColors.MenuText,
            Block = SystemColors.MenuText,
        };

        public static Palette FromTheme()
        {
            var f = Fallback;
            var p = new Palette
            {
                Back = Read("BgElevated", f.Back),
                Border = Read("BorderBrush", f.Border),
                Text = Read("TextPrimary", f.Text),
                Muted = Read("TextSecondary", f.Muted),
                Allow = Read("AllowText", f.Allow),
                Warn = Read("WarnText", f.Warn),
                Block = Read("BlockText", f.Block),
            };
            // The theme's hover token is a translucent white, which on a light
            // surface is invisible. A fixed step of the text colour over the
            // surface reads as hover in both themes.
            p.Hover = Mix(p.Back, p.Text, 0.09f);
            return p;
        }

        private static Color Read(string key, Color fallback)
        {
            try
            {
                if (System.Windows.Application.Current?.TryFindResource(key)
                        is System.Windows.Media.SolidColorBrush brush)
                {
                    var c = brush.Color;
                    return Color.FromArgb(255, c.R, c.G, c.B);
                }
            }
            catch { }
            return fallback;
        }

        private static Color Mix(Color a, Color b, float t) => Color.FromArgb(
            255,
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));

        public Color ToneColor(TrayTone tone) => tone switch
        {
            TrayTone.Allow => Allow,
            TrayTone.Warn => Warn,
            TrayTone.Block => Block,
            _ => Muted,
        };
    }

    // ---- Renderer -------------------------------------------------------------

    private sealed class Renderer : WinForms.ToolStripRenderer
    {
        private readonly float _scale;
        private readonly Font _iconFont;
        public Palette Palette = Palette.Fallback;

        public Renderer(float scale)
        {
            _scale = scale;
            _iconFont = new Font(IconFontFamily(), 8.5f);
        }

        private int Scaled(float logical) => (int)Math.Round(logical * _scale);

        private static string IconFontFamily()
        {
            try
            {
                using var fonts = new InstalledFontCollection();
                if (fonts.Families.Any(f => f.Name == "Segoe Fluent Icons")) return "Segoe Fluent Icons";
            }
            catch { }
            return "Segoe MDL2 Assets";
        }

        protected override void OnRenderToolStripBackground(WinForms.ToolStripRenderEventArgs e)
        {
            using var back = new SolidBrush(Palette.Back);
            e.Graphics.FillRectangle(back, e.AffectedBounds);
        }

        // The icon column is the same surface as the rest of the menu.
        protected override void OnRenderImageMargin(WinForms.ToolStripRenderEventArgs e) { }

        protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
        {
            if (WindowsRoundsCorners) return;          // Windows draws it, rounded
            using var pen = new Pen(Palette.Border);
            var r = new Rectangle(Point.Empty, e.ToolStrip.Size);
            r.Width -= 1; r.Height -= 1;
            e.Graphics.DrawRectangle(pen, r);
        }

        protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Enabled || !e.Item.Selected) return;
            // Inset and rounded, as Windows 11 draws its own menu highlight.
            var r = new Rectangle(Scaled(3), 0, e.Item.Width - Scaled(6), e.Item.Height);
            if (r.Width <= 0 || r.Height <= 0) return;
            using var path = Rounded(r, Scaled(4));
            using var hover = new SolidBrush(Palette.Hover);
            var g = e.Graphics;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(hover, path);
            g.SmoothingMode = old;
        }

        protected override void OnRenderItemImage(WinForms.ToolStripItemImageRenderEventArgs e)
        {
            if (e.Item?.Tag is not Look look) return;
            var g = e.Graphics;
            var slot = e.ImageRectangle;
            if (look.IsStatus)
            {
                int d = Scaled(7);
                var dot = new Rectangle(slot.X + (slot.Width - d) / 2, slot.Y + (slot.Height - d) / 2, d, d);
                using var fill = new SolidBrush(Palette.ToneColor(look.Tone));
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(fill, dot);
                g.SmoothingMode = old;
                return;
            }
            if (look.Glyph.Length == 0) return;
            WinForms.TextRenderer.DrawText(g, look.Glyph, _iconFont, slot, Palette.ToneColor(look.Tone),
                WinForms.TextFormatFlags.HorizontalCenter | WinForms.TextFormatFlags.VerticalCenter
                | WinForms.TextFormatFlags.NoPadding | WinForms.TextFormatFlags.SingleLine);
        }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            // Drawn here rather than by the base renderer, which paints a disabled
            // item in the system grey - the status line is disabled so it cannot
            // be clicked, but it is a reading and should be read.
            var color = e.Item?.Tag is Look { Tone: TrayTone.Block, IsStatus: false }
                ? Palette.Block
                : Palette.Text;
            WinForms.TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, color, e.TextFormat);
        }

        protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(Palette.Border);
            e.Graphics.DrawLine(pen, Scaled(8), y, e.Item.Width - Scaled(8), y);
        }

        // Nothing in this menu is a checkbox or has a submenu.
        protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e) { }
        protected override void OnRenderArrow(WinForms.ToolStripArrowRenderEventArgs e) { }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = Math.Max(1, radius * 2);
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
