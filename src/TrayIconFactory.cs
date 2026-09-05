using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace GHelperAutoMode;

internal static class TrayIconFactory
{
    public static Icon Create(ControlState controlState, PerformanceMode mode)
    {
        var color = controlState switch
        {
            ControlState.Pause => Color.FromArgb(107, 114, 128),
            ControlState.ForceSilent => Color.FromArgb(59, 130, 246),
            ControlState.ForceBalanced => Color.FromArgb(16, 185, 129),
            ControlState.ForceTurbo => Color.FromArgb(249, 115, 22),
            _ => mode switch
            {
                PerformanceMode.Silent => Color.FromArgb(59, 130, 246),
                PerformanceMode.Balanced => Color.FromArgb(16, 185, 129),
                PerformanceMode.Turbo => Color.FromArgb(249, 115, 22),
                _ => Color.FromArgb(99, 102, 241)
            }
        };

        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);

            using var shadowBrush = new SolidBrush(Color.FromArgb(55, 0, 0, 0));
            graphics.FillEllipse(shadowBrush, 2.5f, 3.5f, 27f, 27f);

            using var backgroundBrush = new SolidBrush(color);
            graphics.FillEllipse(backgroundBrush, 2f, 2f, 28f, 28f);

            using var ringPen = new Pen(Color.FromArgb(210, 255, 255, 255), 1.5f);
            graphics.DrawEllipse(ringPen, 3.5f, 3.5f, 25f, 25f);

            if (controlState == ControlState.Pause)
            {
                using var pauseBrush = new SolidBrush(Color.White);
                FillRoundedRectangle(graphics, pauseBrush, new RectangleF(10f, 9f, 4f, 14f), 1.5f);
                FillRoundedRectangle(graphics, pauseBrush, new RectangleF(18f, 9f, 4f, 14f), 1.5f);
            }
            else
            {
                var bolt = new[]
                {
                    new PointF(18.7f, 5.2f),
                    new PointF(9.8f, 17.1f),
                    new PointF(15.2f, 17.1f),
                    new PointF(12.7f, 27.0f),
                    new PointF(23.0f, 13.5f),
                    new PointF(17.4f, 13.5f)
                };

                using var boltBrush = new SolidBrush(Color.White);
                graphics.FillPolygon(boltBrush, bolt);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            if (!DestroyIcon(handle))
            {
                // The managed clone remains valid. Failure to release the temporary
                // native icon handle is non-fatal and there is no meaningful recovery.
            }
        }
    }

    private static void FillRoundedRectangle(Graphics graphics, Brush brush, RectangleF rectangle, float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2f;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
