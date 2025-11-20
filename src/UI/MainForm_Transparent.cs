using LiteMonitor.src.Core;
using LiteMonitor.src.System;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LiteMonitor
{
    public class MainForm : Form
    {
        private readonly Settings _cfg = Settings.Load();
        private UIController? _ui;
        private readonly NotifyIcon _tray = new();
        private Point _dragOffset;

        // 防止 Win11 自动隐藏无边框 + 无任务栏窗口
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;

                // WS_EX_TOOLWINDOW: 防止被系统降为后台工具窗口 → 解决“失焦后自动消失”
                cp.ExStyle |= 0x00000080;

                // 可选：避免 Win11 某些情况错误认为是 AppWindow
                cp.ExStyle &= ~0x00040000; // WS_EX_APPWINDOW

                return cp;
            }
        }

        // ========== 鼠标穿透支持 ==========
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;

        private void SetClickThrough(bool enable)
        {
            try
            {
                int ex = GetWindowLong(Handle, GWL_EXSTYLE);
                if (enable)
                    SetWindowLong(Handle, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED);
                else
                    SetWindowLong(Handle, GWL_EXSTYLE, ex & ~WS_EX_TRANSPARENT);
            }
            catch { }
        }

        // ========== 自动隐藏功能 ==========
        private System.Windows.Forms.Timer? _autoHideTimer;
        private bool _isHidden = false;
        private int _hideWidth = 4;
        private int _hideThreshold = 10;
        private enum DockEdge { None, Left, Right }
        private DockEdge _dock = DockEdge.None;
        private bool _uiDragging = false;

        private void InitAutoHideTimer()
        {
            _autoHideTimer ??= new System.Windows.Forms.Timer { Interval = 250 };
            _autoHideTimer.Tick -= AutoHideTick;
            _autoHideTimer.Tick += AutoHideTick;
            _autoHideTimer.Start();
        }
        private void StopAutoHideTimer() => _autoHideTimer?.Stop();
        private void AutoHideTick(object? sender, EventArgs e) => CheckAutoHide();

        private void CheckAutoHide()
        {
            if (!_cfg.AutoHide) return;
            if (!Visible) return;
            if (_uiDragging || ContextMenuStrip?.Visible == true) return;

            var area = Screen.PrimaryScreen!.WorkingArea;
            var cursor = Cursor.Position;

            bool nearLeft = Left <= area.Left + _hideThreshold;
            bool nearRight = area.Right - Right <= _hideThreshold;

            if (!_isHidden && (nearLeft || nearRight) && !Bounds.Contains(cursor))
            {
                if (nearRight)
                {
                    Left = area.Right - _hideWidth;
                    _dock = DockEdge.Right;
                }
                else
                {
                    Left = area.Left - (Width - _hideWidth);
                    _dock = DockEdge.Left;
                }
                _isHidden = true;
                return;
            }

            if (_isHidden)
            {
                const int hoverBand = 30;
                if (_dock == DockEdge.Right && cursor.X >= area.Right - hoverBand)
                {
                    Left = area.Right - Width;
                    _isHidden = false;
                    _dock = DockEdge.None;
                }
                else if (_dock == DockEdge.Left && cursor.X <= area.Left + hoverBand)
                {
                    Left = area.Left;
                    _isHidden = false;
                    _dock = DockEdge.None;
                }
            }
        }

        // ========== 构造函数 ==========
        public MainForm()
        {
            // === 自动检测系统语言（仅在首次启动或配置缺失时使用） ===
            string sysLang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLower();
            string langDir = Path.Combine(AppContext.BaseDirectory, "resources/lang");
            bool sysLangAvailable = File.Exists(Path.Combine(langDir, $"{sysLang}.json"));

            // 如果已有设置文件且语言有效，则尊重用户选择；否则按系统语言/英语兜底
            string settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
            bool hasSettingsFile = File.Exists(settingsPath);
            bool savedLangAvailable = !string.IsNullOrWhiteSpace(_cfg.Language)
                                      && File.Exists(Path.Combine(langDir, $"{_cfg.Language}.json"));

            if (!hasSettingsFile || !savedLangAvailable)
            {
                _cfg.Language = sysLangAvailable ? sysLang : "en";
                _cfg.Save();
            }

            // 语言与主题的加载交给 UIController.ApplyTheme 统一处理

            // 宽度只认 Settings；真正的主题宽度覆盖在 UIController.ApplyTheme 内执行
            Width = _cfg.PanelWidth > 100 ? _cfg.PanelWidth : Width;


            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = _cfg.TopMost;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoScaleMode = AutoScaleMode.Dpi;


            // === 托盘图标 ===
            this.Icon = Properties.Resources.AppIcon;
            _tray.Icon = this.Icon;
            _tray.Visible = true;
            _tray.Text = "LiteMonitor";


            // 将 _cfg 传递给 UIController（构造内会统一加载语言与主题，并应用宽度等）
            _ui = new UIController(_cfg, this);

            // 现在主题已可用，再设置背景色与菜单
            BackColor = ThemeManager.ParseColor(ThemeManager.Current.Color.Background);

            _tray.ContextMenuStrip = BuildContextMenu();
            ContextMenuStrip = _tray.ContextMenuStrip;


            // === 拖拽移动 ===
            MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    _ui?.SetDragging(true);
                    _uiDragging = true;
                    _dragOffset = e.Location;
                }
            };
            MouseMove += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    if (Math.Abs(e.X - _dragOffset.X) + Math.Abs(e.Y - _dragOffset.Y) < 1) return;
                    Location = new Point(Left + e.X - _dragOffset.X, Top + e.Y - _dragOffset.Y);
                }
            };
            MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    _ui?.SetDragging(false);
                    _uiDragging = false;
                    SavePos();
                }
            };

            // === 渐入透明度 ===
            Opacity = 0;
            double targetOpacity = Math.Clamp(_cfg.Opacity, 0.1, 1.0);
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    while (Opacity < targetOpacity)
                    {
                        await System.Threading.Tasks.Task.Delay(16).ConfigureAwait(false);
                        BeginInvoke(new Action(() => Opacity = Math.Min(targetOpacity, Opacity + 0.05)));
                    }
                }
                catch { }
            });

            ApplyRoundedCorners();
            this.Resize += (_, __) => ApplyRoundedCorners();

            // === 状态恢复 ===
            if (_cfg.ClickThrough) SetClickThrough(true);
            if (_cfg.AutoHide) InitAutoHideTimer();
        }

        // ========== 初始化位置 ==========
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // 确保窗体尺寸已初始化
            this.Update();

            var screen = Screen.FromControl(this);
            var area = screen.WorkingArea;

            if (_cfg.Position.X >= 0)
            {
                Location = _cfg.Position;
            }
            else
            {
                int x = area.Right - Width - 50; // 距右边留白
                int y = area.Top + (area.Height - Height) / 2; // 垂直居中
                Location = new Point(x, y);
            }

            // ✅ 启动时静默检查更新（不打扰用户）
            _ = UpdateChecker.CheckAsync();
        }


        protected override void OnPaint(PaintEventArgs e) => _ui?.Render(e.Graphics);

        private void SavePos()
        {
            _cfg.Position = new Point(Left, Top);
            _cfg.Save();
        }

        // ========== 构建菜单（国际化） ==========
        private ContextMenuStrip BuildContextMenu()
        {
            var menu = new ContextMenuStrip();

            // === 置顶 ===
            var topMost = new ToolStripMenuItem(LanguageManager.T("Menu.TopMost")) { Checked = _cfg.TopMost, CheckOnClick = true };
            topMost.CheckedChanged += (_, __) =>
            {
                TopMost = _cfg.TopMost = topMost.Checked;
                _cfg.Save();
            };
            menu.Items.Add(topMost);
            menu.Items.Add(new ToolStripSeparator());

            // === 透明度 ===
            var opacityRoot = new ToolStripMenuItem(LanguageManager.T("Menu.Opacity"));
            double[] presetOps = { 1.0, 0.95, 0.9, 0.85, 0.8, 0.75, 0.7, 0.6, 0.5, 0.4, 0.3 };
            foreach (var val in presetOps)
            {
                var opItem = new ToolStripMenuItem($"{val * 100:0}%")
                {
                    Checked = Math.Abs(_cfg.Opacity - val) < 0.01
                };
                opItem.Click += (_, __) =>
                {
                    _cfg.Opacity = val;
                    this.Opacity = Math.Clamp(val, 0.1, 1.0);
                    foreach (ToolStripMenuItem other in opacityRoot.DropDownItems)
                        other.Checked = false;
                    opItem.Checked = true;
                    _cfg.Save();
                };
                opacityRoot.DropDownItems.Add(opItem);
            }
            menu.Items.Add(opacityRoot);

            // === 显示项 ===
            var grpShow = new ToolStripMenuItem(LanguageManager.T("Menu.ShowItems"));
            menu.Items.Add(grpShow);
            void AddToggle(string key, Func<bool> get, Action<bool> set)
            {
                var item = new ToolStripMenuItem(LanguageManager.T(key)) { Checked = get(), CheckOnClick = true };
                item.CheckedChanged += (_, __) =>
                {
                    set(item.Checked);
                    _cfg.Save();
                    _ui?.ApplyTheme(_cfg.Skin);
                };
                grpShow.DropDownItems.Add(item);
            }
            AddToggle("Items.CPU.Load", () => _cfg.Enabled.CpuLoad, v => _cfg.Enabled.CpuLoad = v);
            AddToggle("Items.CPU.Temp", () => _cfg.Enabled.CpuTemp, v => _cfg.Enabled.CpuTemp = v);
            AddToggle("Items.GPU.Load", () => _cfg.Enabled.GpuLoad, v => _cfg.Enabled.GpuLoad = v);
            AddToggle("Items.GPU.Temp", () => _cfg.Enabled.GpuTemp, v => _cfg.Enabled.GpuTemp = v);
            AddToggle("Items.GPU.VRAM", () => _cfg.Enabled.GpuVram, v => _cfg.Enabled.GpuVram = v);
            AddToggle("Items.MEM.Load", () => _cfg.Enabled.MemLoad, v => _cfg.Enabled.MemLoad = v);

            // 整组控制（推荐写法，最简）
            AddToggle("Groups.DISK",
                () => _cfg.Enabled.DiskRead || _cfg.Enabled.DiskWrite,
                v => { _cfg.Enabled.DiskRead = v; _cfg.Enabled.DiskWrite = v; });

            AddToggle("Groups.NET",
                () => _cfg.Enabled.NetUp || _cfg.Enabled.NetDown,
                v => { _cfg.Enabled.NetUp = v; _cfg.Enabled.NetDown = v; });


            // === 主题 ===
            var themeRoot = new ToolStripMenuItem(LanguageManager.T("Menu.Theme"));
            foreach (var name in ThemeManager.GetAvailableThemes())
            {
                var item = new ToolStripMenuItem(name)
                {
                    Checked = string.Equals(name, _cfg.Skin, StringComparison.OrdinalIgnoreCase)
                };
                item.Click += (_, __) =>
                {
                    _cfg.Skin = name;
                    _cfg.Save();
                    foreach (ToolStripMenuItem other in themeRoot.DropDownItems) other.Checked = false;
                    item.Checked = true;
                    _ui?.ApplyTheme(name);
                };
                themeRoot.DropDownItems.Add(item);
            }
            menu.Items.Add(themeRoot);
            menu.Items.Add(new ToolStripSeparator());


            // === 更多 ===
            var moreRoot = new ToolStripMenuItem(LanguageManager.T("Menu.More"));

            // === 鼠标穿透 ===
            var clickThrough = new ToolStripMenuItem(LanguageManager.T("Menu.ClickThrough")) { Checked = _cfg.ClickThrough, CheckOnClick = true };
            clickThrough.CheckedChanged += (_, __) =>
            {
                _cfg.ClickThrough = clickThrough.Checked;
                SetClickThrough(clickThrough.Checked);
                _cfg.Save();
            };
            moreRoot.DropDownItems.Add(clickThrough);

            // === 自动隐藏 ===
            var autoHide = new ToolStripMenuItem(LanguageManager.T("Menu.AutoHide")) { Checked = _cfg.AutoHide, CheckOnClick = true };
            autoHide.CheckedChanged += (_, __) =>
            {
                _cfg.AutoHide = autoHide.Checked;
                if (_cfg.AutoHide) InitAutoHideTimer();
                else StopAutoHideTimer();
                _cfg.Save();
            };
            moreRoot.DropDownItems.Add(autoHide);

            // === 界面宽度 ===
            var widthRoot = new ToolStripMenuItem(LanguageManager.T("Menu.Width"));
            int[] presetWidths = { 180,200, 220, 240, 260, 280, 300, 360,420 };

            // 当前宽度：优先取配置，没有就用主题
            int currentW = _cfg.PanelWidth > 0 ? _cfg.PanelWidth : ThemeManager.Current.Layout.Width;

            foreach (var w in presetWidths)
            {
                var widthItem = new ToolStripMenuItem($"{w}px")
                {
                    // ✅ 勾选状态以“当前实际宽度”为准
                    Checked = Math.Abs(currentW - w) < 1
                };
                widthItem.Click += (_, __) =>
                {
                    // 1) 持久化用户选择
                    _cfg.PanelWidth = w;
                    _cfg.Save();

                    // 2) 统一入口：交给 ApplyTheme 按 Settings 覆盖主题宽度并刷新布局
                    _ui?.ApplyTheme(_cfg.Skin);


                    // 3) 同步菜单勾选
                    foreach (ToolStripMenuItem other in widthRoot.DropDownItems) other.Checked = false;
                    widthItem.Checked = true;
                };
                widthRoot.DropDownItems.Add(widthItem);
            }
            moreRoot.DropDownItems.Add(widthRoot);


            menu.Items.Add(moreRoot);
            menu.Items.Add(new ToolStripSeparator());


            

            // === 语言切换 ===
            var langRoot = new ToolStripMenuItem(LanguageManager.T("Menu.Language"));
            var langDir = Path.Combine(AppContext.BaseDirectory, "resources/lang");
            if (Directory.Exists(langDir))
            {
                foreach (var file in Directory.EnumerateFiles(langDir, "*.json"))
                {
                    string code = Path.GetFileNameWithoutExtension(file);
                    var item = new ToolStripMenuItem(code.ToUpper())
                    {
                        Checked = _cfg.Language.Equals(code, StringComparison.OrdinalIgnoreCase)
                    };
                    item.Click += (_, __) =>
                    {
                        _cfg.Language = code;
                        _cfg.Save();

                        // 统一入口：ApplyTheme 内部加载语言与主题
                        _ui?.ApplyTheme(_cfg.Skin);
                        Invalidate();

                        // ✅ 重建菜单并同步到托盘与窗体
                        var newMenu = BuildContextMenu();
                        _tray.ContextMenuStrip = newMenu;
                        this.ContextMenuStrip = newMenu;
                    };

                    langRoot.DropDownItems.Add(item);
                }
            }
            menu.Items.Add(langRoot);
            menu.Items.Add(new ToolStripSeparator());

            // === 自启动 ===
            var autoStart = new ToolStripMenuItem(LanguageManager.T("Menu.AutoStart")) { Checked = _cfg.AutoStart, CheckOnClick = true };
            autoStart.CheckedChanged += (_, __) =>
            {
                _cfg.AutoStart = autoStart.Checked;
                _cfg.Save();
                AutoStart.Set(_cfg.AutoStart);
            };
            menu.Items.Add(autoStart);
            menu.Items.Add(new ToolStripSeparator());

            // === 检查更新 ===
            var checkUpdate = new ToolStripMenuItem(LanguageManager.T("Menu.CheckUpdate"));
            checkUpdate.Click += async (_, __) => await UpdateChecker.CheckAsync(showMessage: true);
            menu.Items.Add(checkUpdate);

            // === 关于 ===
            var about = new ToolStripMenuItem(LanguageManager.T("Menu.About"));
            about.Click += (_, __) => new AboutForm().ShowDialog(this);
            menu.Items.Add(about);
            menu.Items.Add(new ToolStripSeparator());

            // === 退出 ===
            var exit = new ToolStripMenuItem(LanguageManager.T("Menu.Exit"));
            exit.Click += (_, __) => Close();
            menu.Items.Add(exit);

            return menu;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            _ui?.Dispose();
            _tray.Visible = false;
        }

        private void ApplyRoundedCorners()
        {
            try
            {
                var t = ThemeManager.Current;
                int r = Math.Max(0, t.Layout.CornerRadius);
                using var gp = new System.Drawing.Drawing2D.GraphicsPath();
                int d = r * 2;
                gp.AddArc(0, 0, d, d, 180, 90);
                gp.AddArc(Width - d, 0, d, d, 270, 90);
                gp.AddArc(Width - d, Height - d, d, d, 0, 90);
                gp.AddArc(0, Height - d, d, d, 90, 90);
                gp.CloseFigure();
                Region?.Dispose();
                Region = new Region(gp);
            }
            catch { }
        }
    }
}
