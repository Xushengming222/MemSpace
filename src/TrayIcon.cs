using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace MemoryEase
{
    internal static class TrayIcon
    {
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        internal static int Percentage(double usage)
        { return double.IsNaN(usage) ? 0 : (int)Math.Round(Math.Max(0, Math.Min(100, usage)), MidpointRounding.AwayFromZero); }
        internal static Color Background(int percentage)
        { return percentage >= 95 ? Color.FromArgb(186, 57, 49) : percentage >= 85 ? Color.FromArgb(183, 108, 25) : Theme.Accent; }
        internal static Icon Create(int percentage, int size)
        {
            size = Math.Max(16, Math.Min(64, size)); percentage = Math.Max(0, Math.Min(100, percentage));
            using (var bitmap = new Bitmap(size, size))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(Background(percentage)))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias; graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var shape = Theme.Rounded(new RectangleF(0, 0, size, size), size / 5f)) graphics.FillPath(brush, shape);
                string text = percentage.ToString(); float fontSize = size * 0.85f;
                while (fontSize > 6) {
                    using (var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                        if (graphics.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width <= size - 1) break;
                    fontSize -= 0.5f;
                }
                using (var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    graphics.DrawString(text, font, Brushes.White, new RectangleF(0, -0.5f, size, size), format);
                IntPtr handle = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); } finally { DestroyIcon(handle); }
            }
        }
    }
}
