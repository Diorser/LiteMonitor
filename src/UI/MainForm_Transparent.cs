using LiteMonitor.src.Core;
using LiteMonitor.src.System;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

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

        public void SetClickThrough(bool enable)
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
        private enum DockEdge { None, Left, Right, Top, Bottom }
        private DockEdge _dock = DockEdge.None;
        private bool _uiDragging = false;

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

            // ==== 关键修改：基于“当前窗体所在屏幕”计算区域 ====
            var center = new Point(Left + Width / 2, Top + Height / 2);
            var screen = Screen.FromPoint(center);
            var area = screen.WorkingArea;

            var cursor = Cursor.Position;

            // ===== 模式判断 =====
            bool isHorizontal = _cfg.HorizontalMode;

            // ===== 竖屏：左右贴边隐藏 =====
            bool nearLeft = false, nearRight = false;

            // ===== 横屏：上下贴边隐藏 =====
            bool nearTop = false, nearBottom = false;

            if (!isHorizontal)
            {
                // 竖屏 → 左右隐藏
                nearLeft = Left <= area.Left + _hideThreshold;
                nearRight = area.Right - Right <= _hideThreshold;
            }
            else
            {
                // 横屏 → 上下隐藏
                nearTop = Top <= area.Top + _hideThreshold;
                nearBottom = area.Bottom - Bottom <= _hideThreshold;
            }

            // ===== 是否应该隐藏 =====
            bool shouldHide =
                (!isHorizontal && (nearLeft || nearRight)) ||
                (isHorizontal && (nearTop || nearBottom));

            // ===== 靠边 → 自动隐藏 =====
            if (!_isHidden && shouldHide && !Bounds.Contains(cursor))
            {
                if (!isHorizontal)
                {
                    // ========= 竖屏：左右隐藏 =========
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
                }
                else
                {
                    // ========= 横屏：上下隐藏 =========
                    if (nearBottom)
                    {
                        Top = area.Bottom - _hideWidth;
                        _dock = DockEdge.Bottom;
                    }
                    else
                    {
                        Top = area.Top - (Height - _hideWidth);
                        _dock = DockEdge.Top;
                    }
                }

                _isHidden = true;
                return;
            }

            // ===== 已隐藏 → 鼠标靠边 → 弹出 =====
            if (_isHidden)
            {
                const int hoverBand = 30;

                if (!isHorizontal)
                {
                    // ======== 竖屏：左右弹出 ========
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
                else
                {
                    // ======== 横屏：上下弹出 ========
                    if (_dock == DockEdge.Bottom && cursor.Y >= area.Bottom - hoverBand)
                    {
                        Top = area.Bottom - Height;
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
                if (_taskbar == null || _taskbar.IsDisposed)
                {
                    _taskbar = new TaskbarForm(_cfg, _ui!);
                    _taskbar.Show();                  // ★ 必须真正 Show 出来
                }
                else
                {
                    if (!_taskbar.Visible)
                        _taskbar.Show();
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
            // === 自动检测系统语言 ===
            string sysLang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLower();
            string langPath = Path.Combine(AppContext.BaseDirectory, "resources/lang", $"{sysLang}.json");
            _cfg.Language = File.Exists(langPath) ? sysLang : "en";

            // 语言与主题的加载交给 UIController.ApplyTheme 统一处理

            // 宽度只认 Settings；真正的主题宽度覆盖在 UIController.ApplyTheme 内执行
            //Width = _cfg.PanelWidth > 100 ? _cfg.PanelWidth : Width;


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

            _tray.ContextMenuStrip = MenuManager.Build(this, _cfg, _ui);
            ContextMenuStrip = _tray.ContextMenuStrip;
            
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
        public void ShowMainWindow()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
            _cfg.HideMainForm = false;
            _cfg.Save();
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



        // ========== 菜单选项更改后重建菜单 ==========
        public void RebuildMenus()
        {
            var menu = MenuManager.Build(this, _cfg, _ui);
            _tray.ContextMenuStrip = menu;
            ContextMenuStrip = menu;
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



        protected override void OnPaint(PaintEventArgs e) => _ui?.Render(e.Graphics);

        private void SavePos()
        {
            ClampToScreen(); // ★ 新增：确保保存前被校正
            _cfg.Position = new Point(Left, Top);
            _cfg.Save();
        }


        // ========== 初始化位置 ==========
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

             //是否隐藏主窗口
            if (_cfg.HideMainForm)
            {
                this.Hide();
            }

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
            // ★★ 新增：如果是横屏，先强制主窗体完成第一次布局（避免高度跳变）
            if (_cfg.HorizontalMode)
            {
                _ui.Render(CreateGraphics());   // 强制执行一次横屏布局 & _form.Height 正确化
                this.Update();                     // 刷新位置
            }
            // === 根据配置启动任务栏模式 ===
            if (_cfg.ShowTaskbar)
            {
                ToggleTaskbar(true);
            }
                
            // ✅ 启动时静默检查更新（不打扰用户）
            _ = UpdateChecker.CheckAsync();

        }

        
        /// <summary>
        /// 窗体关闭时清理资源：释放 UIController 并隐藏托盘图标
        /// </summary>
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            _ui?.Dispose();      // 释放 UI 资源
            _tray.Visible = false; // 隐藏托盘图标
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
