using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using System.Runtime.InteropServices;
using System.Diagnostics;
using LiteMonitor.src.UI;
using System.Drawing.Drawing2D;

namespace LiteMonitor
{
    public class MainForm : Form
    {
        private readonly Settings _cfg = Settings.Load();
        private UIController? _ui;
        private readonly NotifyIcon _tray = new();
        private Point _dragOffset;

        // =================================================================
        // ★★★ 新增：Win11 原生圆角 API 定义 (DWM)
        // =================================================================
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_DEFAULT = 0;
        private const int DWMWCP_DONT_ROUND = 1;
        private const int DWMWCP_ROUND = 2;
        private const int DWMWCP_ROUNDSMALL = 3;

        // ★★★ 修复：使用 unchecked 处理 uint 到 int 的溢出转换
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE); 

        // =================================================================

        // 防止 Win11 自动隐藏无边框 + 无任务栏窗口
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // WS_EX_TOOLWINDOW: 防止被系统降为后台工具窗口
                cp.ExStyle |= 0x80;
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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;

        public void SetClickThrough(bool enable)
        {
            try
            {
                int ex = GetWindowLong(Handle, GWL_EXSTYLE);
                if (enable)
                    // 开启穿透：必须叠加 Layered 属性，否则 Transparent 可能无效
                    SetWindowLong(Handle, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED);
                else
                    // 关闭穿透
                    SetWindowLong(Handle, GWL_EXSTYLE, ex & ~WS_EX_TRANSPARENT);
            }
            catch { }
        }

        // ========== 自动隐藏功能 ==========
        private System.Windows.Forms.Timer? _autoHideTimer;
        private bool _isHidden = false;
        private int _hideWidth = 4;
        private int _hideThreshold = 10;
        private enum DockEdge { None, Left, Right, Top, Bottom }
        private DockEdge _dock = DockEdge.None;
        private bool _uiDragging = false;
        private DateTime _keepVisibleUntil = DateTime.MinValue; // 保护期截止时间

        public void InitAutoHideTimer()
        {
            _autoHideTimer ??= new System.Windows.Forms.Timer { Interval = 250 };
            _autoHideTimer.Tick -= AutoHideTick;
            _autoHideTimer.Tick += AutoHideTick;
            _autoHideTimer.Start();
        }
        public void StopAutoHideTimer() => _autoHideTimer?.Stop();
        private void AutoHideTick(object? sender, EventArgs e) => CheckAutoHide();

        private void CheckAutoHide()
        {
            if (!_cfg.AutoHide) return;
            if (!Visible) return;
            if (_uiDragging || ContextMenuStrip?.Visible == true) return;
            // ★★★ 优化：如果在保护期内，暂时不执行隐藏逻辑 ★★★
            if (DateTime.Now < _keepVisibleUntil) return;

            // ==== 关键修改：基于"当前窗体所在屏幕"计算区域 ====
            var center = new Point(Left + Width / 2, Top + Height / 2);
            var screen = Screen.FromPoint(center);
            var area = screen.WorkingArea;

            var cursor = Cursor.Position;

            // ===== 无论横竖模式都支持上、左、右三边靠边隐藏 =====
            bool nearLeft = Left <= area.Left + _hideThreshold;
            bool nearRight = area.Right - Right <= _hideThreshold;
            bool nearTop = Top <= area.Top + _hideThreshold;
            //bool nearBottom = area.Bottom - Bottom <= _hideThreshold; //下方不隐藏 会和任务量冲突

            // ===== 是否应该隐藏 =====
            bool shouldHide = nearLeft || nearRight || nearTop;

            // ===== 靠边 → 自动隐藏 =====
            if (!_isHidden && shouldHide && !Bounds.Contains(cursor))
            {
                // ========= 统一处理上、左、右三边隐藏 =========
                if (nearRight)
                {
                    Left = area.Right - _hideWidth;
                    _dock = DockEdge.Right;
                }
                else if (nearLeft)
                {
                    Left = area.Left - (Width - _hideWidth);
                    _dock = DockEdge.Left;
                }
                else if (nearTop)
                {
                    Top = area.Top - (Height - _hideWidth);
                    _dock = DockEdge.Top;
                }

                _isHidden = true;
                return;
            }

            // ===== 已隐藏 → 鼠标靠边 → 弹出 =====
            if (_isHidden)
            {
                const int hoverBand = 30;

                // 关键修复：只有当鼠标在隐藏的面板区域内时，才显示面板
                bool isMouseOnHiddenPanel = false;

                // ========= 统一处理上、左、右三边检测 =========
                if (_dock == DockEdge.Right)
                    isMouseOnHiddenPanel = cursor.X >= area.Right - _hideWidth && cursor.Y >= Top && cursor.Y <= Top + Height;
                else if (_dock == DockEdge.Left)
                    isMouseOnHiddenPanel = cursor.X <= area.Left + _hideWidth && cursor.Y >= Top && cursor.Y <= Top + Height;
                else if (_dock == DockEdge.Top)
                    isMouseOnHiddenPanel = cursor.Y <= area.Top + _hideWidth && cursor.X >= Left && cursor.X <= Left + Width;

                if (isMouseOnHiddenPanel)
                {
                    // ======== 统一处理上、左、右三边弹出 ========
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
                    else if (_dock == DockEdge.Top && cursor.Y <= area.Top + hoverBand)
                    {
                        Top = area.Top;
                        _isHidden = false;
                        _dock = DockEdge.None;
                    }
                }
            }
        }

