using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace VirtualHereTray;

/// <summary>Draws all icons at runtime (no image resources needed).</summary>
public static class Icons
{
    // The DPI everything is currently drawn for. It is not fixed at startup: docking, undocking and remote
    // sessions change it, so callers refresh it (RefreshDpi) and everything derived from it is rebuilt.
    public static int Dpi { get; private set; } = PrimaryDpi();
    public static event Action? DpiChanged;

    public static float Scale => Dpi / 96f;
    public static int Px(int v) => (int)Math.Round(v * Scale);
    public static int Size => Px(16);

    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    /// <summary>Effective DPI of the monitor containing the point (nearest monitor if it is off-screen).</summary>
    public static int DpiAt(Point p)
    {
        try
        {
            if (GetDpiForMonitor(MonitorFromPoint(p, 2), 0, out var x, out _) == 0 && x > 0) return (int)x;
        }
        catch { }
        return 96;
    }

    /// <summary>DPI of the primary monitor, where the taskbar and tray normally are.</summary>
    public static int PrimaryDpi() => DpiAt(new Point(0, 0));

    /// <summary>Records a new DPI; clears everything drawn for the old one and tells listeners.</summary>
    public static void RefreshDpi(int dpi)
    {
        if (dpi <= 0 || dpi == Dpi) return;
        Dpi = dpi;
        lock (Cache) Cache.Clear();
        TrayCache.Clear();
        DpiChanged?.Invoke();
    }

    /// <summary>The UI font at a DPI, sized in pixels so no DPI conversion by GDI can skew it (9 pt at 96 dpi = 12 px).</summary>
    public static Font UiFont(int dpi, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", (float)Math.Round(12.0 * dpi / 96.0), style, GraphicsUnit.Pixel);

    private static readonly string GlyphFont =
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    private static readonly Dictionary<string, Bitmap> Cache = new();

    private const string GUsb = "", GKeyboard = "", GMouse = "", GAudio = "",
        GStar = "", GStorage = "", GCamera = "", GPrinter = "", GBluetooth = "",
        GGamepad = "", GSerial = "";

    public static string GlyphFor(VhDevice d)
    {
        var name = (d.Product + " " + d.Vendor).ToLowerInvariant();
        if (name.Contains("uart") || name.Contains("serial") || name.Contains("cp210") || name.Contains("ftdi")) return GSerial;
        return d.InterfaceClass switch
        {
            1 => GAudio,
            3 => d.InterfaceProtocol == 1 ? GKeyboard : d.InterfaceProtocol == 2 ? GMouse : GStar,
            7 => GPrinter,
            8 => GStorage,
            9 => GUsb,
            14 => GCamera,
            224 => GStar,
            _ => GStar,
        };
    }

    public static Bitmap Device(VhDevice d)
    {
        var glyph = GlyphFor(d);
        var dot = d.State switch
        {
            DeviceState.InUseByMe => Color.FromArgb(34, 177, 76),
            DeviceState.InUseByOther => Color.FromArgb(232, 140, 20),
            DeviceState.Connecting => Color.FromArgb(240, 200, 30),
            _ => Color.Empty,
        };
        return Cached($"d{glyph}{dot.ToArgb()}", () => RenderGlyph(glyph, Color.FromArgb(60, 60, 60), dot));
    }

    public static Bitmap Server(bool connected) =>
        Cached("s" + connected, () =>
        {
            var bmp = new Bitmap(Size, Size);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var c = connected ? Color.FromArgb(34, 177, 76) : Color.FromArgb(190, 60, 60);
            using var b = new SolidBrush(c);
            using var pen = new Pen(Color.FromArgb(40, 0, 0, 0), 1f);
            var r = new RectangleF(Px(2), Px(2), Size - Px(4), Size - Px(4));
            g.FillEllipse(b, r);
            g.DrawEllipse(pen, r);
            return bmp;
        });

    public static Bitmap Glyph(string glyph, Color? color = null) =>
        Cached($"g{glyph}{(color ?? Color.Gray).ToArgb()}", () => RenderGlyph(glyph, color ?? Color.FromArgb(60, 60, 60), Color.Empty));

    private static Bitmap RenderGlyph(string glyph, Color color, Color dot)
    {
        var bmp = new Bitmap(Size, Size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using var font = new Font(GlyphFont, Size * 0.72f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, font, brush, new RectangleF(0, 0, Size, Size), sf);
        if (dot != Color.Empty)
        {
            int d = Px(7);
            var r = new Rectangle(Size - d - 1, Size - d - 1, d, d);
            using var wb = new SolidBrush(Color.White);
            using var db = new SolidBrush(dot);
            g.FillEllipse(wb, Rectangle.Inflate(r, 1, 1));
            g.FillEllipse(db, r);
        }
        return bmp;
    }

    private static Bitmap Cached(string key, Func<Bitmap> make)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var b)) Cache[key] = b = make();
            return b;
        }
    }

    // ---- Tray icon -----------------------------------------------------------------------

    public enum TrayKind { Offline, Idle, InUse }
    private static readonly Dictionary<TrayKind, Icon> TrayCache = new();

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    public static Icon Tray(TrayKind kind)
    {
        if (TrayCache.TryGetValue(kind, out var ic)) return ic;
        // Render at least 32 px (more on big-icon systems): Windows scales a single-size icon down to the tray
        // slot, which keeps it sharp at 100-200%. The shape fills the whole square, like the neighbouring tray icons.
        int s = Math.Max(32, GetSystemMetricsForDpi(49 /* SM_CXSMICON */, (uint)Dpi));
        using var bmp = RenderLogo(kind, s);
        var h = bmp.GetHicon();
        using var tmp = Icon.FromHandle(h);
        ic = (Icon)tmp.Clone();      // own copy so the GDI handle can be released
        DestroyIcon(h);
        TrayCache[kind] = ic;
        return ic;
    }

    /// <summary>The app logo: a rounded square with a USB-stick symbol, at any size (used for the tray icon, app.ico and the README).</summary>
    public static Bitmap RenderLogo(TrayKind kind, int s)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bg = kind switch
        {
            TrayKind.Offline => Color.FromArgb(120, 120, 120),
            TrayKind.InUse => Color.FromArgb(30, 150, 70),
            _ => Color.FromArgb(0, 102, 153),
        };
        using var path = RoundRect(new RectangleF(0, 0, s, s), s * 0.24f);
        using var bb = new SolidBrush(bg);
        g.FillPath(bb, path);

        // The USB-stick symbol is drawn as paths rather than with the icon font, so it is exactly
        // symmetric about the centre line and its line weight is under our control.
        float cx = s / 2f, top = s * 0.12f, bottom = s * 0.88f;
        float lw = s * 0.085f;                                  // line width
        float capW = s * 0.34f, capH = s * 0.19f;
        float bodyW = s * 0.54f, bodyTop = top + capH - lw * 0.2f;
        using var pen = new Pen(Color.White, lw) { LineJoin = LineJoin.Round };
        using var body = RoundRect(new RectangleF(cx - bodyW / 2 + lw / 2, bodyTop + lw / 2, bodyW - lw, bottom - bodyTop - lw), s * 0.1f);
        g.DrawPath(pen, body);
        g.DrawRectangle(pen, cx - capW / 2 + lw / 2, top + lw / 2, capW - lw, capH + lw * 0.2f - lw / 2);
        return bmp;
    }

    private static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
