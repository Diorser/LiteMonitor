using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;
using LiteMonitor.src.Core;

namespace LiteMonitor
{
    public enum AppTrafficViewMode { Day, Week, Month, Quarter, Year, All }

    /// <summary>
    /// 应用流量统计：与「流量统计中心」同风格，每行一个应用。
    /// 数据来自 AppTrafficLogger（由 AppTrafficTracer 通过 ETW 采集）。
    /// </summary>
    public class AppTrafficForm : Form
    {
        private readonly System.Windows.Forms.Timer _timer;
        private readonly float _scale = 1.0f;

        private Label _lblSpeedUp = null!, _lblSpeedDown = null!, _lblSumUp = null!, _lblSumDown = null!, _lblSumTitle = null!, _lblSumTotal = null!;
        private Label _lblSummary = null!, _lblRange = null!;
        private Button _btnPrev = null!, _btnNext = null!;
        private DataGridView _grid = null!;
        private readonly Dictionary<AppTrafficViewMode, Button> _viewButtons = new();

        private AppTrafficViewMode _mode = AppTrafficViewMode.Day;
        private DateTime _anchor = DateTime.Today;

        // 图标缓存：随窗口释放，关闭后不占内存
        private readonly Dictionary<string, Image> _icons = new(StringComparer.OrdinalIgnoreCase);
        private Image? _defaultIcon;

