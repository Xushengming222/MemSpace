using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MemoryEase
{
    internal sealed class ExclusionPicker : Form
    {
        private readonly List<ExclusionChoice> choices;
        private readonly TextBox search;
        private readonly DataGridView list;
        private readonly Button save, cancel;
        private bool filling;
        internal string SelectedNames { get { return ExclusionChoice.Selection(choices); } }
        internal ExclusionPicker(List<ExclusionChoice> choices)
        {
            this.choices = choices; Text = "选择排除应用"; Icon = Program.AppIcon;
            AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.CenterParent;
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(Math.Min(820, area.Width - 80), Math.Min(620, area.Height - 100)); MinimumSize = new Size(280, 260);
            BackColor = Theme.Background;
            search = new TextBox { Font = Theme.Font(10) };
            list = new DataGridView { Font = Theme.Font(10), BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
                ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
                ColumnHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, GridColor = Theme.Line, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal };
            list.RowTemplate.Height = 46;
            list.DefaultCellStyle = new DataGridViewCellStyle { Font = Theme.Font(10), SelectionBackColor = Color.FromArgb(225, 242, 237), SelectionForeColor = Theme.Ink, ForeColor = Theme.Ink, Padding = new Padding(4) };
            list.ColumnHeadersDefaultCellStyle.Font = Theme.Font(10);
            list.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", Width = 42, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Frozen = true });
            list.Columns.Add(new DataGridViewTextBoxColumn { Name = "Application" });
            save = Theme.Button("应用", true); cancel = Theme.Button("取消");
            save.DialogResult = DialogResult.OK; cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(search); Controls.Add(list); Controls.Add(save); Controls.Add(cancel);
            foreach (Control control in Controls) Theme.ScaleTree(control, Theme.UnitScale);
            var rowFont = new Font("Microsoft YaHei UI", 13.5f * Theme.UserFontScale, FontStyle.Regular, GraphicsUnit.Point);
            list.DefaultCellStyle.Font = list.RowsDefaultCellStyle.Font = list.AlternatingRowsDefaultCellStyle.Font = list.RowTemplate.DefaultCellStyle.Font = rowFont;
            foreach (DataGridViewColumn column in list.Columns) column.DefaultCellStyle.Font = rowFont;
            list.Columns[0].Width = (int)(42 * Theme.UnitScale);
            search.TextChanged += delegate { Fill(); };
            list.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e) { if (!filling && e.RowIndex >= 0 && e.ColumnIndex == 0) ((ExclusionChoice)list.Rows[e.RowIndex].Tag).Selected = Convert.ToBoolean(list.Rows[e.RowIndex].Cells[0].Value); };
            list.CellClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) Toggle(list.Rows[e.RowIndex]); };
            list.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Space && list.CurrentRow != null) { Toggle(list.CurrentRow); e.Handled = e.SuppressKeyPress = true; } };
            Resize += delegate { Arrange(); };
            AcceptButton = save; CancelButton = cancel; Fill(); Arrange();
        }
        private void Fill()
        {
            filling = true; list.SuspendLayout();
            try {
                list.Rows.Clear(); foreach (var choice in choices.Where(c => c.Name.IndexOf(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)) {
                    int row = list.Rows.Add(choice.Selected, choice.ToString()); list.Rows[row].Tag = choice;
                }
                using (var graphics = list.CreateGraphics()) {
                    int textWidth = choices.Count == 0 ? 0 : choices.Max(c => TextRenderer.MeasureText(graphics, c.ToString(), list.RowsDefaultCellStyle.Font).Width);
                    list.Columns[1].MinimumWidth = Math.Max((int)(180 * Theme.UnitScale), textWidth + (int)(20 * Theme.UnitScale));
                }
            } finally { list.ResumeLayout(); filling = false; }
        }
        private static void Toggle(DataGridViewRow row) { row.Cells[0].Value = !Convert.ToBoolean(row.Cells[0].Value); }
        private void Arrange()
        {
            int pad = 12, width = Math.Max(1, ClientSize.Width - pad * 2);
            int height = Math.Max(36, (int)(38 * Theme.UnitScale)), gap = 8;
            search.SetBounds(pad, pad, width, search.PreferredHeight);
            int buttonWidth = Math.Min((int)(160 * Theme.UnitScale), Math.Max(1, (width - gap) / 2));
            int bottom = Math.Max(search.Bottom + gap + 1, ClientSize.Height - pad - height);
            save.SetBounds(pad, bottom, buttonWidth, height); cancel.SetBounds(pad + buttonWidth + gap, bottom, buttonWidth, height);
            list.SetBounds(pad, search.Bottom + gap, width, Math.Max(1, bottom - search.Bottom - gap * 2));
        }
        internal void VerifySearchKeepsChoices()
        {
            if (choices.Count == 0) return;
            using (var graphics = list.CreateGraphics()) {
                int textHeight = TextRenderer.MeasureText(graphics, "内存 Ag", list.Rows[0].Cells[1].InheritedStyle.Font).Height;
                if (list.Rows[0].Height < textHeight + list.DefaultCellStyle.Padding.Vertical) throw new InvalidOperationException("应用选择列表的字号超过行高");
            }
            var first = choices[0]; bool before = first.Selected; search.Text = first.Name; Toggle(list.Rows[0]);
            search.Text = "";
            if (first.Selected == before) throw new InvalidOperationException("筛选后勾选状态丢失");
            first.Selected = before; Fill();
        }
    }
}
