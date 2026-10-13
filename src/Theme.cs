using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MemoryEase
{
    internal static class Theme
    {
        internal static float UnitScale = 1;
        internal static float UserFontScale = 1;
        internal static readonly Color Background = Color.FromArgb(244, 247, 249);
        internal static readonly Color Ink = Color.FromArgb(24, 44, 52);
        internal static readonly Color Muted = Color.FromArgb(80, 101, 110);
        internal static readonly Color Accent = Color.FromArgb(0, 125, 113);
        internal static readonly Color Line = Color.FromArgb(224, 232, 236);
        // Native controls use point units so Windows applies monitor DPI exactly once.
        internal static Font Font(float size, FontStyle style = FontStyle.Regular)
        { return new Font("Microsoft YaHei UI", (size >= 32 ? size * 4f / 3f : Math.Max(18f, size * 1.5f)) * 72f / 96f, style, GraphicsUnit.Point); }
        internal static Font DrawingFont(float size, FontStyle style = FontStyle.Regular)
        { return new Font("Microsoft YaHei UI", size >= 32 ? size * 4f / 3f : Math.Max(18f, size * 1.5f), style, GraphicsUnit.Pixel); }
        private static Font ScaledFont(Font font, float scale)
        { return new Font(font.FontFamily, font.SizeInPoints * UserFontScale, font.Style, GraphicsUnit.Point); }
        private static Padding ScaledPadding(Padding p, float scale)
        { return new Padding((int)Math.Round(p.Left * scale), (int)Math.Round(p.Top * scale), (int)Math.Round(p.Right * scale), (int)Math.Round(p.Bottom * scale)); }
        // Apply once to a freshly constructed tree. Rebuilds start from logical coordinates.
        internal static void ScaleTree(Control control, float scale)
        {
            control.SuspendLayout();
            foreach (Control child in control.Controls) ScaleTree(child, scale);
            control.Font = ScaledFont(control.Font, scale);
            // Native edit controls create HFONTs at 96 DPI; supply the monitor factor explicitly.
            if (control is TextBox || control is NumericUpDown)
                control.Font = new Font(control.Font.FontFamily, control.Font.SizeInPoints * scale / UserFontScale, control.Font.Style, GraphicsUnit.Point);
            control.Margin = ScaledPadding(control.Margin, scale); control.Padding = ScaledPadding(control.Padding, scale);
            control.MinimumSize = new Size((int)(control.MinimumSize.Width * scale), (int)(control.MinimumSize.Height * scale));
            control.MaximumSize = new Size((int)(control.MaximumSize.Width * scale), (int)(control.MaximumSize.Height * scale));
            var table = control as TableLayoutPanel;
            if (table != null) {
                foreach (RowStyle row in table.RowStyles) if (row.SizeType == SizeType.Absolute) row.Height *= scale;
                foreach (ColumnStyle column in table.ColumnStyles) if (column.SizeType == SizeType.Absolute) column.Width *= scale;
            }
            var grid = control as DataGridView;
            if (grid != null) {
                grid.ColumnHeadersDefaultCellStyle.Font = ScaledFont(grid.ColumnHeadersDefaultCellStyle.Font, scale);
                grid.DefaultCellStyle.Font = ScaledFont(grid.DefaultCellStyle.Font, scale);
                grid.ColumnHeadersDefaultCellStyle.Padding = ScaledPadding(grid.ColumnHeadersDefaultCellStyle.Padding, scale);
                grid.DefaultCellStyle.Padding = ScaledPadding(grid.DefaultCellStyle.Padding, scale);
                grid.ColumnHeadersHeight = (int)Math.Round(grid.ColumnHeadersHeight * scale);
                grid.RowTemplate.Height = (int)Math.Round(grid.RowTemplate.Height * scale);
                foreach (DataGridViewColumn column in grid.Columns) column.MinimumWidth = (int)Math.Round(column.MinimumWidth * scale);
            }
            control.Bounds = new Rectangle((int)Math.Round(control.Left * scale), (int)Math.Round(control.Top * scale), (int)Math.Round(control.Width * scale), (int)Math.Round(control.Height * scale));
            control.ResumeLayout();
        }
        internal static Label Label(string text, float size, Color color)
        { return new Label { Text = text, AutoSize = true, ForeColor = color, Font = Font(size), Margin = new Padding(0, 0, 0, 8) }; }
        internal static Button Button(string text, bool primary = false)
        {
            var b = new Button { Text = text, Height = 46, Width = 160, FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink,
                Font = Font(10), Cursor = Cursors.Hand, Margin = new Padding(0, 0, 12, 0), UseVisualStyleBackColor = false };
            b.FlatAppearance.BorderColor = primary ? Accent : Line;
            b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(0, 105, 94) : Color.FromArgb(235, 244, 242);
            return b;
        }
        internal static GraphicsPath Rounded(RectangleF rect, float radius)
        {
            var p = new GraphicsPath(); float d = radius * 2;
            p.AddArc(rect.X, rect.Y, d, d, 180, 90); p.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            p.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); p.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
    }
    internal class Card : Panel
    {
        internal Card() { DoubleBuffered = true; BackColor = Color.White; Padding = new Padding(20); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < 2 || Height < 2) return;
            using (var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 12 * Theme.UnitScale))
            using (var pen = new Pen(Theme.Line)) e.Graphics.DrawPath(pen, path);
        }
    }
    internal sealed class Dashboard : Card
    {
        internal MemorySnapshot Snapshot;
        internal int PreferredHeight(int width)
        {
            float scale = Theme.UnitScale, usable = Math.Max(1, width / scale - 32);
            using (var graphics = CreateGraphics()) using (var font = Theme.DrawingFont(10)) {
                string text = Footer();
                return (int)Math.Ceiling((160 + graphics.MeasureString(text, font, (int)usable).Height + 18) * scale);
            }
        }
        private string Footer()
        {
            return Snapshot == null ? "正在读取…" : "可用 " + MemorySnapshot.Size(Snapshot.Available) + "   /   总计 " + MemorySnapshot.Size(Snapshot.Total);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g = e.Graphics; var m = Snapshot;
            if (m == null) return;
            var state = g.Save(); g.ScaleTransform(Theme.UnitScale, Theme.UnitScale); float logicalWidth = Width / Theme.UnitScale;
            float numberSize = Math.Min(56, Math.Max(24, (logicalWidth - 32) / 3.5f));
            using (var small = Theme.DrawingFont(10)) using (var big = new Font("Microsoft YaHei UI", numberSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var mid = Theme.DrawingFont(13, FontStyle.Bold)) using (var ink = new SolidBrush(Theme.Ink))
            using (var muted = new SolidBrush(Theme.Muted)) using (var accent = new SolidBrush(Theme.Accent))
            {
                g.DrawString(logicalWidth < 180 ? "物理内存" : "实时物理内存", small, muted, new RectangleF(16, 16, Math.Max(1, logicalWidth - 32), 52));
                g.DrawString((m.Used / 1073741824.0).ToString("0.00"), big, ink, 16, 54);
                g.DrawString("GB 使用中", mid, muted, new RectangleF(16, 119, Math.Max(1, logicalWidth - 32), 42));
                g.DrawString(Footer(), small, muted, new RectangleF(16, 160, Math.Max(1, logicalWidth - 32), Math.Max(1, Height / Theme.UnitScale - 166)));
                float x = logicalWidth - 208, y = 28, size = 150;
                if (logicalWidth >= 600)
                {
                using (var track = new Pen(Color.FromArgb(231, 240, 239), 12))
                using (var progress = new Pen(m.UsedPercent >= 85 ? Color.FromArgb(198, 123, 39) : Theme.Accent, 12))
                {
                    g.DrawEllipse(track, x, y, size, size); progress.StartCap = progress.EndCap = LineCap.Round;
                    g.DrawArc(progress, x, y, size, size, -90, (float)Math.Min(359.8, Math.Max(0.1, 360 * m.UsedPercent / 100)));
                }
                using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                using (var percent = Theme.DrawingFont(24, FontStyle.Bold))
                {
                    g.DrawString(m.UsedPercent.ToString("0") + "%", percent, ink, new RectangleF(x, y + 38, size, 55), sf);
                    g.DrawString(m.UsedPercent >= 85 ? "内存压力较高" : "当前内存占用", small, muted, new RectangleF(x - 10, y + 96, size + 20, 30), sf);
                }
                }
            }
            g.Restore(state);
        }
    }
    internal sealed class HistoryChart : Card
    {
        internal MemoryHistory History;
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var state = g.Save(); g.ScaleTransform(Theme.UnitScale, Theme.UnitScale);
            float logicalWidth = Width / Theme.UnitScale, logicalHeight = Height / Theme.UnitScale;
            using (var font = Theme.DrawingFont(9)) using (var brush = new SolidBrush(Theme.Muted))
            { g.DrawString(logicalWidth < 160 ? "占用趋势" : "内存占用趋势", font, brush, 12, 10); if (logicalWidth > 480) { string caption = "最近 2 分钟 · 0–100%"; g.DrawString(caption, font, brush, logicalWidth - g.MeasureString(caption, font).Width - 18, 10); } }
            var area = new RectangleF(12, 43, Math.Max(1, logicalWidth - 24), Math.Max(1, logicalHeight - 56));
            using (var grid = new Pen(Theme.Line)) for (int i = 0; i < 3; i++) g.DrawLine(grid, area.Left, area.Top + area.Height * i / 2, area.Right, area.Top + area.Height * i / 2);
            if (History == null || History.Samples.Count < 2) { g.Restore(state); return; }
            var samples = History.Samples; long now = samples[samples.Count - 1].At;
            var points = new PointF[samples.Count];
            for (int i = 0; i < points.Length; i++) points[i] = new PointF(area.Right - area.Width * (now - samples[i].At) / MemoryHistory.Duration, area.Bottom - area.Height * (float)(samples[i].Usage / 100));
            using (var path = new GraphicsPath()) using (var fill = new SolidBrush(Color.FromArgb(22, Theme.Accent)))
            {
                path.AddLines(points); path.AddLine(points[points.Length - 1], new PointF(points[points.Length - 1].X, area.Bottom));
                path.AddLine(new PointF(points[points.Length - 1].X, area.Bottom), new PointF(points[0].X, area.Bottom)); path.CloseFigure(); g.FillPath(fill, path);
            }
            using (var pen = new Pen(Theme.Accent, 2)) g.DrawLines(pen, points);
            g.Restore(state);
        }
    }
}