        // === 配色 (与 TrafficHistoryForm 一致) ===
        private readonly Color C_Back = Color.FromArgb(32, 32, 32);
        private readonly Color C_Panel = Color.FromArgb(45, 45, 45);
        private readonly Color C_GridBack = Color.FromArgb(38, 38, 38);
        private readonly Color C_GridLine = Color.FromArgb(60, 60, 60);
        private readonly Color C_TextMain = Color.FromArgb(230, 230, 230);
        private readonly Color C_TextDim = Color.FromArgb(160, 160, 160);
        private readonly Color C_Header = Color.FromArgb(50, 50, 50);
        private readonly Color C_Accent = Color.FromArgb(0, 122, 204);
        private readonly Color C_Up = Color.FromArgb(80, 160, 255);
        private readonly Color C_Down = Color.FromArgb(80, 220, 120);
        private readonly Color C_BarBg = Color.FromArgb(60, 60, 60);
        private readonly Color C_Highlight = Color.FromArgb(255, 215, 0);

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x02000000; return cp; } // WS_EX_COMPOSITED
        }

        public AppTrafficForm()
        {
            DoubleBuffered = true;
            using (var g = CreateGraphics()) _scale = g.DpiX / 96f;

            Text = "应用流量统计 (App Traffic)";
            Size = new Size(S(1040), S(650));
            MinimumSize = new Size(S(1000), S(480));
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = C_Back;
            ForeColor = C_TextMain;
            Font = new Font("Microsoft YaHei UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            SuspendLayout();
            BuildUI();
            ResumeLayout(true);

            SwitchView(AppTrafficViewMode.Day);

            _timer = new System.Windows.Forms.Timer { Interval = 2000 };
            _timer.Tick += (_, _) => { UpdateSpeed(); LoadGrid(keepState: true); };
            _timer.Start();
        }

        private int S(int px) => (int)(px * _scale);
        private static string Fmt(long bytes) => MetricUtils.FormatDataSize(bytes);
        private static string AppName(string app) => Path.GetFileNameWithoutExtension(app);

        // ================================================================ UI

        private void BuildUI()
        {
            // 1. 仪表盘
            var dash = new Panel { Dock = DockStyle.Top, Height = S(120), BackColor = C_Panel };
            Dash(dash, S(50), "实时速度 (Live Speed)", out _lblSpeedUp, out _lblSpeedDown, out _);
            dash.Controls.Add(new Label { AutoSize = false, Size = new Size(1, S(80)), Location = new Point(S(440), S(25)), BackColor = Color.FromArgb(80, 80, 80) });
            Dash(dash, S(490), "所选时段 (Period Total)", out _lblSumUp, out _lblSumDown, out _lblSumTitle);
            // 总计 (上传 + 下载)
            _lblSumTotal = new Label { Text = "0KB", ForeColor = C_TextMain, Location = new Point(S(860), S(45)), AutoSize = true, Font = new Font("Consolas", 20, FontStyle.Bold) };
            dash.Controls.Add(_lblSumTotal);
            dash.Controls.Add(new Label { Text = "Σ 总计 (Total)", ForeColor = C_TextMain, Location = new Point(S(862), S(85)), AutoSize = true, Font = new Font("Microsoft YaHei UI", 8) });
            Controls.Add(dash);

            // 2. 视图切换 + 时段导航
            var tool = new Panel { Dock = DockStyle.Top, Height = S(55), BackColor = C_Back };
            tool.Controls.Add(new Label { Text = "统计维度 (View):", ForeColor = C_TextDim, Location = new Point(S(20), S(18)), AutoSize = true });
            int x = S(130);
            foreach (var (m, t) in new[] { (AppTrafficViewMode.Day, "日 Day"), (AppTrafficViewMode.Week, "周 Week"), (AppTrafficViewMode.Month, "月 Month"),
                                           (AppTrafficViewMode.Quarter, "季 Quarter"), (AppTrafficViewMode.Year, "年 Year"), (AppTrafficViewMode.All, "全部 All") })
            {
                var b = FlatBtn(t, new Point(x, S(12)), new Size(S(82), S(30)));
                b.Click += (_, _) => SwitchView(m);
                tool.Controls.Add(b);
                _viewButtons[m] = b;
                x += S(86);
            }
            _btnPrev = FlatBtn("◀", new Point(x + S(14), S(12)), new Size(S(30), S(30)));
            _btnPrev.Click += (_, _) => Step(-1);
            _lblRange = new Label
            {
                AutoSize = false, Size = new Size(S(190), S(30)), Location = new Point(x + S(46), S(12)),
                TextAlign = ContentAlignment.MiddleCenter, ForeColor = C_TextMain, Cursor = Cursors.Hand,
                Font = new Font("Consolas", 10.5f, FontStyle.Bold),
            };
            _lblRange.Click += (_, _) => { _anchor = DateTime.Today; LoadGrid(false); };
            new ToolTip().SetToolTip(_lblRange, "点击回到当前时段 (Back to current period)");
            _btnNext = FlatBtn("▶", new Point(x + S(238), S(12)), new Size(S(30), S(30)));
            _btnNext.Click += (_, _) => Step(+1);
            tool.Controls.AddRange(new Control[] { _btnPrev, _lblRange, _btnNext });
            Controls.Add(tool);

            // 3. 底部汇总
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(45), BackColor = C_Panel };
            _lblSummary = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = C_TextMain,
                Font = new Font("Consolas", 11, FontStyle.Bold), Padding = new Padding(0, 0, S(25), 0),
            };
            bottom.Controls.Add(_lblSummary);
            Controls.Add(bottom);

            // 4. 列表
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, BackgroundColor = C_GridBack, BorderStyle = BorderStyle.None,
                EnableHeadersVisualStyles = false, GridColor = C_GridLine, RowHeadersVisible = false,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                ColumnHeadersHeight = S(45), ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                AutoGenerateColumns = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            };
            typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(_grid, true);
            _grid.ColumnHeadersDefaultCellStyle.BackColor = C_Header;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.Gainsboro;
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 10);
            _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
            _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            _grid.DefaultCellStyle.BackColor = C_GridBack;
            _grid.DefaultCellStyle.ForeColor = C_TextMain;
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(70, 70, 70);
            _grid.DefaultCellStyle.SelectionForeColor = Color.White;
            _grid.DefaultCellStyle.Font = new Font("Consolas", 11);
            _grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            _grid.RowTemplate.Height = S(36);

            Col("Rank", "#", 26, DataGridViewContentAlignment.MiddleCenter).DefaultCellStyle.ForeColor = C_TextDim;
            Col("App", "应用 (App)", 140, DataGridViewContentAlignment.MiddleLeft).DefaultCellStyle.Font = new Font("Microsoft YaHei UI", 10);
            Col("Up", "上传 (Up)", 78).DefaultCellStyle.ForeColor = C_Up;
            Col("Down", "下载 (Down)", 82).DefaultCellStyle.ForeColor = C_Down;
            Col("Total", "总计 (Total)", 82);
            var sp = Col("Speed", "当前速度 (Now)", 145);
            sp.DefaultCellStyle.ForeColor = C_TextDim;
            sp.DefaultCellStyle.Font = new Font("Consolas", 9.5f);
            Col("Chart", "流量占比 (Ratio)", 130, DataGridViewContentAlignment.MiddleCenter);

            _grid.CellPainting += Grid_CellPainting;
            _grid.SortCompare += Grid_SortCompare;
            _grid.Sorted += (_, _) =>
            {
                foreach (DataGridViewColumn c in _grid.Columns)
                    c.HeaderCell.SortGlyphDirection = c == _grid.SortedColumn ? _grid.SortOrder : SortOrder.None;
            };
            _grid.ColumnHeaderMouseClick += (_, ev) =>
            {
                var c = _grid.Columns[ev.ColumnIndex];
                bool desc = _grid.SortedColumn != c || _grid.SortOrder == SortOrder.Ascending;
                if (c.Name is "App" or "Rank") desc = !desc; // 文本列默认升序，数值列默认从大到小
                _grid.Sort(c, desc ? ListSortDirection.Descending : ListSortDirection.Ascending);
            };
            _grid.ContextMenuStrip = BuildRowMenu();
            _grid.CellMouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Right && e.RowIndex >= 0) { _grid.ClearSelection(); _grid.Rows[e.RowIndex].Selected = true; }
            };

            Controls.Add(_grid);
            _grid.BringToFront();
        }

        private DataGridViewColumn Col(string name, string header, int weight, DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleRight)
        {
            var c = new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = weight, SortMode = DataGridViewColumnSortMode.Programmatic };
            c.DefaultCellStyle.Alignment = align;
            _grid.Columns.Add(c);
            return c;
        }

        private Button FlatBtn(string text, Point loc, Size size)
        {
            var b = new Button
            {
                Text = text, Location = loc, Size = size, FlatStyle = FlatStyle.Flat, BackColor = C_Back,
                ForeColor = C_TextDim, Cursor = Cursors.Hand, Font = new Font("Microsoft YaHei UI", 9), TabStop = false,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 60, 60);
            return b;
        }

        private void Dash(Panel p, int x, string title, out Label up, out Label down, out Label titleLbl)
        {
            titleLbl = new Label { Text = title, ForeColor = C_TextDim, Location = new Point(x, S(15)), AutoSize = true };
            p.Controls.Add(titleLbl);
            up = new Label { Text = "0KB", ForeColor = C_Up, Location = new Point(x, S(45)), AutoSize = true, Font = new Font("Consolas", 20, FontStyle.Bold) };
            p.Controls.Add(up);
            p.Controls.Add(new Label { Text = "↑ 上传 (Upload)", ForeColor = C_Up, Location = new Point(x + S(2), S(85)), AutoSize = true, Font = new Font("Microsoft YaHei UI", 8) });
            int dx = x + S(185);
            down = new Label { Text = "0KB", ForeColor = C_Down, Location = new Point(dx, S(45)), AutoSize = true, Font = new Font("Consolas", 20, FontStyle.Bold) };
            p.Controls.Add(down);
            p.Controls.Add(new Label { Text = "↓ 下载 (Download)", ForeColor = C_Down, Location = new Point(dx + S(2), S(85)), AutoSize = true, Font = new Font("Microsoft YaHei UI", 8) });
        }

        private ContextMenuStrip BuildRowMenu()
        {
            var m = new ContextMenuStrip();
            var open = new ToolStripMenuItem("打开文件位置 (Open file location)");
            var copy = new ToolStripMenuItem("复制名称 (Copy name)");
            open.Click += (_, _) =>
            {
                var path = SelectedApp() is { } a ? AppTrafficLogger.GetPath(a) : null;
                if (path != null && File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            };
            copy.Click += (_, _) => { if (SelectedApp() is { } a) Clipboard.SetText(a); };
            m.Opening += (_, e) =>
            {
                var a = SelectedApp();
                if (a == null) { e.Cancel = true; return; }
                var path = AppTrafficLogger.GetPath(a);
                open.Enabled = path != null && File.Exists(path);
            };
            m.Items.AddRange(new ToolStripItem[] { open, copy });
            return m;
        }

        private string? SelectedApp() => _grid.SelectedRows.Count > 0 ? (_grid.SelectedRows[0].Tag as RowData)?.App : null;

        // ================================================================ 时段

        private void SwitchView(AppTrafficViewMode mode)
        {
            _mode = mode;
            _anchor = DateTime.Today;
            foreach (var (m, b) in _viewButtons)
            {
                bool on = m == mode;
                b.BackColor = on ? C_Accent : C_Back;
                b.ForeColor = on ? Color.White : C_TextDim;
                b.FlatAppearance.MouseOverBackColor = on ? C_Accent : Color.FromArgb(60, 60, 60);
            }
            UpdateSpeed();
            LoadGrid(keepState: false);
        }

        private void Step(int dir)
        {
            _anchor = _mode switch
            {
                AppTrafficViewMode.Day => _anchor.AddDays(dir),
                AppTrafficViewMode.Week => _anchor.AddDays(7 * dir),
                AppTrafficViewMode.Month => _anchor.AddMonths(dir),
                AppTrafficViewMode.Quarter => _anchor.AddMonths(3 * dir),
                AppTrafficViewMode.Year => _anchor.AddYears(dir),
                _ => _anchor,
            };
            if (_anchor > DateTime.Today) _anchor = DateTime.Today;
            LoadGrid(keepState: false);
        }

        private (DateTime from, DateTime to) CurrentRange()
        {
            var a = _anchor.Date;
            switch (_mode)
            {
                case AppTrafficViewMode.Day: return (a, a);
                case AppTrafficViewMode.Week:
                    var mon = a.AddDays(-(((int)a.DayOfWeek + 6) % 7));
                    return (mon, mon.AddDays(6));
                case AppTrafficViewMode.Month:
                    var m1 = new DateTime(a.Year, a.Month, 1);
                    return (m1, m1.AddMonths(1).AddDays(-1));
                case AppTrafficViewMode.Quarter:
                    var q1 = new DateTime(a.Year, (a.Month - 1) / 3 * 3 + 1, 1);
                    return (q1, q1.AddMonths(3).AddDays(-1));
                case AppTrafficViewMode.Year: return (new DateTime(a.Year, 1, 1), new DateTime(a.Year, 12, 31));
                default: return (DateTime.MinValue, DateTime.MaxValue.Date);
            }
        }

        private string RangeLabel(DateTime f, DateTime t)
        {
            string cur = DateTime.Today >= f && DateTime.Today <= t ? "*" : "";
            return _mode switch
            {
                AppTrafficViewMode.Day => f.ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture) + cur,
                AppTrafficViewMode.Week => $"{f:MM-dd} ~ {t:MM-dd}" + cur,
                AppTrafficViewMode.Month => f.ToString("yyyy-MM") + cur,
                AppTrafficViewMode.Quarter => $"{f.Year}-Q{(f.Month - 1) / 3 + 1}" + cur,
                AppTrafficViewMode.Year => f.ToString("yyyy") + cur,
                _ => "全部 (All)",
            };
        }

        // ================================================================ 数据

        private sealed class RowData
        {
            public string App = "";
            public long Up, Down, Speed, Max, PeriodTotal;
            public long Total => Up + Down;
        }

        private void UpdateSpeed()
        {
            var (u, d) = AppTrafficLogger.GetTotalSpeed();
            _lblSpeedUp.Text = Fmt(u) + "/s";
            _lblSpeedDown.Text = Fmt(d) + "/s";
        }

        private void LoadGrid(bool keepState)
        {
            var (f, t) = CurrentRange();
            var rows = AppTrafficLogger.Query(f, t);
            bool live = DateTime.Today >= f && DateTime.Today <= t;
            bool isAll = _mode == AppTrafficViewMode.All;

            _lblRange.Text = RangeLabel(f, t);
            _btnNext.Enabled = !live;
            _btnNext.ForeColor = live ? Color.FromArgb(80, 80, 80) : C_TextDim;
            _btnPrev.Visible = _btnNext.Visible = !isAll;

            long sumUp = rows.Sum(r => r.Up), sumDown = rows.Sum(r => r.Down);
            _lblSumTitle.Text = $"所选时段 (Period Total) · {_lblRange.Text.TrimEnd('*')}";
            _lblSumUp.Text = Fmt(sumUp);
            _lblSumDown.Text = Fmt(sumDown);
            _lblSumTotal.Text = Fmt(sumUp + sumDown);
            _lblSummary.Text = rows.Count == 0
                ? (AppTrafficTracerRunning() ? "该时段暂无数据 (No data for this period)" : "未在采集：需要管理员权限 (Not recording: administrator rights required)")
                : $"{rows.Count} 个应用 (apps)";

            // 刷新时保持选中/滚动/排序
            string? selected = keepState ? SelectedApp() : null;
            int scroll = keepState && _grid.FirstDisplayedScrollingRowIndex >= 0 ? _grid.FirstDisplayedScrollingRowIndex : 0;
            int curCol = keepState ? (_grid.CurrentCell?.ColumnIndex ?? -1) : -1;
            var sortCol = _grid.SortedColumn ?? _grid.Columns["Total"]; // 默认按总量降序，并显示排序箭头
            var sortDir = _grid.SortedColumn != null ? _grid.SortOrder : SortOrder.Descending;

            long max = rows.Count > 0 ? Math.Max(1, rows.Max(r => r.Up + r.Down)) : 1;
            long periodTotal = Math.Max(1, sumUp + sumDown);

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            int rank = 0;
            foreach (var (app, up, down) in rows)
            {
                var (su, sd) = live ? AppTrafficLogger.GetSpeed(app) : (0, 0);
                long speed = su + sd;
                int i = _grid.Rows.Add(++rank, AppName(app), Fmt(up), Fmt(down), Fmt(up + down),
                                       speed > 0 ? $"↓{Fmt(sd)}/s ↑{Fmt(su)}/s" : "", "");
                var row = _grid.Rows[i];
                row.Tag = new RowData { App = app, Up = up, Down = down, Speed = speed, Max = max, PeriodTotal = periodTotal };
                row.Cells["App"].ToolTipText = AppTrafficLogger.GetPath(app) ?? app;
                if (speed >= 1L << 20) row.Cells["Speed"].Style.ForeColor = C_Highlight; // ≥ 1MB/s
                else if (speed > 0) row.Cells["Speed"].Style.ForeColor = C_TextMain;
            }
            if (sortCol != null && sortDir != SortOrder.None)
                _grid.Sort(sortCol, sortDir == SortOrder.Ascending ? ListSortDirection.Ascending : ListSortDirection.Descending);
            _grid.ResumeLayout();

            _grid.ClearSelection();
            _grid.CurrentCell = null;
            if (selected != null)
                foreach (DataGridViewRow r in _grid.Rows)
                    if ((r.Tag as RowData)?.App == selected)
                    {
                        if (curCol >= 0) _grid.CurrentCell = r.Cells[curCol]; // 保留当前列，表头高亮不丢失
                        r.Selected = true;
                        break;
                    }
            if (scroll > 0 && scroll < _grid.Rows.Count) _grid.FirstDisplayedScrollingRowIndex = scroll;
        }

        private static bool AppTrafficTracerRunning() => src.SystemServices.AppTrafficTracer.IsRunning;

        // ================================================================ 绘制 & 排序

        private void Grid_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.Graphics == null) return;
            if (_grid.Rows[e.RowIndex].Tag is not RowData d) return;
            var col = _grid.Columns[e.ColumnIndex].Name;

            if (col == "App")
            {
                e.Paint(e.CellBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.ContentForeground);
                int sz = S(20);
                var r = e.CellBounds;
                var img = GetIcon(d.App);
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                if (img != null) e.Graphics.DrawImage(img, r.X + S(8), r.Y + (r.Height - sz) / 2, sz, sz);
                bool sel = (e.State & DataGridViewElementStates.Selected) != 0;
                TextRenderer.DrawText(e.Graphics, AppName(d.App), e.CellStyle!.Font, new Rectangle(r.X + S(36), r.Y, r.Width - S(40), r.Height),
                    sel ? Color.White : C_TextMain, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                e.Handled = true;
            }
            else if (col == "Chart")
            {
                e.Paint(e.CellBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.ContentForeground);
                var r = e.CellBounds;
                r.Inflate(-S(10), -S(10));
                var g = e.Graphics;
                using (var bg = new SolidBrush(C_BarBg)) g.FillRectangle(bg, r);

                // 条长相对第一名，文字为占本时段总量的百分比
                int w = (int)(r.Width * ((double)d.Total / d.Max));
                if (w < 3 && d.Total > 0) w = 3;
                int wUp = d.Total > 0 ? (int)(w * (double)d.Up / d.Total) : 0;
                using (var bu = new SolidBrush(C_Up)) if (wUp > 0) g.FillRectangle(bu, r.X, r.Y, wUp, r.Height);
                using (var bd = new SolidBrush(C_Down)) if (w - wUp > 0) g.FillRectangle(bd, r.X + wUp, r.Y, w - wUp, r.Height);

                using var f = new Font("Consolas", 8.5f, FontStyle.Bold);
                TextRenderer.DrawText(g, $"{d.Total * 100.0 / d.PeriodTotal:0.0}%", f, new Rectangle(r.X, r.Y - 1, r.Width - S(4), r.Height + 2),
                    Color.White, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                e.Handled = true;
            }
        }

        private void Grid_SortCompare(object? sender, DataGridViewSortCompareEventArgs e)
        {
            var a = _grid.Rows[e.RowIndex1].Tag as RowData;
            var b = _grid.Rows[e.RowIndex2].Tag as RowData;
            if (a == null || b == null) return;
            e.SortResult = e.Column.Name switch
            {
                "App" => string.Compare(a.App, b.App, StringComparison.OrdinalIgnoreCase),
                "Up" => a.Up.CompareTo(b.Up),
                "Down" => a.Down.CompareTo(b.Down),
                "Speed" => a.Speed.CompareTo(b.Speed),
                _ => a.Total.CompareTo(b.Total),
            };
            if (e.SortResult == 0) e.SortResult = b.Total.CompareTo(a.Total);
            e.Handled = true;
        }

        private Image? GetIcon(string app)
        {
            if (_icons.TryGetValue(app, out var img)) return img;
            var path = AppTrafficLogger.GetPath(app);
            _defaultIcon ??= Shrink(SystemIcons.Application);
            if (path == null) return _defaultIcon; // 路径稍后才可能出现，不缓存

            Image? result = null;
            try
            {
                if (File.Exists(path))
                    using (var ico = Icon.ExtractAssociatedIcon(path))
                        if (ico != null) result = Shrink(ico);
            }
            catch { }
            _icons[app] = result ?? _defaultIcon; // 失败也缓存，避免每次重绘都去读文件
            return _icons[app];
        }

        private Bitmap Shrink(Icon ico)
        {
            using var big = ico.ToBitmap();
            return new Bitmap(big, S(20), S(20));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _grid.ClearSelection(); // DataGridView 首次显示会自动选中第一行
            _grid.CurrentCell = null;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            _timer.Dispose();
            foreach (var i in _icons.Values) if (i != _defaultIcon) i.Dispose();
            _icons.Clear();
            _defaultIcon?.Dispose();
            base.OnFormClosed(e);
        }
    }
}
