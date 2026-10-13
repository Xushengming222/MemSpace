using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MemoryEase
{
    internal sealed partial class MainForm
    {
        private sealed class ResponsivePage
        {
            internal Panel Viewport, Header;
            internal TableLayoutPanel Frame;
            internal Label Title, Subtitle;
        }
        private Panel shell, sidebar, body;
        private Button compactNavigation;
        private ContextMenuStrip compactMenu;
        private TableLayoutPanel metricsLayout, settingsContent;
        private readonly List<ResponsivePage> responsivePages = new List<ResponsivePage>();
        private readonly List<Card> metricCards = new List<Card>();
        private readonly Dictionary<Control, Size> naturalSizes = new Dictionary<Control, Size>();
        private bool layoutReady, reflowing;

        private void RememberSizes(Control control)
        {
            naturalSizes[control] = control.Size;
            foreach (Control child in control.Controls) RememberSizes(child);
        }
        private int Unit(float value) { return Math.Max(1, (int)Math.Round(value * layoutScale)); }
        private int TextHeight(Control control, int width)
        {
            using (var graphics = control.CreateGraphics())
                return TextRenderer.MeasureText(graphics, control.Text.Length == 0 ? " " : control.Text, control.Font,
                    new Size(Math.Max(1, width), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height + Unit(4);
        }
        private int WrapLabel(Label label, int width)
        {
            label.AutoSize = false; label.MaximumSize = Size.Empty; label.Width = Math.Max(1, width);
            label.Height = TextHeight(label, width); return label.Height;
        }
        // Manual width constraint prevents AutoScroll's old DisplayRectangle, and nested preferred
        // widths, from keeping a page wider than its viewport after the window shrinks.
        private void Reflow()
        {
            if (!layoutReady || rebuilding || reflowing || IsDisposed || shell == null || responsivePages.Count != 4) return;
            reflowing = true;
            try {
                bool wide = ClientSize.Width >= Unit(1000);
                int sideWidth = wide ? Unit(224) : 0;
                sidebar.Visible = wide; sidebar.Bounds = new Rectangle(0, 0, sideWidth, shell.ClientSize.Height);
                body.Bounds = new Rectangle(sideWidth, 0, Math.Max(1, shell.ClientSize.Width - sideWidth), shell.ClientSize.Height);
                int pad = Math.Min(Unit(26), Math.Max(8, body.Width / 30));
                int innerWidth = Math.Max(1, body.ClientSize.Width - pad * 2);
                int top = pad;
                compactNavigation.Visible = !wide;
                if (!wide) {
                    string[] shortNames = { "概览", "排行", "开机", "设置" };
                    compactNavigation.Text = "菜单 · " + (innerWidth < Unit(340) ? shortNames[currentPage] : nav[currentPage].Text);
                    compactNavigation.Bounds = new Rectangle(pad, top, innerWidth, Math.Max(Unit(38), TextHeight(compactNavigation, innerWidth - Unit(16)) + Unit(10)));
                    top += compactNavigation.Height + Unit(8);
                }
                int statusHeight = TextHeight(status, 10000) + Unit(4);
                status.Bounds = new Rectangle(pad, Math.Max(top + 1, body.Height - statusHeight - pad), innerWidth, statusHeight);
                pages.Bounds = new Rectangle(pad, top, innerWidth, Math.Max(1, status.Top - top - Unit(8)));
                var page = responsivePages[currentPage]; var viewport = page.Viewport; var frame = page.Frame;
                int width = Math.Max(1, viewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 1);
                frame.SuspendLayout();
                try {
                    frame.Width = width; frame.ColumnStyles[0].SizeType = SizeType.Absolute; frame.ColumnStyles[0].Width = width;
                    page.Title.Dock = page.Subtitle.Dock = DockStyle.None;
                    int titleHeight = WrapLabel(page.Title, width);
                    page.Title.Location = Point.Empty;
                    page.Subtitle.Visible = page.Subtitle.Text.Length > 0;
                    int subtitleHeight = page.Subtitle.Visible ? WrapLabel(page.Subtitle, width) + Unit(4) : 0;
                    page.Subtitle.Location = new Point(0, titleHeight + Unit(4));
                    SetRow(frame, 0, titleHeight + subtitleHeight + Unit(20));
                    if (currentPage == 0) {
                        SetRow(frame, 1, dashboard.PreferredHeight(width) + dashboard.Margin.Vertical);
                        SetRow(frame, 2, FlowHeight((FlowLayoutPanel)frame.GetControlFromPosition(0, 2), width));
                        SetRow(frame, 3, ArrangeMetrics(width));
                        SetRow(frame, 4, Unit(116));
                        var card = (Card)frame.GetControlFromPosition(0, 5);
                        SetRow(frame, 5, WrapLabel(advice, Math.Max(1, width - card.Padding.Horizontal)) + card.Padding.Vertical + Unit(8));
                    } else if (currentPage == 1 || currentPage == 2) {
                        Control introduction = frame.GetControlFromPosition(0, 1);
                        SetRow(frame, 1, introduction is FlowLayoutPanel ? FlowHeight((FlowLayoutPanel)introduction, width) : WrapLabel((Label)introduction, width));
                        SetRow(frame, 3, FlowHeight((FlowLayoutPanel)frame.GetControlFromPosition(0, 3), width));
                        if (currentPage == 1) SetRow(frame, 4, WrapLabel((Label)frame.GetControlFromPosition(0, 4), width));
                        int otherHeight = frame.RowStyles.Cast<RowStyle>().Where((row, index) => index != 2).Sum(row => (int)row.Height);
                        SetRow(frame, 2, Math.Max(Unit(220), viewport.ClientSize.Height - otherHeight));
                    } else SetRow(frame, 1, ArrangeSettings(width));
                    int height = frame.RowStyles.Cast<RowStyle>().Sum(row => (int)row.Height);
                    frame.Height = Math.Max(height, viewport.ClientSize.Height);
                    viewport.AutoScrollMinSize = new Size(0, frame.Height);
                    frame.Location = new Point(0, viewport.AutoScrollPosition.Y);
                } finally { frame.ResumeLayout(true); }
            } finally { reflowing = false; }
        }
        private static void SetRow(TableLayoutPanel table, int row, int height)
        {
            while (table.RowStyles.Count <= row) table.RowStyles.Add(new RowStyle());
            table.RowStyles[row].SizeType = SizeType.Absolute; table.RowStyles[row].Height = Math.Max(1, height);
        }
        private int FlowHeight(FlowLayoutPanel flow, int width)
        {
            flow.AutoSize = false; flow.WrapContents = true; flow.Width = Math.Max(1, width);
            int usable = Math.Max(1, width - flow.Padding.Horizontal);
            int lineWidth = 0, lineHeight = 0, total = flow.Padding.Vertical;
            foreach (Control child in flow.Controls) {
                child.Dock = DockStyle.None; child.AutoSize = false;
                Size natural; if (!naturalSizes.TryGetValue(child, out natural)) natural = child.Size;
                int childWidth = Math.Max(1, Math.Min(natural.Width, usable - child.Margin.Horizontal));
                child.Width = childWidth;
                var nested = child as FlowLayoutPanel; var label = child as Label;
                if (nested != null) child.Height = FlowHeight(nested, childWidth);
                else if (label != null) WrapLabel(label, childWidth);
                else if (child is Button || child is RadioButton) child.Height = Math.Max(natural.Height, TextHeight(child, Math.Max(1, childWidth - Unit(16))) + Unit(12));
                else child.Height = natural.Height;
                int itemWidth = child.Width + child.Margin.Horizontal;
                int itemHeight = child.Height + child.Margin.Top + Unit(8);
                if (lineWidth > 0 && lineWidth + itemWidth > usable) { total += lineHeight; lineWidth = lineHeight = 0; }
                lineWidth += itemWidth; lineHeight = Math.Max(lineHeight, itemHeight);
                // A real bottom margin makes the native FlowLayoutPanel match the measured row height.
                child.Margin = new Padding(child.Margin.Left, child.Margin.Top, child.Margin.Right, Unit(8));
            }
            flow.Height = total + lineHeight; return flow.Height + flow.Margin.Vertical;
        }
        private int ArrangeMetrics(int width)
        {
            int columns = Math.Max(1, Math.Min(3, width / Unit(280)));
            int rows = (metricCards.Count + columns - 1) / columns;
            metricsLayout.SuspendLayout();
            try {
                metricsLayout.ColumnCount = columns; metricsLayout.RowCount = rows;
                metricsLayout.ColumnStyles.Clear(); metricsLayout.RowStyles.Clear();
                int cellWidth = width / columns;
                for (int col = 0; col < columns; col++) metricsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, col == columns - 1 ? width - cellWidth * col : cellWidth));
                int[] heights = new int[rows];
                for (int i = 0; i < metricCards.Count; i++) {
                    var card = metricCards[i]; int col = i % columns, row = i / columns;
                    metricsLayout.SetCellPosition(card, new TableLayoutPanelCellPosition(col, row));
                    card.Margin = new Padding(col == 0 ? 0 : Unit(5), 0, col == columns - 1 ? 0 : Unit(5), Unit(8));
                    int inner = Math.Max(1, cellWidth - card.Margin.Horizontal - card.Padding.Horizontal);
                    var table = (TableLayoutPanel)card.Controls[0]; table.ColumnCount = 1;
                    table.ColumnStyles.Clear(); table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, inner));
                    int height = card.Padding.Vertical + card.Margin.Vertical;
                    for (int j = 0; j < 3; j++) {
                        var label = (Label)table.GetControlFromPosition(0, j); label.Dock = DockStyle.Fill;
                        int labelHeight = WrapLabel(label, inner) + label.Margin.Vertical;
                        SetRow(table, j, labelHeight); height += labelHeight;
                    }
                    heights[row] = Math.Max(heights[row], height);
                }
                for (int row = 0; row < rows; row++) metricsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, heights[row]));
                return heights.Sum();
            } finally { metricsLayout.ResumeLayout(true); }
        }
        private int ArrangeSettings(int width)
        {
            var card = (Card)responsivePages[3].Frame.GetControlFromPosition(0, 1);
            int inner = Math.Max(1, width - card.Padding.Horizontal);
            var content = settingsContent; content.SuspendLayout();
            try {
                content.Width = inner; content.ColumnStyles[0].Width = inner; content.Location = new Point(card.Padding.Left, card.Padding.Top);
                int total = 0;
                for (int i = 0; i < content.Controls.Count; i++) {
                    Control child = content.Controls[i]; child.AutoSize = false; child.MaximumSize = Size.Empty;
                    int childWidth = Math.Max(1, inner - child.Margin.Horizontal), height;
                    var label = child as Label; var flow = child as FlowLayoutPanel;
                    if (flow != null) height = FlowHeight(flow, childWidth) - flow.Margin.Vertical;
                    else if (label != null) height = WrapLabel(label, childWidth);
                    else if (child is CheckBox) height = TextHeight(child, Math.Max(1, childWidth - Unit(24))) + Unit(12);
                    else { Size natural = naturalSizes[child]; height = natural.Height; if (child is Button) { childWidth = Math.Min(childWidth, natural.Width); height = Math.Max(height, TextHeight(child, childWidth - Unit(16)) + Unit(12)); } }
                    child.Width = childWidth; child.Height = height; child.Dock = child is Button ? DockStyle.None : DockStyle.Fill;
                    SetRow(content, i, height + child.Margin.Vertical); total += height + child.Margin.Vertical;
                }
                content.Height = total; return total + card.Padding.Vertical;
            } finally { content.ResumeLayout(true); }
        }
    }
}
