// One place for the handful of colours and fonts the UI uses, so the forms
// don't drift apart. WinForms defaults look like 2005 out of the box; this is
// the minimum needed to look like it belongs on Windows 11.

using System.Drawing.Drawing2D;

namespace Split.UI;

internal static class Theme
{
    public static readonly Color Accent = Color.FromArgb(0, 120, 212);
    public static readonly Color Card = Color.FromArgb(243, 243, 243);
    public static readonly Color CardBorder = Color.FromArgb(225, 225, 225);
    public static readonly Color Page = Color.White;
    public static readonly Color Secondary = Color.FromArgb(96, 96, 96);
    public static readonly Color WarningBack = Color.FromArgb(255, 244, 206);
    public static readonly Color WarningText = Color.FromArgb(96, 62, 0);

    public static readonly Color Playing = Color.FromArgb(16, 137, 62);
    public static readonly Color Attention = Color.FromArgb(193, 108, 0);
    public static readonly Color Bad = Color.FromArgb(196, 43, 28);
    public static readonly Color Idle = Color.FromArgb(140, 140, 140);

    public static readonly Font Body = new("Segoe UI", 9F);
    public static readonly Font Heading = new("Segoe UI Semibold", 12F);
    public static readonly Font CardTitle = new("Segoe UI Semibold", 10F);
    public static readonly Font Caption = new("Segoe UI", 8F);
    public static readonly Font Monospace = new("Consolas", 8.5F);

    /// <summary>Fills a rounded rectangle, for card backgrounds.</summary>
    public static void FillRounded(Graphics g, Rectangle bounds, int radius, Color fill, Color border)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Inset by one pixel so the antialiased border isn't clipped.
        var r = new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        using var path = RoundedPath(r, radius);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(border);

        g.FillPath(brush, path);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // Extracting an icon allocates a GDI handle, and the route cards redraw
    // once a second — pulling a fresh one each time bleeds handles until the
    // app hits the 10,000-object limit and the UI starts failing to paint.
    // An app's icon doesn't change while it's installed, so cache forever.
    private static readonly Dictionary<string, Bitmap?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Best-effort app icon, cached. Returns null rather than throwing, and
    /// the caller must NOT dispose the result — it's shared.
    /// </summary>
    public static Bitmap? IconFor(string executablePath)
    {
        if (string.IsNullOrEmpty(executablePath)) return null;
        if (IconCache.TryGetValue(executablePath, out var cached)) return cached;

        Bitmap? bitmap = null;
        try
        {
            if (File.Exists(executablePath))
            {
                using var icon = Icon.ExtractAssociatedIcon(executablePath);
                bitmap = icon?.ToBitmap();
            }
        }
        catch
        {
            // Missing, unreadable, or icon-less executable.
        }

        IconCache[executablePath] = bitmap;
        return bitmap;
    }
}

/// <summary>A flat panel with a rounded card background.</summary>
internal class CardPanel : Panel
{
    public CardPanel()
    {
        // Without this the rounded corners flicker badly on resize.
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Page;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.FillRounded(e.Graphics, ClientRectangle, 8, Theme.Card, Theme.CardBorder);
        base.OnPaint(e);
    }
}

/// <summary>
/// The thin green bar under a route's volume slider. Peak meters look dead if
/// they track the signal exactly, so the level falls back gradually.
/// </summary>
internal sealed class LevelBar : Control
{
    private float _level;
    private float _display;

    public LevelBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Height = 3;
    }

    // Built in code, never dropped on a designer surface.
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float Level
    {
        get => _level;
        set
        {
            _level = Math.Clamp(value, 0f, 1f);
            // Jump up instantly, ease down — standard meter ballistics.
            _display = _level > _display ? _level : _display * 0.7f + _level * 0.3f;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (_display <= 0.01f) return;

        var width = (int)(Width * _display);
        if (width <= 0) return;

        using var brush = new SolidBrush(Theme.Playing);
        e.Graphics.FillRectangle(brush, 0, 0, width, Height);
    }
}