        // ==== 任务栏显示 ====
        private TaskbarForm? _taskbar;

        public void ToggleTaskbar(bool show)
        {
            if (show)
            {
                // ★★★ 核心修复：检查目标屏幕是否发生了变化 ★★★
                if (_taskbar != null && !_taskbar.IsDisposed)
                {
                    // 如果当前运行的任务栏窗口所在的屏幕，与配置中的不一致
                    // 或者配置变成了 "" (自动)，但当前锁死在某个设备上
                    // 则关闭旧窗口，强制重建
                    if (_taskbar.TargetDevice != _cfg.TaskbarMonitorDevice)
                    {
                        _taskbar.Close();
                        _taskbar.Dispose();
                        _taskbar = null;
                    }
                }

                if (_taskbar == null || _taskbar.IsDisposed)
                {
                    if (_ui != null)
                    {
                        _taskbar = new TaskbarForm(_cfg, _ui, this);
                        _taskbar.Show();
                    }
                }
                else
                {
                    // 只是显隐切换，不需要重建
                    if (!_taskbar.Visible)
                    {
                        _taskbar.Show();
                        // 额外调用一次 Reload 以确保颜色/字体等其他非屏幕配置也刷新
                        _taskbar.ReloadLayout();
                    }
                }
            }
            else
            {
                if (_taskbar != null)
                {
                    _taskbar.Close();
                    _taskbar.Dispose();
                    _taskbar = null;
                }
            }
        }

