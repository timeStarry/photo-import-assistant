using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PhotoImportV2
{
    public static class AppIcon
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
        public static Icon Create()
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var background = new SolidBrush(Color.FromArgb(0, 103, 192)))
            using (var line = new Pen(Color.White, 1.8f))
            using (var shape = new GraphicsPath())
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                shape.AddArc(0, 0, 14, 14, 180, 90); shape.AddArc(18, 0, 14, 14, 270, 90);
                shape.AddArc(18, 18, 14, 14, 0, 90); shape.AddArc(0, 18, 14, 14, 90, 90); shape.CloseFigure();
                graphics.FillPath(background, shape); line.LineJoin = LineJoin.Round;
                graphics.DrawPolygon(line, new[] { new Point(6, 11), new Point(11, 11), new Point(13, 7), new Point(20, 7), new Point(22, 11), new Point(26, 11), new Point(26, 25), new Point(6, 25) });
                graphics.DrawEllipse(line, 12, 13, 9, 9);
                IntPtr handle = bitmap.GetHicon();
                try { using (var borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
    }
}
