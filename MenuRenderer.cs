using System.Drawing.Drawing2D;

namespace VirtualHereTray;

/// <summary>
/// The stock menu renderer sizes the submenu arrow from a DPI that WinForms only updates on some change events, so after
/// docking/undocking it can be far too big or too small. This one draws the arrow from the DPI of the window it is
/// actually painted in.
/// </summary>
public sealed class DpiMenuRenderer : ToolStripProfessionalRenderer
{
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        if (e.Direction != ArrowDirection.Right) { base.OnRenderArrow(e); return; }

        int dpi = e.Item.Owner?.DeviceDpi ?? Icons.Dpi;
        int h = Math.Max(5, (int)Math.Round(7.0 * dpi / 96.0)) | 1;   // odd, so the tip sits on a pixel row
        int w = (h + 1) / 2;
        var r = e.ArrowRectangle;
        float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;

        var old = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // e.ArrowColor turns white on the (light blue) selected row, so use the item's text colour instead.
        using var brush = new SolidBrush(e.Item.Enabled ? SystemColors.MenuText : SystemColors.GrayText);
        e.Graphics.FillPolygon(brush, new[]
        {
            new PointF(cx - w / 2f, cy - h / 2f),
            new PointF(cx - w / 2f, cy + h / 2f),
            new PointF(cx + w / 2f, cy),
        });
        e.Graphics.SmoothingMode = old;
    }
}