        // ========== 构造函数 ==========
        public MainForm()
        {
            // 如果用户未设置过语言（首次启动），则使用系统默认语言
            if (string.IsNullOrEmpty(_cfg.Language))
            {
                // === 自动检测系统语言 ===
                string sysLang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLower();
                string langPath = Path.Combine(AppContext.BaseDirectory, "resources/lang", $"{sysLang}.json");
                _cfg.Language = File.Exists(langPath) ? sysLang : "en";
            }

            // ★★★【新增】补救措施 1：启动时必须手动加载一次语言 ★★★
            // 既然 UIController.ApplyTheme 不再负责加载语言，这里必须显式调用！
            LanguageManager.Load(_cfg.Language);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = _cfg.TopMost;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            // 开启透明背景支持 (对 Win10 Region 方案有帮助)
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            
            AutoScaleMode = AutoScaleMode.Dpi;

            // 1. 加载历史流量数据
            TrafficLogger.Load();


            // === 托盘图标 ===
            this.Icon = Properties.Resources.AppIcon;
            _tray.Icon = this.Icon;
            _tray.Visible = !_cfg.HideTrayIcon;
            _tray.Text = "LiteMonitor";


            // 将 _cfg 传递给 UIController（构造内会统一加载语言与主题，并应用宽度等）
            _ui = new UIController(_cfg, this);
            // ★★★ 初始化 WebServer 单例 (此时不启动，只创建对象) ★★★
            new src.WebServer.LiteWebServer(_cfg);

            // 现在主题已可用，再设置背景色与菜单
            BackColor = ThemeManager.ParseColor(ThemeManager.Current.Color.Background);

            // 1. 只把菜单生成出来，赋值给窗体备用（但不赋值给 _tray.ContextMenuStrip）
            ContextMenuStrip = MenuManager.Build(this, _cfg, _ui);

            // 2. 手动监听托盘的鼠标抬起事件
            _tray.MouseUp += (_, e) =>
            {
                // 仅响应右键
                if (e.Button == MouseButtons.Right)
                {
                    // ★关键步骤A：必须先激活一下主窗口（即使它是隐藏的），
                    // 否则菜单弹出后，点击屏幕其他地方菜单不会自动消失

                    // 注意：这里使用 Win32 API 激活可能比 this.Activate() 更稳，
                    // 但对于隐藏窗口，只需确保 MessageLoop 能收到消息即可。
                    // 简单处理：
                    SetForegroundWindow(Handle); // 下面会补充这个 API 定义

                    // ★关键步骤B：在当前鼠标光标位置强制弹出
                    // 这样就完全绕过了 WinForms 对多屏 DPI 的错误计算
                    ContextMenuStrip?.Show(Cursor.Position);
                }
            };

            // 托盘图标双击 → 显示主窗口
            _tray.MouseDoubleClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    ShowMainWindow();
                }
            };



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
                    ClampToScreen();      // ★ 新增：松开鼠标后校正位置
                    SavePos();
                }
            };
            // === 双击事件重构 ===
            this.DoubleClick += (_, __) =>
            {
                switch (_cfg.MainFormDoubleClickAction)
                {
                    case 1: // 任务管理器
                        OpenTaskManager();
                        break;
                    case 2: // 设置
                        OpenSettings();
                        break;
                    case 3: // 历史流量
                        OpenTrafficHistory();
                        break;
                    case 0: // 默认：切换横竖屏
                    default:
                        ToggleLayoutMode();
                        break;
                }
            };


            // === 渐入透明度 (标准 WinForms 方式) ===
            this.Opacity = 0;
            double targetOpacity = Math.Clamp(_cfg.Opacity, 0.1, 1.0);

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    double current = 0;
                    while (current < targetOpacity)
                    {
                        await System.Threading.Tasks.Task.Delay(16).ConfigureAwait(false);
                        // 在 UI 线程更新
                        BeginInvoke(new Action(() => 
                        {
                            current += 0.05;
                            if (current > targetOpacity) current = targetOpacity;
                            this.Opacity = current;
                        }));
                        if (current >= targetOpacity) break;
                    }
                }
                catch { }
            });

            // 初始化圆角
            ApplyRoundedCorners();
            this.Resize += (_, __) => ApplyRoundedCorners();

            // === 状态恢复 ===
            if (_cfg.ClickThrough) SetClickThrough(true);
            if (_cfg.AutoHide) InitAutoHideTimer();
        }

        // =================================================================
        // ★★★ 核心修改：智能圆角方案 (Hybrid) ★★★
        // Win11+: 使用 DWM 原生无锯齿圆角 + 去除边框
        // Win10-: 回退使用 Region 切割 (保留原有逻辑)
        // =================================================================
        private void ApplyRoundedCorners()
        {
            try
            {
                // 检测是否为 Windows 11 (Build >= 22000)
                bool isWin11 = Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000;

                if (isWin11)
                {
                    // 【Win11 模式】
                    // 必须清空 Region，否则 DWM 圆角会被覆盖
                    this.Region = null;

                    // 1. 开启系统圆角
                    int preference = DWMWCP_ROUND;
                    DwmSetWindowAttribute(this.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));

                    // 2. 去除系统默认的灰色边框 (Color = None)
                    int borderColor = DWMWA_COLOR_NONE;
                    DwmSetWindowAttribute(this.Handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
                }
                else
                {
                    // 【Win10 / 旧系统模式】
                    // 保持原有的 Region 切割逻辑，虽然有锯齿但兼容性好
                    var t = ThemeManager.Current;
                    int r = Math.Max(0, t.Layout.CornerRadius);
                    
                    // 若无圆角需求，清除 Region
                    if (r == 0)
                    {
                        this.Region = null;
                        return;
                    }

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
            }
            catch { }
        }


        // ★★★ 新增：通用动作方法 (供 TaskbarForm 和 本地调用) ★★★
        public void OpenTaskManager()
        {
            try
            {
                Process.Start(new ProcessStartInfo("taskmgr") { UseShellExecute = true });
            }
            catch { }
        }

        public void OpenSettings()
        {
            // 防止重复打开
            foreach (Form f in Application.OpenForms)
            {
                if (f is SettingsForm) { f.Activate(); return; }
            }
            new SettingsForm(_cfg, _ui, this).Show();
        }

        public void OpenTrafficHistory()
        {
            foreach (Form f in Application.OpenForms)
            {
                if (f is TrafficHistoryForm) { f.Activate(); return; }
            }
            new TrafficHistoryForm(_cfg).Show();
        }

        private void ToggleLayoutMode()
        {
            // 1. 记录切换前的窗口中心点位置
            Point oldCenter = new Point(Left + Width / 2, Top + Height / 2);
            
            // 2. 切换横竖屏模式
            _cfg.HorizontalMode = !_cfg.HorizontalMode;
            _cfg.Save();
            
            // 3. 应用新主题和布局
            _ui.ApplyTheme(_cfg.Skin);
            RebuildMenus();
            
            // 4. 强制触发一次重绘和圆角计算
            this.Invalidate();
            ApplyRoundedCorners();
            
            // 5. 强制更新窗口尺寸
            this.Update();
            
            // 6. 计算新的窗口位置，保持中心点不变
            int newLeft = oldCenter.X - Width / 2;
            int newTop = oldCenter.Y - Height / 2;
            
            // 7. 确保新位置在当前屏幕的可视区域内
            var screen = Screen.FromPoint(oldCenter);
            var workingArea = screen.WorkingArea;
            
            // 调整水平位置，确保不超出屏幕边界
            if (newLeft < workingArea.Left)
                newLeft = workingArea.Left;
            if (newLeft + Width > workingArea.Right)
                newLeft = workingArea.Right - Width;
            
            // 调整垂直位置，确保不超出屏幕边界
            if (newTop < workingArea.Top)
                newTop = workingArea.Top;
            if (newTop + Height > workingArea.Bottom)
                newTop = workingArea.Bottom - Height;
            
            // 8. 防止切换时触发自动隐藏
            // 直接使用类级别的配置，保持统一
            int safeMargin = _hideThreshold + 1; // 阈值 + 1像素安全区

            // 确保窗口不靠近左边缘
            if (newLeft <= workingArea.Left + _hideThreshold)
                newLeft = workingArea.Left + safeMargin;

            // 确保窗口不靠近右边缘
            if (workingArea.Right - (newLeft + Width) <= _hideThreshold)
                newLeft = workingArea.Right - Width - safeMargin;

            // 确保窗口不靠近上边缘
            if (newTop <= workingArea.Top + _hideThreshold)
                newTop = workingArea.Top + safeMargin;
            
            // 9. 设置新位置
            Location = new Point(newLeft, newTop);
            
            // 10. 保存新的位置信息
            SavePos();
        }

        public void ShowMainWindow()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
            _cfg.HideMainForm = false;
            _cfg.Save();

            // ★★★ 优化：双击托盘时，如果处于自动隐藏状态，强制拉出来 ★★★
            if (_isHidden)
            {
                _isHidden = false; // 重置隐藏标志
                _dock = DockEdge.None; // 重置停靠状态
                
                // 强制把窗口拉回屏幕可视区域 (利用现有的 ClampToScreen 逻辑，或者手动计算)
                // 注意：ClampToScreen 依赖配置，如果用户关了 ClampToScreen 可能会失效，建议手动强制拉回
                var area = Screen.FromControl(this).WorkingArea;
                if (Left > area.Right - Width) Left = area.Right - Width;
                if (Left < area.Left) Left = area.Left;
                if (Top < area.Top) Top = area.Top;
            }

            // ★★★ 优化：唤醒后给予 3 秒保护期，让用户有时间操作 ★★★
            _keepVisibleUntil = DateTime.Now.AddSeconds(3.0);

            // 关键补充：每次显示主窗口时同步刷新菜单状态
            RebuildMenus();
        }

        public void HideMainWindow()
        {
            // 只隐藏窗口，不退出程序，不动任务栏
            this.Hide();
            _cfg.HideMainForm = true;
            _cfg.Save();
            // 关键补充：每次显示主窗口时同步刷新菜单状态
            RebuildMenus();
        }

        // ========== 隐藏托盘图标 ==========
        public void HideTrayIcon()
        {
            _tray.Visible = false;
        }

        // ========== 显示托盘图标 ==========
        public void ShowTrayIcon()
        {
            _tray.Visible = true;
        }



        // ========== 菜单选项更改后重建菜单 ==========
        public void RebuildMenus()
        {
            // ★★★ 修复方案：先销毁旧菜单，释放 GDI 句柄和内存 ★★★
            if (ContextMenuStrip != null)
            {
                ContextMenuStrip.Dispose();
                ContextMenuStrip = null;
            }

            var menu = MenuManager.Build(this, _cfg, _ui);
            //_tray.ContextMenuStrip = menu;
            ContextMenuStrip = menu;
            UIUtils.ClearBrushCache(); // 确保你有这个静态方法清空字典
        }

        // ========== 限制窗口不能拖出屏幕边界 ==========
        private void ClampToScreen()
        {

            if (!_cfg.ClampToScreen) return; // 未开启→不处理

            var area = Screen.FromControl(this).WorkingArea;

            int newX = Left;
            int newY = Top;

            // 限制 X
            if (newX < area.Left)
                newX = area.Left;
            if (newX + Width > area.Right)
                newX = area.Right - Width;

            // 限制 Y
            if (newY < area.Top)
                newY = area.Top;
            if (newY + Height > area.Bottom)
                newY = area.Bottom - Height;

            Left = newX;
            Top = newY;
        }


        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            _ui?.Render(e.Graphics);
        }
        // 覆盖此方法并留空，是为了防止 WinForms 在重绘时用 BackColor 清除屏幕，
        // 配合 UIController 的顺序调整，能彻底解决“闪烁”问题。
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 什么都不做，禁止默认的擦除行为
        }

        /// <summary>
        /// DPI变化时重新计算布局
        /// </summary>
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            // DPI变化时重新应用主题以适配新DPI
            _ui?.ApplyTheme(_cfg.Skin);
            
            // 重新计算圆角（Region模式下需要）
            ApplyRoundedCorners();
            this.Invalidate();
        }

        private void SavePos()
        {
            ClampToScreen();

            // ★★★ 优化：使用中心点判断屏幕，比 FromControl 更靠谱 (防止跨屏边缘识别错误) ★★★
            var center = new Point(Left + Width / 2, Top + Height / 2);
            var scr = Screen.FromPoint(center);

            _cfg.ScreenDevice = scr.DeviceName;
            _cfg.Position = new Point(Left, Top);
            _cfg.Save();
        }


        // ========== 初始化位置 ==========
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // ★★★ [新增] 检查更新成功标志 ★★★
            CheckUpdateSuccess();

            // === 是否隐藏主窗口 ===
            if (_cfg.HideMainForm)
            {
                this.Hide();
            }

            // 确保窗体尺寸已初始化
            this.Update();

            // ============================
            // ① 多显示器：查找保存的屏幕
            // ============================
            Screen? savedScreen = null;
            if (!string.IsNullOrEmpty(_cfg.ScreenDevice))
            {
                savedScreen = Screen.AllScreens
                    .FirstOrDefault(s => s.DeviceName == _cfg.ScreenDevice);
            }

            // ============================
            // ② 恢复位置：若找到原屏幕 → 精准还原
            // ============================
            if (savedScreen != null)
            {
                var area = savedScreen.WorkingArea;

                int x = _cfg.Position.X;
                int y = _cfg.Position.Y;

                // 防止窗口越界（例如 DPI 或屏幕位置改变）
                if (x < area.Left) x = area.Left;
                if (y < area.Top) y = area.Top;
                if (x + Width > area.Right) x = area.Right - Width;
                if (y + Height > area.Bottom) y = area.Bottom - Height;

                Location = new Point(x, y);
            }
            else
            {
                // ============================
                // ③ 回落到你原有逻辑
                // ============================
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
            }

            // ========================================================
            // ★★ 若是横屏：必须强制先渲染一次确保 Height 正确
            // ========================================================
            if (_cfg.HorizontalMode && _ui != null)
            {
                // 这里可能需要调整尺寸
                this.Size = new Size(this.Width, this.Height);
            }

            // 确保圆角生效
            ApplyRoundedCorners();

            // ★★★ 优化：启动时强制显示 2 秒，防止瞬间自动隐藏 ★★★
            _keepVisibleUntil = DateTime.Now.AddSeconds(3.0);

            // === 根据配置启动任务栏模式 ===
            if (_cfg.ShowTaskbar)
            {
                ToggleTaskbar(true);
            }

            // === 静默更新 (串行检查：先软件后驱动) ===
            _ = CheckUpdatesAndDriversAsync();

            if (_cfg.WebServerEnabled)
            {
                // 确保你已经创建了 src/WebServer/LiteWebServer.cs
                src.WebServer.LiteWebServer.Instance?.Start();
            }
        }

        // [新增] 串行检查更新和驱动
        private async Task CheckUpdatesAndDriversAsync()
        {
            try
            {
                // 1. 先检查驱动缺失 (优先保证功能可用)
                if (src.SystemServices.HardwareMonitor.Instance != null)
                {
                    await src.SystemServices.HardwareMonitor.Instance.SmartCheckDriver();
                }

                // 2. 再检查软件更新 (非阻塞，作为最后一步)
                await UpdateChecker.CheckAsync();
            }
            catch { }
        }

        // [新增] 检查并提示更新成功
        private void CheckUpdateSuccess()
        {
            string tokenPath = Path.Combine(AppContext.BaseDirectory, "update_success");

            if (File.Exists(tokenPath))
            {
                // 1. 尝试删除标志文件（防止下次启动重复提示）
                try { File.Delete(tokenPath); } catch { }

                // 2. 方式 A：弹出气泡提示（推荐，不打扰）
                string title = "⚡️LiteMonitor_v" + UpdateChecker.GetCurrentVersion();
                string content = _cfg.Language == "zh" ? "🎉 软件已成功更新到最新版本！" : "🎉 Software updated to latest version!";
                ShowNotification(title, content, ToolTipIcon.Info);

            }
        }

        // 显示右小角通知气泡
        public void ShowNotification(string title, string text, ToolTipIcon icon)
        {
            // 必须判断 Visible：如果用户隐藏了托盘图标，就不要（也无法）弹窗打扰他了
            if (_tray != null && _tray.Visible)
            {
                _tray.ShowBalloonTip(5000, title, text, icon);
            }
        }

        /// <summary>
        /// 窗体关闭时清理资源：释放 UIController 并隐藏托盘图标
        /// </summary>
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 退出时必须强制存一次最新的配置
            _cfg.Save(); // 保存配置
            TrafficLogger.Save(); // 退出时强制保存一次流量数据
            src.WebServer.LiteWebServer.Instance?.Stop();
            base.OnFormClosed(e); // 调用基类方法确保正常关闭
            _ui?.Dispose();      // 释放 UI 资源
            _tray.Visible = false; // 隐藏托盘图标
        }

    }
}