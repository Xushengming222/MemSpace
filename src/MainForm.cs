using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MemoryEase
{
    internal sealed partial class MainForm : Form
    {
        private readonly AppSettings settings;
        private readonly bool isolated, startupLaunch;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly PressureGate gate = new PressureGate();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly MemoryHistory memoryHistory = new MemoryHistory();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 2000 };
        private NotifyIcon tray;
        private Panel pages;
        private readonly List<Panel> views = new List<Panel>();
        private readonly List<Button> nav = new List<Button>();
        private Label status, advice, commitValue, poolValue, reservedValue, authorization, exclusionSummary;
        private Dashboard dashboard;
        private HistoryChart history;
        private DataGridView processes, startups;
        private TextBox search;
        private Button release, selectedRelease, strongRelease, authorize;
        private CheckBox auto, autoStrong, autoExclusions, gameProtect, login, startupOnce;
        private StrongSession strongSession;
        private NumericUpDown threshold;
        private RadioButton groupApplications, groupProcesses, font100, font125, font150;
        private List<ApplicationGroup> appGroups = new List<ApplicationGroup>();
        private float layoutScale = 1;
        private int monitorDpi = 96;
        private bool rebuilding;
        private MemorySnapshot memory;
        private List<ProcessRow> processRows = new List<ProcessRow>();
        private bool busy, scanning, refreshing, automaticPaused, startupChecked, loadingSettings, restoringWindow;
        private int currentPage;
        private long lastScan = -30000;
        internal MainForm(AppSettings settings, bool trayLaunch, bool startupLaunch, bool isolated)
        {
            this.settings = settings; this.isolated = isolated; this.startupLaunch = startupLaunch;
            Text = "MemSpace · 内存管家"; Font = Theme.Font(10); BackColor = Theme.Background;
            ClientSize = new Size(1200, 880); MinimumSize = new Size(280, 240);
            StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.None; Icon = Program.AppIcon;
            BuildLayout();
            monitorDpi = (int)Native.GetDpiForWindow(Handle); if (monitorDpi <= 0) monitorDpi = 96;
            ApplyAppearance(false);
            if (!isolated) BuildTray();
            Resize += delegate { if (!restoringWindow && WindowState == FormWindowState.Minimized && tray != null) HideToTray(); };
            timer.Tick += async delegate { await RefreshAsync(); };
            Shown += async delegate {
                if (trayLaunch && !isolated) HideToTray();
                UpdateRefreshInterval(); await RefreshAsync(); if (!isolated) timer.Start();
            };
            FormClosing += delegate { timer.Stop(); lifetime.Cancel(); StopStrongSession(); };
            FormClosed += delegate { timer.Dispose(); lifetime.Dispose(); DisposeTray(); };
        }
        protected override void WndProc(ref Message message)
        {
            bool dpiChanged = message.Msg == 0x02e0;
            int newDpi = dpiChanged ? (int)(message.WParam.ToInt64() & 0xffff) : monitorDpi;
            base.WndProc(ref message);
            if (dpiChanged && newDpi > 0 && newDpi != monitorDpi && pages != null && !rebuilding) {
                monitorDpi = newDpi;
                BeginInvoke(new Action(delegate { if (!IsDisposed) ApplyAppearance(true); }));
            }
        }
        private void ApplyAppearance(bool rebuild)
        {
            rebuilding = true; layoutReady = false; timer.Stop(); SuspendLayout(); int selectedPage = currentPage; Size previousSize = ClientSize;
            try {
                if (rebuild) {
                    foreach (Control control in Controls.Cast<Control>().ToArray()) control.Dispose();
                    processes = startups = null; release = selectedRelease = strongRelease = null;
                    views.Clear(); nav.Clear(); responsivePages.Clear(); metricCards.Clear(); naturalSizes.Clear(); layoutScale = 1; Theme.UnitScale = Theme.UserFontScale = 1; BuildLayout();
                }
                layoutScale = monitorDpi / 96f * AppSettings.NormalizeFontScale(settings.FontScale) / 100f;
                Theme.UnitScale = layoutScale;
                Theme.UserFontScale = AppSettings.NormalizeFontScale(settings.FontScale) / 100f;
                foreach (Control control in Controls) Theme.ScaleTree(control, layoutScale);
                foreach (var grid in new[] { processes, startups }) { IntPtr handle = grid.Handle; ResetGridFonts(grid); }
                var area = Screen.FromControl(this).WorkingArea;
                MinimumSize = new Size(280, 240);
                ClientSize = rebuild ? previousSize : new Size(Math.Min((int)(1200 * layoutScale), area.Width - 48), Math.Min((int)(880 * layoutScale), area.Height - 100));
                Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
                SelectPage(selectedPage); FillProcesses(); dashboard.Snapshot = memory;
            } finally { ResumeLayout(true); rebuilding = false; release.Enabled = selectedRelease.Enabled = strongRelease.Enabled = !busy; RememberSizes(this); layoutReady = true; Reflow(); UpdateRefreshInterval(); if (!isolated && IsHandleCreated) timer.Start(); }
        }
        private void BuildLayout()
        {
            shell = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }; Controls.Add(shell);
            shell.Resize += delegate { Reflow(); };
            var side = new Panel { BackColor = Color.White, Margin = Padding.Empty, Padding = new Padding(16, 26, 16, 20), AutoScroll = true }; sidebar = side;
            side.Resize += delegate { side.AutoScrollMinSize = new Size(0, (int)(400 * layoutScale)); };
            shell.Controls.Add(side);
            var brand = Theme.Label("MemSpace", 20, Theme.Accent); brand.Font = Theme.Font(20, FontStyle.Bold); brand.Location = new Point(18, 27); side.Controls.Add(brand);
            string[] names = { "内存概览", "占用排行", "开机排查", "设置" };
            for (int i = 0; i < names.Length; i++)
            {
                int index = i; var b = Theme.Button(names[i]); b.Width = 192; b.Height = 50; b.Location = new Point(16, 108 + i * 62);
                b.TextAlign = ContentAlignment.MiddleLeft; b.Padding = new Padding(16, 0, 0, 0); b.FlatAppearance.BorderSize = 0;
                b.Click += async delegate { SelectPage(index); if (index == 1) await ScanAsync(); if (index == 2) await LoadStartupAsync(); };
                nav.Add(b); side.Controls.Add(b);
            }
            body = new Panel { Margin = Padding.Empty }; shell.Controls.Add(body);
            compactNavigation = Theme.Button("菜单 · 内存概览"); compactNavigation.TextAlign = ContentAlignment.MiddleLeft;
            compactMenu = new ContextMenuStrip();
            for (int i = 0; i < names.Length; i++) { int index = i; compactMenu.Items.Add(names[i], null, async delegate { SelectPage(index); if (index == 1) await ScanAsync(); if (index == 2) await LoadStartupAsync(); }); }
            compactNavigation.Click += delegate { compactMenu.Show(compactNavigation, new Point(0, compactNavigation.Height)); }; body.Controls.Add(compactNavigation);
            var ownedMenu = compactMenu; compactNavigation.Disposed += delegate { ownedMenu.Dispose(); };
            pages = new Panel { Margin = Padding.Empty }; body.Controls.Add(pages);
            status = new Label { Text = "正在读取内存状态…", ForeColor = Theme.Muted, Font = Theme.Font(9), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty }; body.Controls.Add(status);
            var statusTip = new ToolTip(); status.TextChanged += delegate { statusTip.SetToolTip(status, status.Text); }; status.Disposed += delegate { statusTip.Dispose(); };
            BuildHome(); BuildProcesses(); BuildStartup(); BuildSettings(); SelectPage(0);
        }
        private TableLayoutPanel Page(string title, string subtitle, int rows)
        {
            var panel = new Panel { Dock = DockStyle.Fill, Visible = false, AutoScroll = true }; views.Add(panel); pages.Controls.Add(panel);
            var layout = new TableLayoutPanel { Height = 760, ColumnCount = 1, RowCount = rows + 1, Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90)); panel.Controls.Add(layout);
            var heading = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var h = Theme.Label(title, 21, Theme.Ink); h.Font = Theme.Font(21, FontStyle.Bold); h.Location = Point.Empty; heading.Controls.Add(h);
            var s = Theme.Label(subtitle, 9, Theme.Muted); s.Location = new Point(2, 47);
            heading.Controls.Add(s); layout.Controls.Add(heading, 0, 0);
            responsivePages.Add(new ResponsivePage { Viewport = panel, Frame = layout, Header = heading, Title = h, Subtitle = s });
            return layout;
        }
        private void BuildHome()
        {
            var page = Page("内存概览", "", 5);
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 210)); page.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 132)); page.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            dashboard = new Dashboard { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10) }; page.Controls.Add(dashboard, 0, 1);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 5, 0, 0), Margin = Padding.Empty };
            release = Theme.Button("释放后台内存", true); release.Width = 178; release.Click += async delegate { await ReleaseAsync(null, false); };
            strongRelease = Theme.Button("强力释放", true); strongRelease.Width = 150; strongRelease.Click += async delegate { await ReleaseStrongAsync(); };
            var export = Theme.Button("导出诊断报告"); export.Click += async delegate { await ExportAsync(); };
            var hide = Theme.Button("收起到托盘"); hide.Click += delegate { HideToTray(); };
            actions.Controls.Add(release); actions.Controls.Add(strongRelease); actions.Controls.Add(export); actions.Controls.Add(hide); page.Controls.Add(actions, 0, 2);
            var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            metricsLayout = metrics;
            for (int i = 0; i < 3; i++) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
            commitValue = Metric(metrics, 0, "已提交 / 上限", "包含 RAM 和分页文件");
            poolValue = Metric(metrics, 1, "非分页池", "驱动和内核占用");
            reservedValue = Metric(metrics, 2, "硬件保留（估算）", "安装容量减可用总量"); page.Controls.Add(metrics, 0, 3);
            history = new HistoryChart { History = memoryHistory, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 10) }; page.Controls.Add(history, 0, 4);
            var card = new Card { Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 12), Margin = Padding.Empty };
            advice = new Label { Dock = DockStyle.Top, Font = Theme.Font(9), ForeColor = Theme.Muted, Text = "正在读取诊断建议…" };
            advice.TextChanged += delegate { Reflow(); };
            card.Controls.Add(advice); page.Controls.Add(card, 0, 5);
        }
        private Label Metric(TableLayoutPanel layout, int column, string title, string note)
        {
            var card = new Card { Dock = DockStyle.Fill, Margin = new Padding(column == 0 ? 0 : 5, 0, column == 2 ? 0 : 5, 6), Padding = new Padding(14, 10, 8, 8) };
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = Padding.Empty };
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); table.RowStyles.Add(new RowStyle(SizeType.Absolute, 33)); table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var t = Theme.Label(title, 9, Theme.Muted); var v = Theme.Label("—", 14, Theme.Ink); v.Font = Theme.Font(14, FontStyle.Bold);
            var n = new Label { Text = note, Dock = DockStyle.Fill, Font = Theme.Font(7.8f), ForeColor = Theme.Muted };
            table.Controls.Add(t, 0, 0); table.Controls.Add(v, 0, 1); table.Controls.Add(n, 0, 2); card.Controls.Add(table); layout.Controls.Add(card, column, 0); metricCards.Add(card); return v;
        }
        private DataGridView Grid()
        {
            var grid = new DataGridView { Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
                ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false,
                EnableHeadersVisualStyles = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeight = 50, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                GridColor = Theme.Line, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, Margin = new Padding(0, 8, 0, 12) };
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(233, 240, 241), ForeColor = Theme.Ink, Font = Theme.Font(9, FontStyle.Bold), Padding = new Padding(9, 0, 0, 0) };
            grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = Theme.Ink, SelectionBackColor = Color.FromArgb(218, 240, 234), SelectionForeColor = Theme.Ink, Font = Theme.Font(9), Padding = new Padding(9, 0, 0, 0) };
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 251, 252); grid.RowTemplate.Height = 46;
            grid.HandleCreated += delegate { ResetGridFonts(grid); };
            grid.DpiChangedAfterParent += delegate { ResetGridFonts(grid); };
            return grid;
        }
        private static void ResetGridFonts(DataGridView grid)
        {
            var normal = new Font("Microsoft YaHei UI", 13.5f * Theme.UserFontScale, FontStyle.Regular, GraphicsUnit.Point);
            grid.DefaultCellStyle.Font = normal; grid.RowsDefaultCellStyle.Font = normal; grid.AlternatingRowsDefaultCellStyle.Font = normal; grid.RowTemplate.DefaultCellStyle.Font = normal;
            foreach (DataGridViewColumn column in grid.Columns) column.DefaultCellStyle.Font = normal;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 13.5f * Theme.UserFontScale, FontStyle.Bold, GraphicsUnit.Point);
        }
        private void BuildProcesses()
        {
            var page = Page("占用排行", "", 4);
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 60)); page.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            search = new TextBox { Dock = DockStyle.Fill, Font = Theme.Font(11), Margin = new Padding(0, 2, 0, 8) }; search.TextChanged += delegate { FillProcesses(); };
            var filter = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = true };
            var choices = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(10, 0, 0, 0), WrapContents = false };
            groupApplications = Choice("应用汇总", 96); groupProcesses = Choice("单个进程", 96);
            choices.Controls.Add(groupApplications); choices.Controls.Add(groupProcesses);
            groupApplications.Checked = settings.GroupByApplication; groupProcesses.Checked = !settings.GroupByApplication;
            groupApplications.CheckedChanged += delegate { settings.GroupByApplication = groupApplications.Checked; ConfigureProcessColumns(); FillProcesses(); if (!isolated && !rebuilding) try { settings.Save(); } catch (Exception e) { SetStatus("视图设置保存失败：" + e.Message); } };
            var find = Theme.Label("搜索程序", 10, Theme.Muted); find.Margin = new Padding(0, 5, 12, 0); search.Dock = DockStyle.None; search.Width = 240; choices.Dock = DockStyle.None; choices.Width = 210; choices.WrapContents = true;
            filter.Controls.Add(find); filter.Controls.Add(search); filter.Controls.Add(choices); page.Controls.Add(filter, 0, 1);
            processes = Grid();
            ConfigureProcessColumns();
            processes.CellDoubleClick += delegate { ShowSelectedInfo(); }; page.Controls.Add(processes, 0, 2);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            selectedRelease = Theme.Button(settings.GroupByApplication ? "释放选中应用" : "释放选中程序", true); selectedRelease.Width = 170; selectedRelease.Click += async delegate { await ReleaseSelectedAsync(); };
            var exclude = Theme.Button("加入排除名单"); exclude.Click += delegate { AddExclusion(); };
            var refresh = Theme.Button("刷新排行"); refresh.Click += async delegate { await ScanAsync(); };
            var details = Theme.Button("程序信息"); details.Width = 110; details.Click += delegate { ShowSelectedInfo(); };
            actions.Controls.Add(selectedRelease); actions.Controls.Add(exclude); actions.Controls.Add(refresh); actions.Controls.Add(details); page.Controls.Add(actions, 0, 3);
            var note = new Label { Text = "双击查看详情 · 工作集合计包含共享页", Dock = DockStyle.Fill, Font = Theme.Font(9), ForeColor = Theme.Muted };
            page.Controls.Add(note, 0, 4);
        }
        private void BuildStartup()
        {
            var page = Page("开机排查", "", 3);
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 138)); page.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); page.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            var note = new Label { Text = "点击启动设置管理自启动项", Dock = DockStyle.Fill, Font = Theme.Font(9), ForeColor = Theme.Muted }; page.Controls.Add(note, 0, 1);
            startups = Grid(); startups.Columns.Add("Name", "名称"); startups.Columns.Add("State", "状态"); startups.Columns.Add("Source", "来源"); startups.Columns.Add("Command", "启动命令 / 路径");
            startups.Columns[0].FillWeight = 85; startups.Columns[1].FillWeight = 65; startups.Columns[2].FillWeight = 80; startups.Columns[3].FillWeight = 150;
            foreach (DataGridViewColumn column in startups.Columns) column.MinimumWidth = column.Index == 3 ? 230 : 145;
            startups.CellDoubleClick += delegate {
                if (startups.CurrentRow != null) { var item = (StartupItem)startups.CurrentRow.Tag; ShowTextInfo("启动项详情", item.Name + "\r\n" + item.Source + "\r\n状态：" + (item.State ?? "无法读取") + "\r\n\r\n" + item.Command); }
            }; page.Controls.Add(startups, 0, 2);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var windows = Theme.Button("Windows 启动设置", true); windows.Width = 190; windows.Click += delegate { OpenSystem("ms-settings:startupapps"); };
            var task = Theme.Button("任务管理器"); task.Click += delegate { OpenSystem("taskmgr.exe"); };
            var refresh = Theme.Button("刷新启动项"); refresh.Click += async delegate { await LoadStartupAsync(); };
            actions.Controls.Add(windows); actions.Controls.Add(task); actions.Controls.Add(refresh); page.Controls.Add(actions, 0, 3);
        }
        private void BuildSettings()
        {
            loadingSettings = true;
            var page = Page("设置", "", 1);
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var card = new Card { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var content = new TableLayoutPanel { ColumnCount = 1, RowCount = 20, Padding = Padding.Empty, Margin = Padding.Empty };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1)); settingsContent = content;
            var fontRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
            fontRow.Controls.Add(Theme.Label("界面字号", 10, Theme.Ink));
            font100 = Choice("100%", 100); font125 = Choice("125%", 100); font150 = Choice("150%", 100);
            fontRow.Controls.Add(font100); fontRow.Controls.Add(font125); fontRow.Controls.Add(font150);
            font100.Checked = settings.FontScale == 100; font125.Checked = settings.FontScale == 125; font150.Checked = settings.FontScale == 150;
            content.Controls.Add(fontRow);
            auto = Check("自动释放后台内存", settings.AutoRelease); content.Controls.Add(auto);
            autoStrong = Check("自动强力释放（需管理员授权）", settings.AutoStrongRelease); content.Controls.Add(autoStrong);
            gameProtect = Check("游戏 / 全屏时暂停自动强力", settings.GameProtection); content.Controls.Add(gameProtect);
            var authRow = new FlowLayoutPanel { Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
            authorize = Theme.Button("授权管理员"); authorize.Width = 160;
            authorize.Click += async delegate { autoStrong.Checked = true; await SaveSettingsAsync(); };
            authorization = Theme.Label("未授权", 9, Theme.Muted); authRow.Controls.Add(authorize); authRow.Controls.Add(authorization); content.Controls.Add(authRow);
            autoStrong.CheckedChanged += delegate { if (!autoStrong.Checked && !loadingSettings && !rebuilding) StopStrongSession(); UpdateAuthorization(); };
            var thresholdRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 2, 0, 6) };
            var t = Theme.Label("触发占用率", 10, Theme.Muted); t.Margin = new Padding(0, 4, 12, 0);
            threshold = new NumericUpDown { Minimum = 70, Maximum = 95, Value = settings.Threshold, Width = 65, Font = Theme.Font(10) };
            thresholdRow.Controls.Add(t); thresholdRow.Controls.Add(threshold); thresholdRow.Controls.Add(Theme.Label("% · 持续 45 秒 / 间隔 15 分钟", 9, Theme.Muted)); content.Controls.Add(thresholdRow);
            login = Check("登录 Windows 时启动到托盘", !isolated && StartupRegistration.Enabled()); content.Controls.Add(login);
            startupOnce = Check("登录后按需释放一次（延迟 2 分钟）", settings.StartupRelease); content.Controls.Add(startupOnce);
            content.Controls.Add(Theme.Label("排除名单", 12, Theme.Ink));
            autoExclusions = Check("自动识别游戏、录屏与工作软件", settings.AutoExclusions); content.Controls.Add(autoExclusions);
            var exclusionActions = new FlowLayoutPanel { Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
            var choose = Theme.Button("选择应用"); choose.Click += async delegate { await ChooseExclusionsAsync(); }; exclusionActions.Controls.Add(choose);
            exclusionSummary = Theme.Label("", 9, Theme.Muted); exclusionActions.Controls.Add(exclusionSummary); content.Controls.Add(exclusionActions);
            content.Controls.Add(Theme.Label("排除名单仅用于后台释放；强力释放可能短暂卡顿。", 9, Theme.Muted));
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Margin = new Padding(0, 8, 0, 0) };
            var save = Theme.Button("保存设置", true); save.Click += async delegate { await SaveSettingsAsync(); }; buttons.Controls.Add(save);
            var help = Theme.Button("使用帮助"); help.Click += delegate { ShowHelp(); }; buttons.Controls.Add(help); content.Controls.Add(buttons);
            card.Controls.Add(content); page.Controls.Add(card, 0, 1); loadingSettings = false; UpdateAuthorization(); UpdateExclusionSummary();
        }
        private CheckBox Check(string text, bool value)
        {
            var check = new CheckBox { Text = text, Checked = value, AutoSize = false, Height = 32, Dock = DockStyle.Top, Font = Theme.Font(10), ForeColor = Theme.Ink, Margin = new Padding(0, 0, 0, 10) };
            return check;
        }
        private RadioButton Choice(string text, int width)
        {
            var radio = new RadioButton { Appearance = Appearance.Button, Text = text, Width = width, Height = 38, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Font(10), FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Theme.Ink, Margin = new Padding(0, 0, 6, 0) };
            radio.FlatAppearance.BorderColor = Theme.Line;
            radio.CheckedChanged += delegate { radio.BackColor = radio.Checked ? Color.FromArgb(225, 242, 237) : Color.White; radio.ForeColor = radio.Checked ? Theme.Accent : Theme.Ink; };
            return radio;
        }
        private void SelectPage(int index)
        {
            currentPage = index;
            for (int i = 0; i < views.Count; i++)
            { views[i].Visible = i == index; nav[i].BackColor = i == index ? Color.FromArgb(225, 242, 237) : Color.White; nav[i].ForeColor = i == index ? Theme.Accent : Theme.Muted; }
            views[index].BringToFront();
            if (index == 1 && processes != null) ResetGridFonts(processes);
            if (index == 2 && startups != null) ResetGridFonts(startups);
            if (compactNavigation != null) compactNavigation.Text = "菜单 · " + nav[index].Text;
            if (index == 0) UpdateHome();
            if (index == 1) FillProcesses();
            if (index == 3) UpdateAuthorization();
            Reflow();
        }
        private void UpdateHome()
        {
            if (memory == null || !RefreshPolicy.Home(WindowVisible, currentPage)) return;
            dashboard.Snapshot = memory; dashboard.Invalidate(); history.Invalidate();
            string commit = memory.PerformanceAvailable ? (memory.Commit / 1073741824.0).ToString("0.0") + " / " + (memory.CommitLimit / 1073741824.0).ToString("0.0") + " GB" : "无法读取";
            string pool = memory.PerformanceAvailable ? (memory.NonpagedPool / 1048576.0).ToString("0") + " MB" : "无法读取";
            string reserved = memory.Installed >= memory.Total ? MemorySnapshot.Size(memory.Installed - memory.Total) : "未知";
            bool remeasure = commit.Length != commitValue.Text.Length || pool.Length != poolValue.Text.Length || reserved.Length != reservedValue.Text.Length;
            commitValue.Text = commit; poolValue.Text = pool; reservedValue.Text = reserved; advice.Text = Diagnostics.ShortAdvice(memory);
            if (remeasure) Reflow();
        }
        private async Task RefreshAsync()
        {
            if (IsDisposed || refreshing) return; refreshing = true;
            try {
                if (!isolated) RecentActivity.ObserveForeground();
                UpdateRefreshInterval(); memory = MemorySnapshot.Read(); memoryHistory.Add(clock.ElapsedMilliseconds, memory.UsedPercent);
                ReadGameProtection();
                var mode = AutomaticRelease.Mode(settings, strongSession != null && strongSession.Ready);
                bool paused = GameProtection.PausesStrong(settings, mode, gameReason), released = false, pauseChanged = paused != automaticPaused;
                automaticPaused = paused;
                if (!busy && gate.Observe(mode != AutomaticMode.Disabled && !paused, memory.UsedPercent, settings.Threshold, clock.ElapsedMilliseconds)) {
                    if (mode == AutomaticMode.Strong) await ReleaseStrongAsync(true); else await ReleaseAsync(null, true);
                    released = true;
                }
                if (!startupChecked && startupLaunch && clock.ElapsedMilliseconds >= 120000)
                {
                    startupChecked = true;
                    if (settings.StartupRelease && !paused && memory.UsedPercent >= settings.Threshold && !busy && gate.CanAttempt(clock.ElapsedMilliseconds)) {
                        if (mode == AutomaticMode.Strong) await ReleaseStrongAsync(true); else await ReleaseAsync(null, true);
                        released = true;
                    }
                }
                if (IsDisposed) return;
                if (released) { memory = MemorySnapshot.Read(); memoryHistory.Add(clock.ElapsedMilliseconds, memory.UsedPercent); }
                UpdateHome(); UpdateTrayState(); if (WindowVisible && currentPage == 3) UpdateAuthorization();
                if (RefreshPolicy.Processes(WindowVisible, currentPage, busy, clock.ElapsedMilliseconds - lastScan)) await ScanAsync();
                if (!busy && (status.Text == "正在读取内存状态…" || status.Text.StartsWith("游戏保护 ·", StringComparison.Ordinal) || pauseChanged))
                    status.Text = paused ? "游戏保护 · 自动强力暂停" : "已连接 · 自动释放" + (mode == AutomaticMode.Strong ? "：强力" : mode == AutomaticMode.Background ? "：后台" : "关闭");
            } catch (Exception e) { SetStatus("读取失败：" + e.Message); }
            finally { refreshing = false; }
        }
        private async Task ScanAsync()
        {
            if (scanning || IsDisposed) return; scanning = true;
            try { var rows = await Task.Run(() => MemoryService.Scan()); var groups = ApplicationGroup.Build(rows); if (!IsDisposed) { processRows = rows; appGroups = groups; lastScan = clock.ElapsedMilliseconds; FillProcesses(); } }
            catch (Exception e) { if (!IsDisposed) SetStatus("读取进程失败：" + e.Message); }
            finally { scanning = false; }
        }
        private void FillProcesses()
        {
            if (processes == null || loadingSettings || busy || (!isolated && !(WindowVisible && currentPage == 1))) return;
            object selected = processes.CurrentRow == null ? null : processes.CurrentRow.Tag;
            string selectedKey = selected is ApplicationGroup ? ((ApplicationGroup)selected).Key : selected is ProcessRow ? RecentActivity.Identity((ProcessRow)selected) : "";
            var direction = processes.SortOrder; var sort = processes.SortedColumn;
            processes.Rows.Clear(); var policy = MemoryService.Policy(settings, false, processRows);
            if (settings.GroupByApplication) {
                foreach (var group in appGroups.Where(g => g.Members.Any(p => p.Name.IndexOf(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))) {
                    int eligible = group.Members.Count(p => policy.Reason(p) == null);
                    string state = eligible > 0 ? "候选 " + eligible + " / " + group.Members.Count : policy.Reason(group.Members[0]) ?? "均受保护";
                    int i = processes.Rows.Add(group.Name, group.Members.Count, group.WorkingSet / 1048576.0, group.Private / 1048576.0, state);
                    processes.Rows[i].Tag = group; processes.Rows[i].Cells[0].ToolTipText = group.Path;
                }
            } else foreach (var p in processRows.Where(p => p.Name.IndexOf(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)) {
                int i = processes.Rows.Add(p.Name, p.Id, p.WorkingSet / 1048576.0, p.Private / 1048576.0, policy.Reason(p) ?? "候选 · 需验证闲置");
                processes.Rows[i].Tag = p; processes.Rows[i].Cells[0].ToolTipText = p.Path;
            }
            if (sort != null && direction != SortOrder.None) processes.Sort(sort, direction == SortOrder.Ascending ? System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending);
            foreach (DataGridViewRow row in processes.Rows) {
                string key = row.Tag is ApplicationGroup ? ((ApplicationGroup)row.Tag).Key : RecentActivity.Identity((ProcessRow)row.Tag);
                if (key == selectedKey) { row.Selected = true; processes.CurrentCell = row.Cells[0]; break; }
            }
        }
        private void ConfigureProcessColumns()
        {
            if (processes == null) return; processes.Columns.Clear();
            processes.Columns.Add("Name", settings.GroupByApplication ? "应用" : "程序"); processes.Columns.Add("Id", settings.GroupByApplication ? "进程数" : "PID");
            processes.Columns.Add("Working", settings.GroupByApplication ? "工作集* (MB)" : "工作集 (MB)"); processes.Columns.Add("Private", "私有提交 (MB)"); processes.Columns.Add("State", "处理策略");
            processes.Columns[2].HeaderCell.ToolTipText = "应用视图为各进程工作集合计，可能重复计算共享页；单位 MB。";
            float[] weights = { 150, 50, 110, 100, 140 }; int[] minima = { 190, 70, 155, 150, 200 };
            for (int i = 0; i < 5; i++) { processes.Columns[i].FillWeight = weights[i]; processes.Columns[i].MinimumWidth = (int)(minima[i] * (rebuilding ? 1 : layoutScale)); }
            processes.Columns[2].DefaultCellStyle.Format = processes.Columns[3].DefaultCellStyle.Format = "N1";
            if (selectedRelease != null) selectedRelease.Text = settings.GroupByApplication ? "释放选中应用" : "释放选中程序";
        }
        private List<ProcessRow> SelectedMembers()
        {
            if (processes == null || processes.CurrentRow == null) return new List<ProcessRow>();
            var group = processes.CurrentRow.Tag as ApplicationGroup;
            return group == null ? new List<ProcessRow> { (ProcessRow)processes.CurrentRow.Tag } : group.Members.ToList();
        }
        private async Task ReleaseSelectedAsync()
        {
            var members = SelectedMembers(); if (members.Count == 0) return;
            var policy = MemoryService.Policy(settings, true, processRows);
            if (members.All(p => policy.Reason(p) != null)) { ShowTextInfo("进程保护", string.Join("\r\n", members.Select(p => p.Name + "：" + policy.Reason(p)))); return; }
            if (MessageBox.Show(this, "释放选中项中闲置进程的工作集？\r\n\r\n程序不会关闭；可能短暂卡顿或重新载入数据。正在使用的应用家族仍会跳过。", "释放选中项", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK)
                await ReleaseAsync(members, false);
        }
        private async Task ReleaseAsync(List<ProcessRow> selected, bool automatic)
        {
            if (busy || isolated) return; busy = true; release.Enabled = selectedRelease.Enabled = strongRelease.Enabled = false;
            UpdateTrayState();
            gate.MarkManual(clock.ElapsedMilliseconds); SetStatus("正在检查闲置程序并释放工作集…");
            try {
                var result = await Task.Run(() => selected == null ? MemoryService.ReleaseAsync(settings, null, lifetime.Token) : MemoryService.ReleaseSelectionAsync(settings, selected, lifetime.Token));
                if (IsDisposed) return;
                SetStatus(result.Summary());
                try { AppendLog(result); } catch (Exception e) { SetStatus(result.Summary() + "；日志保存失败：" + e.Message); }
                if (tray != null && !Visible && !automatic) tray.ShowBalloonTip(4000, "内存释放完成", result.Summary(), ToolTipIcon.Info);
            } catch (OperationCanceledException) { }
              catch (Exception e) { if (!IsDisposed) SetStatus("释放失败：" + e.Message); }
            finally { busy = false; if (!IsDisposed) { release.Enabled = selectedRelease.Enabled = strongRelease.Enabled = true; UpdateTrayState(); } }
            if (!IsDisposed) await RefreshAsync();
        }
        private void AppendLog(ReleaseResult result)
        {
            Directory.CreateDirectory(AppSettings.DataDirectory); string path = Path.Combine(AppSettings.DataDirectory, "activity.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.WriteAllText(path, "", Encoding.UTF8);
            File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + result.Summary() + "\r\n" +
                "前：" + MemorySnapshot.Size(result.Before.Available) + " / 后：" + MemorySnapshot.Size(result.After.Available) + "\r\n" + string.Join("\r\n", result.Details) + "\r\n\r\n", Encoding.UTF8);
        }
        private async Task ReleaseStrongAsync(bool automatic = false)
        {
            if (automatic) {
                ReadGameProtection();
                if (GameProtection.PausesStrong(settings, AutomaticMode.Strong, gameReason)) {
                    gate.Observe(false, 0, settings.Threshold, clock.ElapsedMilliseconds); SetStatus("游戏保护 · 自动强力暂停"); UpdateTrayState(); return;
                }
            }
            if (automatic && (strongSession == null || !strongSession.Ready)) return;
            if (busy || isolated) return; busy = true; release.Enabled = selectedRelease.Enabled = strongRelease.Enabled = false;
            UpdateAuthorization();
            gate.MarkManual(clock.ElapsedMilliseconds); SetStatus("正在执行强力释放…");
            try {
                if (!automatic && settings.AutoStrongRelease && (strongSession == null || !strongSession.Ready)) {
                    StopStrongSession(); strongSession = await StrongSession.AuthorizeAsync(lifetime.Token);
                }
                string details = strongSession != null && strongSession.Ready ? await strongSession.ReleaseAsync(lifetime.Token) : await StrongRelease.RequestAsync(lifetime.Token); if (IsDisposed) return;
                SetStatus(details.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0]);
                try {
                    Directory.CreateDirectory(AppSettings.DataDirectory); string path = Path.Combine(AppSettings.DataDirectory, "activity.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.WriteAllText(path, "", Encoding.UTF8);
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + details + "\r\n\r\n", Encoding.UTF8);
                } catch (Exception e) { SetStatus(status.Text + "；日志保存失败：" + e.Message); }
                if (details.StartsWith("强力释放未全部完成", StringComparison.Ordinal)) StopStrongSession();
            } catch (System.ComponentModel.Win32Exception e) { if (!IsDisposed) SetStatus(e.NativeErrorCode == 1223 ? "已取消管理员授权，未执行强力释放。" : "强力释放失败：" + e.Message); }
            catch (Exception e) { StopStrongSession(); if (!IsDisposed) SetStatus("强力释放失败：" + e.Message); }
            finally { busy = false; if (!IsDisposed) { release.Enabled = selectedRelease.Enabled = strongRelease.Enabled = true; UpdateAuthorization(); } }
            if (!IsDisposed) await RefreshAsync();
        }
        private async Task LoadStartupAsync()
        {
            try { var items = await Task.Run(() => Diagnostics.StartupItems()); if (IsDisposed) return;
                startups.Rows.Clear(); foreach (var s in items) { int i = startups.Rows.Add(s.Name, s.State ?? "无法读取", s.Source, s.Command); startups.Rows[i].Tag = s; startups.Rows[i].Cells[3].ToolTipText = s.Command; }
            } catch (Exception e) { SetStatus("读取启动项失败：" + e.Message); }
        }
        private async Task ExportAsync()
        {
            using (var dialog = new SaveFileDialog { Filter = "文本报告 (*.txt)|*.txt", FileName = "内存诊断-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { string report = await Task.Run(() => Diagnostics.Report(MemorySnapshot.Read(), MemoryService.Scan(), Diagnostics.StartupItems()));
                    File.WriteAllText(dialog.FileName, report, Encoding.UTF8); SetStatus("报告已保存：" + dialog.FileName);
                } catch (Exception e) { SetStatus("导出失败：" + e.Message); }
            }
        }
        private async Task SaveSettingsAsync()
        {
            if (isolated) return;
            if (busy) { SetStatus("正在释放内存，请完成后再保存设置。"); return; }
            try {
                int newScale = font150.Checked ? 150 : font125.Checked ? 125 : 100;
                bool changed = settings.FontScale != newScale; settings.FontScale = newScale;
                settings.AutoRelease = auto.Checked; settings.Threshold = (int)threshold.Value;
                settings.AutoStrongRelease = autoStrong.Checked; settings.AutoExclusions = autoExclusions.Checked;
                settings.GameProtection = gameProtect.Checked; ReadGameProtection(); UpdateTrayState();
                settings.StartupRelease = startupOnce.Checked;
                if (StartupRegistration.Enabled() != login.Checked) StartupRegistration.Set(login.Checked, Application.ExecutablePath);
                settings.Save();
                if (changed) ApplyAppearance(true);
                gate.Observe(false, 0, settings.Threshold, clock.ElapsedMilliseconds);
                if (!settings.AutoStrongRelease) StopStrongSession();
                else if (strongSession == null || !strongSession.Ready) {
                    StopStrongSession(); busy = true; autoStrong.Enabled = false; release.Enabled = selectedRelease.Enabled = strongRelease.Enabled = false; UpdateAuthorization();
                    try { strongSession = await StrongSession.AuthorizeAsync(lifetime.Token); }
                    catch (System.ComponentModel.Win32Exception e) { SetStatus(e.NativeErrorCode == 1223 ? "授权已取消 · 自动强力未启动" : "授权失败：" + e.Message); return; }
                    catch (Exception e) { SetStatus("授权失败：" + e.Message); return; }
                    finally { busy = false; if (!IsDisposed) { autoStrong.Enabled = true; release.Enabled = selectedRelease.Enabled = strongRelease.Enabled = true; UpdateAuthorization(); } }
                    if (IsDisposed) { StopStrongSession(); return; }
                    if (!autoStrong.Checked) StopStrongSession();
                }
                UpdateAuthorization(); UpdateExclusionSummary(); FillProcesses();
                SetStatus("设置已保存" + (strongSession != null && strongSession.Ready ? " · 自动强力已授权" : ""));
            } catch (Exception e) { MessageBox.Show(this, e.Message, "保存设置失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        private void AddExclusion()
        {
            var members = SelectedMembers(); if (members.Count == 0 || isolated) return;
            try {
                var values = ReleasePolicy.ParseExclusions(settings.Exclusions); foreach (var p in members) values.Add(p.Name);
                settings.Exclusions = string.Join("; ", values.OrderBy(x => x)); settings.Save(); UpdateExclusionSummary();
                FillProcesses(); SetStatus("选中应用的程序已加入排除名单");
            } catch (Exception e) { SetStatus("保存排除名单失败：" + e.Message); }
        }
        private void ShowSelectedInfo()
        {
            var members = SelectedMembers(); if (members.Count == 0) return;
            var policy = MemoryService.Policy(settings, false, processRows);
            ShowTextInfo("应用与进程信息", string.Join("\r\n\r\n", members.Select(p => p.Name + " (PID " + p.Id + "，父进程 " + p.ParentId + ")\r\n" + p.Path + "\r\n工作集：" + MemorySnapshot.Size(p.WorkingSet) + " / 私有提交：" + MemorySnapshot.Size(p.Private) + "\r\n" + (policy.Reason(p) ?? "候选 · 需验证 CPU / I/O 闲置"))));
        }
        private void ShowTextInfo(string title, string text)
        {
            using (var dialog = new Form { Text = title, Icon = Program.AppIcon, StartPosition = FormStartPosition.CenterParent, AutoScaleMode = AutoScaleMode.None }) {
                dialog.ClientSize = new Size(Math.Min((int)(800 * layoutScale), Screen.FromControl(this).WorkingArea.Width - 80), Math.Min((int)(480 * layoutScale), Screen.FromControl(this).WorkingArea.Height - 120));
                var body = new TextBox { Text = text, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, BackColor = Color.White, Font = new Font("Microsoft YaHei UI", 13.5f * layoutScale, FontStyle.Regular, GraphicsUnit.Point) };
                dialog.Controls.Add(body); dialog.ShowDialog(this);
            }
        }
        private void OpenSystem(string target)
        { try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception e) { SetStatus("打开失败：" + e.Message); } }
        private void SetStatus(string text) { if (!IsDisposed) status.Text = text; }
        // Internal UI smoke test: render each real view and verify live data without changing settings or trimming processes.
        internal async Task CaptureViewsAsync(string directory)
        {
            await RefreshAsync(); await ScanAsync(); await LoadStartupAsync();
            if (autoStrong.Checked || !autoExclusions.Checked || !gameProtect.Checked) throw new InvalidOperationException("新增自动选项默认状态错误");
            groupProcesses.Checked = true;
            if (processes.Rows.Count == 0 || !(processes.Rows[0].Tag is ProcessRow)) throw new InvalidOperationException("进程视图切换失败");
            groupApplications.Checked = true;
            if (!(processes.Rows[0].Tag is ApplicationGroup)) throw new InvalidOperationException("应用汇总视图切换失败");
            File.WriteAllText(Path.Combine(directory, "layout.txt"), SelfTests.VerifyWindowDpi(this));
            if (Native.GetDpiForWindow(Handle) != monitorDpi) throw new InvalidOperationException("布局 DPI 与窗口 DPI 不一致");
            if (processRows.Count == 0) throw new InvalidOperationException("进程列表为空");
            foreach (int scale in new[] { 100, 125, 150 }) {
                settings.FontScale = scale; ApplyAppearance(true); await RefreshAsync(); await LoadStartupAsync();
                foreach (Size size in new[] { new Size(1500, 900), new Size(1100, 720), new Size(800, 600), new Size(480, 360), new Size(280, 240), new Size(1500, 900) }) {
                  ClientSize = size;
                  for (int i = 0; i < 4; i++) {
                    SelectPage(i); PerformLayout(); Reflow(); await Task.Delay(100);
                    if (!(scale == 100 ? font100.Checked : scale == 125 ? font125.Checked : font150.Checked)) throw new InvalidOperationException("字号选择未应用");
                    float expected = 13.5f * scale / 100f;
                    float cellFont = processes.Rows[0].Cells[0].InheritedStyle.Font.SizeInPoints;
                    if (Math.Abs(cellFont - expected) > 0.01 || Math.Abs(processes.ColumnHeadersDefaultCellStyle.Font.SizeInPoints - expected) > 0.01)
                        throw new InvalidOperationException("表格字号与选择不一致：scale=" + scale + " cell=" + cellFont + " rows=" + processes.RowsDefaultCellStyle.Font.SizeInPoints + " header=" + processes.ColumnHeadersDefaultCellStyle.Font.SizeInPoints);
                    var page = responsivePages[i];
                    if (page.Frame.Width > page.Viewport.ClientSize.Width || page.Viewport.HorizontalScroll.Visible || pages.Right > body.ClientSize.Width)
                        throw new InvalidOperationException("页面超出窗口：" + size + " page=" + i);
                    CheckControlBounds(page.Frame);
                    page.Viewport.AutoScrollPosition = Point.Empty;
                    using (var bitmap = new Bitmap(Width, Height)) {
                        DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(directory, "scale-" + scale + "-" + size.Width + "x" + size.Height + "-view-" + i + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                    page.Viewport.AutoScrollPosition = new Point(0, page.Frame.Height);
                    if (page.Frame.Height > page.Viewport.ClientSize.Height && page.Frame.Bottom > page.Viewport.ClientSize.Height + 1)
                        throw new InvalidOperationException("页面底部无法滚动到：" + size + " page=" + i);
                    if (size.Width == 480) using (var bitmap = new Bitmap(Width, Height)) {
                        DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(directory, "scale-" + scale + "-480-bottom-view-" + i + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                    page.Viewport.AutoScrollPosition = Point.Empty;
                  }
                }
            }
            foreach (int scale in new[] { 100, 125, 150 }) {
                settings.FontScale = scale; ApplyAppearance(true);
                using (var picker = new ExclusionPicker(ExclusionChoice.Build(appGroups, settings.Exclusions))) {
                    picker.Show(this); await Task.Delay(100); picker.VerifySearchKeepsChoices();
                    foreach (Size size in new[] { new Size(820, 620), new Size(480, 360) }) {
                        picker.ClientSize = size; await Task.Delay(60); CheckControlBounds(picker);
                        using (var bitmap = new Bitmap(picker.Width, picker.Height)) { picker.DrawToBitmap(bitmap, new Rectangle(0, 0, picker.Width, picker.Height)); bitmap.Save(Path.Combine(directory, "picker-" + scale + "-" + size.Width + ".png")); }
                    }
                    picker.Close();
                }
            }
            autoStrong.Checked = true; strongSession = await StrongSession.AuthorizeAsync(lifetime.Token, true); UpdateAuthorization();
            if (authorization.Text != "已授权") throw new InvalidOperationException("授权状态未显示");
            settings.AutoStrongRelease = true; gameReason = "前台游戏"; UpdateAuthorization();
            if (!authorization.Text.Contains("游戏保护暂停")) throw new InvalidOperationException("游戏保护暂停状态未显示");
            settings.GameProtection = false; UpdateAuthorization();
            if (authorization.Text != "已授权") throw new InvalidOperationException("关闭游戏保护未取消暂停状态");
            settings.GameProtection = true; settings.AutoStrongRelease = false; gameReason = "";
            autoStrong.Checked = false;
            if (strongSession != null || authorization.Text != "未授权") throw new InvalidOperationException("关闭复选框未结束授权会话");
            using (var fullscreen = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = Screen.FromControl(this).Bounds }) {
                fullscreen.Show(this); await Task.Delay(100);
                if (!GameProtection.WindowIsFullScreen(fullscreen.Handle)) throw new InvalidOperationException("真实无边框全屏窗口未识别");
                fullscreen.FormBorderStyle = FormBorderStyle.Sizable; fullscreen.WindowState = FormWindowState.Maximized; await Task.Delay(100);
                if (GameProtection.WindowIsFullScreen(fullscreen.Handle)) throw new InvalidOperationException("普通最大化窗口被误判为全屏");
                fullscreen.Close();
            }
            SaveTrayIconPreview(directory);
            await CaptureTrayAsync();
        }
        internal async Task CaptureTrayAsync()
        {
            if (tray == null) BuildTray(); SelectPage(0); ShowWindow(); await Task.Delay(100);
            WindowState = FormWindowState.Minimized; await Task.Delay(100);
            if (Visible || !tray.Visible) throw new InvalidOperationException("最小化未收起到托盘：visible=" + Visible + " tray=" + tray.Visible + " state=" + WindowState);
            if (timer.Interval != 10000 || numberIcon == null || trayPercentage != TrayIcon.Percentage(memory.UsedPercent)) throw new InvalidOperationException("托盘频率或数字图标错误");
            string previousHome = commitValue.Text; int invalidations = 0;
            InvalidateEventHandler invalidated = delegate { invalidations++; }; dashboard.Invalidated += invalidated;
            try { await RefreshAsync(); if (commitValue.Text != previousHome || invalidations != 0) throw new InvalidOperationException("隐藏概览仍在刷新或重绘"); }
            finally { dashboard.Invalidated -= invalidated; }
            Icon retained = numberIcon; UpdateTrayState();
            if (!object.ReferenceEquals(retained, numberIcon)) throw new InvalidOperationException("不变的百分比图标没有复用");
            busy = true; UpdateTrayState();
            if (trayBackground.Enabled || trayStrong.Enabled) throw new InvalidOperationException("忙碌时托盘释放按钮未禁用");
            busy = false; UpdateTrayState();
            if (!trayBackground.Enabled || !trayStrong.Enabled || trayStrong.Text != "强力释放") throw new InvalidOperationException("托盘释放按钮未恢复");
            using (var own = Process.GetCurrentProcess()) {
                uint gdi = Native.GetGuiResources(own.Handle, 0), users = Native.GetGuiResources(own.Handle, 1);
                MemorySnapshot original = memory;
                try {
                    for (int percentage = 0; percentage <= 100; percentage++) {
                        memory = new MemorySnapshot { Total = 100, Available = (ulong)(100 - percentage) }; UpdateTrayState();
                    }
                    if (Native.GetGuiResources(own.Handle, 0) > gdi + 8 || Native.GetGuiResources(own.Handle, 1) > users + 8)
                        throw new InvalidOperationException("重复更新托盘图标造成 GUI 资源增长");
                } finally { memory = original; UpdateTrayState(); }
            }
            ShowWindow(); await Task.Delay(100);
            if (!Visible || tray.Visible || WindowState != FormWindowState.Normal) throw new InvalidOperationException("托盘恢复窗口失败：visible=" + Visible + " tray=" + tray.Visible + " state=" + WindowState);
            if (timer.Interval != 2000 || dashboard.Snapshot != memory) throw new InvalidOperationException("恢复窗口未立即更新数据与刷新频率");
        }
        private static void SaveTrayIconPreview(string directory)
        {
            int[] sizes = { 16, 24, 32, 48 }, values = { 0, 9, 59, 85, 95, 100 };
            using (var preview = new Bitmap(420, 280)) using (var graphics = Graphics.FromImage(preview))
            using (var font = new Font("Segoe UI", 12, GraphicsUnit.Pixel)) {
                graphics.Clear(Color.FromArgb(244, 247, 249));
                for (int row = 0; row < sizes.Length; row++) {
                    graphics.DrawString(sizes[row] + "px", font, Brushes.Black, 4, row * 70 + 24);
                    for (int column = 0; column < values.Length; column++)
                        using (var icon = TrayIcon.Create(values[column], sizes[row])) graphics.DrawIcon(icon, 56 + column * 58, row * 70 + 12);
                }
                preview.Save(Path.Combine(directory, "tray-icons.png"));
            }
        }
        private static void CheckControlBounds(Control control)
        {
            if (control is DataGridView) return; // Columns intentionally scroll inside their grid.
            foreach (Control child in control.Controls) {
                if (!child.Visible) continue;
                if (child.Right > control.ClientSize.Width + 1 || child.Left < 0 || child.Top < 0 || child.Bottom > control.ClientSize.Height + 1)
                    throw new InvalidOperationException("控件被裁切：" + child.GetType().Name + " " + child.Text + " bounds=" + child.Bounds + " parent=" + control.GetType().Name + " " + control.ClientSize);
                CheckControlBounds(child);
            }
        }
    }
}
