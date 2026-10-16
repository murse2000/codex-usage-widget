using System.Drawing.Drawing2D;

namespace CodexUsageWidget;

internal class GlassForm : Form
{
    protected GlassForm()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(238, 244, 252);
        ResizeRedraw = true;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        // 클릭 영역도 둥근 창 모양과 일치시킨다.
        using var outline = RoundedRectangle(ClientRectangle, LogicalToDeviceUnits(18));
        var previous = Region;
        Region = new Region(outline);
        previous?.Dispose();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var glass = new LinearGradientBrush(ClientRectangle, Color.FromArgb(250, 253, 255), Color.FromArgb(220, 232, 249), 65f);
        e.Graphics.FillRectangle(glass, ClientRectangle);
        using var glow = new GraphicsPath();
        glow.AddEllipse(-Width / 2, -Height / 2, Width * 2, Height * 2);
        using var light = new PathGradientBrush(glow)
        {
            CenterPoint = new PointF(Width * 0.18f, Height * 0.08f),
            CenterColor = Color.FromArgb(150, Color.White),
            SurroundColors = new[] { Color.FromArgb(0, Color.White) }
        };
        e.Graphics.FillRectangle(light, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = RoundedRectangle(new RectangleF(0.75f, 0.75f, Width - 1.5f, Height - 1.5f), LogicalToDeviceUnits(18));
        using var rim = new Pen(Color.FromArgb(225, Color.White), 1.5f);
        e.Graphics.DrawPath(rim, outline);
        using var inner = RoundedRectangle(new RectangleF(2, 2, Width - 4, Height - 4), LogicalToDeviceUnits(17));
        using var edge = new Pen(Color.FromArgb(38, 112, 140, 178));
        e.Graphics.DrawPath(edge, inner);
    }

    internal static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
