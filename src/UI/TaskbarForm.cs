using Microsoft.Win32;
using System.Runtime.InteropServices;
using LiteMonitor.src.Core;

namespace LiteMonitor
{
    public class TaskbarForm : Form
    {
        private Dictionary<uint, ToolStripItem> _commandMap = new Dictionary<uint, ToolStripItem>();
        private readonly Settings _cfg;
        private readonly UIController _ui;
        private readonly System.Windows.Forms.Timer _timer = new();

        // 1. 去掉 readonly，允许 ReloadLayout 重新赋值 (保留你的修改)
        private HorizontalLayout _layout;

        private IntPtr _hTaskbar = IntPtr.Zero;
        private IntPtr _hTray = IntPtr.Zero;

        private Rectangle _taskbarRect = Rectangle.Empty;
        private int _taskbarHeight = 32;
        private bool _isWin11;

        // ⭐ 动态透明色键：不再是 readonly，因为要随主题变
        private Color _transparentKey = Color.Black;
        // 记录当前是否是浅色模式，用于检测变化
        private bool _lastIsLightTheme = false;

        private System.Collections.Generic.List<Column>? _cols;
        // 1. 添加字段
        private readonly MainForm _mainForm;

        // ★★★ 新增：WndProc 消息常量 ★★★
        private const int WM_RBUTTONUP = 0x0205;

        // 公开方法：重新加载布局 (保留你的修改)
        public void ReloadLayout()
        {
           // 1. 重新构建布局 (自动读取最新的 Settings.cs)
            _layout = new HorizontalLayout(ThemeManager.Current, 300, LayoutMode.Taskbar, _cfg);
            
            // 2. ★ 核心：应用穿透和颜色 ★
            // 无论谁调用 ReloadLayout，都确保最新的穿透设置生效
            SetClickThrough(_cfg.TaskbarClickThrough);

            // 强制刷新主题检查，确保自定义背景色 (TaskbarColorBg) 被应用
            CheckTheme(true);

            // 3. 触发重绘
            if (_cols != null && _cols.Count > 0)
            {
                _layout.Build(_cols, _taskbarHeight);
                Width = _layout.PanelWidth;
                UpdatePlacement(Width);
            }
            Invalidate();
        }

        public TaskbarForm(Settings cfg, UIController ui, MainForm mainForm)
        {
            _cfg = cfg;
            _ui = ui;
            // 2. 初始化 MainForm 引用
            _mainForm = mainForm;
            
            // 初始化：LayoutMode.Taskbar
            ReloadLayout(); // 初始化布局器，读取最新的 Settings 文件

            _isWin11 = Environment.OSVersion.Version >= new Version(10, 0, 22000);

            // == 窗体基础设置 ==
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ControlBox = false;
            TopMost = false;
            DoubleBuffered = true;

            // 初始化主题颜色
            CheckTheme(true);

            // 查找任务栏和托盘区
            FindHandles();

            // Windows Forms会在需要时自动创建句柄，无需手动调用
            // CreateHandle();

            // 挂载到任务栏
            AttachToTaskbar();

            // 定时刷新
            _timer.Interval = Math.Max(_cfg.RefreshMs, 60);
            _timer.Tick += (_, __) => Tick();
            _timer.Start();

            Tick();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();
            }
            base.Dispose(disposing);
        }

        // ====================================================================================
        // ★★★ 核心修复：WndProc 仅针对 Win10 拦截 ★★★
        // ====================================================================================
        protected override void WndProc(ref Message m)
        {
            // 仅在 Win10 下拦截右键抬起 (解决 Win10 任务栏卡死问题)
            if (!_isWin11 && m.Msg == WM_RBUTTONUP)
            {
                // Win10 必须使用异步 (BeginInvoke) 来防止死锁
                this.BeginInvoke(new Action(ShowContextMenu));

                // ★ 关键：拦截消息，不调用 base.WndProc
                // 这样 Win10 系统任务栏就收不到这个右键，不会弹出系统菜单
                return; 
            }

            // Win11 或其他消息：不做任何拦截，走原生流程
            // 这样会正常触发后面的 OnMouseUp，保证 Win11 行为与你原始代码完全一致
            base.WndProc(ref m);
        }

        // 提取出来的显示菜单逻辑 (供 Win10 异步调用 和 Win11 OnMouseUp 调用)
        private void ShowContextMenu()
        {
            // 1. 构建菜单 (复用 MenuManager)
            var menu = MenuManager.Build(_mainForm, _cfg, _ui);

            // 2. ★★★ 核心修复：强制让 TaskbarForm 获取前台焦点 ★★★
            // 严格保留你原始代码的写法：使用 this.Handle
            // 确保菜单弹出后立即激活，不需要点第二次
            SetForegroundWindow(this.Handle);

            // 3. 显示菜单
            menu.Show(Cursor.Position);
        }

        // ====================================================================================
        // 原生事件处理 (Win11 走这里，保持完美体验)
        // ====================================================================================
        
        // 添加鼠标右键点击事件
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (e.Button == MouseButtons.Right)
            {
                // Win10 的右键已经被 WndProc 拦截了，不会进到这里
                // 所以这里只有 Win11 会执行，完全复刻你的原始逻辑
                ShowContextMenu();
            }
        }

        // 添加鼠标左键双击事件 (保持不变)
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);

            // 只响应左键双击
            if (e.Button == MouseButtons.Left)
            {
                // ★ 改为：显示 / 隐藏 主窗口自动切换
                if (_mainForm.Visible)
                    _mainForm.HideMainWindow();
                else
                    _mainForm.ShowMainWindow();
            }
        }

        // -------------------------------------------------------------
        // Win32 API
        // -------------------------------------------------------------
        [DllImport("user32.dll")] private static extern IntPtr FindWindow(string cls, string? name);
        [DllImport("user32.dll")] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int idx);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int idx, int value);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);
        [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
         // 在 TaskbarForm 类中添加
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const uint LWA_COLORKEY = 0x00000001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public int lParam;
        }
        [DllImport("shell32.dll")] private static extern uint SHAppBarMessage(uint msg, ref APPBARDATA pData);
        private const uint ABM_GETTASKBARPOS = 5;

        // -------------------------------------------------------------
        // 主题检测与颜色设置
        // -------------------------------------------------------------
        private bool IsSystemLightTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                object? val = key.GetValue("SystemUsesLightTheme");
                if (val is int i) return i == 1;
            }
            }
            catch { }
            return false;
        }

       private void CheckTheme(bool force = false)
        {
            bool isLight = IsSystemLightTheme();

            // 如果主题没变且不是强制刷新，则忽略
            if (!force && isLight == _lastIsLightTheme) return;

            _lastIsLightTheme = isLight;

            // ★★★ [修改] 背景色逻辑 ★★★
            if (_cfg.TaskbarCustomStyle)
            {
                // 自定义模式：使用用户指定的背景色作为透明键
                try {
                    _transparentKey = ColorTranslator.FromHtml(_cfg.TaskbarColorBg);
                } catch {
                    _transparentKey = Color.Black; 
                }
            }
            else
            {
                // 原有模式
                if (isLight) _transparentKey = Color.FromArgb(210, 210, 210);
                else _transparentKey = Color.Black;
            }

            // 更新 WinForms 属性
            BackColor = _transparentKey;

            // 更新 API 属性 (如果句柄已创建)
            if (IsHandleCreated)
            {
                ApplyLayeredAttribute();
            }
        }

        // 2. 添加鼠标穿透控制方法
        public void SetClickThrough(bool enable)
        {
            int exStyle = GetWindowLong(Handle, GWL_EXSTYLE);
            if (enable)
                exStyle |= WS_EX_TRANSPARENT; // 添加穿透
            else
                exStyle &= ~WS_EX_TRANSPARENT; // 移除穿透
            
            SetWindowLong(Handle, GWL_EXSTYLE, exStyle);
        }

        private void ApplyLayeredAttribute()
        {
            uint colorKey = (uint)(_transparentKey.R | (_transparentKey.G << 8) | (_transparentKey.B << 16));
            SetLayeredWindowAttributes(Handle, colorKey, 0, LWA_COLORKEY);
        }

        // -------------------------------------------------------------
        // 核心逻辑
        // -------------------------------------------------------------
        private void FindHandles()
        {
            _hTaskbar = FindWindow("Shell_TrayWnd", null);
            _hTray = FindWindowEx(_hTaskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        }

        private void AttachToTaskbar()
        {
            if (_hTaskbar == IntPtr.Zero) FindHandles();

            SetParent(Handle, _hTaskbar);

            int style = GetWindowLong(Handle, GWL_STYLE);
            style &= (int)~0x80000000;
            style |= WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS;
            SetWindowLong(Handle, GWL_STYLE, style);

            // int exStyle = GetWindowLong(Handle, GWL_EXSTYLE);
            // exStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
            // SetWindowLong(Handle, GWL_EXSTYLE, exStyle);

            ApplyLayeredAttribute();
        }

        private void Tick()
        {
            // 每5秒检查一次主题变化（比每秒检查效率高很多）
            if (Environment.TickCount % 5000 < _cfg.RefreshMs)
            {
                CheckTheme();
            }

            _cols = _ui.GetTaskbarColumns();
            if (_cols == null || _cols.Count == 0) return;
            UpdateTaskbarRect(); 
                
            // 重新构建布局 (Build 内部也有 MeasureText，能省则省)
            _layout.Build(_cols, _taskbarHeight);
            Width = _layout.PanelWidth;
            Height = _taskbarHeight;
            
            UpdatePlacement(Width);

            // 最终渲染
            Invalidate();
        }



        // -------------------------------------------------------------
        // 定位与辅助
        // -------------------------------------------------------------
        private void UpdateTaskbarRect()
        {
            APPBARDATA abd = new APPBARDATA();
            abd.cbSize = Marshal.SizeOf(abd);
            uint res = SHAppBarMessage(ABM_GETTASKBARPOS, ref abd);
            if (res != 0)
                _taskbarRect = Rectangle.FromLTRB(abd.rc.left, abd.rc.top, abd.rc.right, abd.rc.bottom);
            else
            {
                var s = Screen.PrimaryScreen;
                if (s != null)
                {
                    _taskbarRect = new Rectangle(s.WorkingArea.Left, s.WorkingArea.Bottom, s.WorkingArea.Width, s.Bounds.Bottom - s.WorkingArea.Bottom);
                }
            }
            _taskbarHeight = Math.Max(24, _taskbarRect.Height);
        }

       // 1. 检测 Win11 是否居中 (现有的，供菜单使用)
        public static bool IsCenterAligned()
        {
            if (Environment.OSVersion.Version.Major < 10 || Environment.OSVersion.Version.Build < 22000) 
                return false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                return ((int)(key?.GetValue("TaskbarAl", 1) ?? 1)) == 1;
            }
            catch { return false; }
        }


        // ======================
        //  获取任务栏 DPI（最准确）
        // ======================
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        public static int GetTaskbarDpi()
        {
            // 使用任务栏句柄（静态也能获取）
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero)
            {
                try
                {
                    return (int)GetDpiForWindow(taskbar);
                }
                catch { }
            }

            return 96; // fallback
        }



         // ==========================
            //  Windows 11 Widgets 检测
            // ==========================
        public static int GetWidgetsWidth()
        {
            int dpi = TaskbarForm.GetTaskbarDpi();

            // ==========================
            //  Windows 11 Widgets 检测
            // ==========================
            if (Environment.OSVersion.Version >= new Version(10, 0, 22000))
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string pkg = Path.Combine(local, "Packages");

                // 无 WebExperience 包 = Win11 无 Widgets
                bool hasWidgetPkg = Directory.GetDirectories(pkg, "MicrosoftWindows.Client.WebExperience*").Any();
                if (!hasWidgetPkg) return 0;

                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                if (key == null) return 0;

                object? val = key.GetValue("TaskbarDa");
                if (val is int i)
                {
                    if (i != 0)
                        return 150 * dpi / 96;   // Win11 Widgets 宽度
                    else
                        return 0;
                }

                return 0;
            }

            return 0;
        }


        // 2. 更新位置 (调用 GetWidgetsWidth)
        private void UpdatePlacement(int panelWidth)
        {
            if (_hTaskbar == IntPtr.Zero) return;

            var scr = Screen.PrimaryScreen;
            if (scr == null) return;
            
            bool bottom = _taskbarRect.Bottom >= scr.Bounds.Bottom - 2;
            GetWindowRect(_hTray, out RECT tray);

            // 1. 获取当前系统状态
            bool sysCentered = IsCenterAligned(); // 系统是否居中
            bool alignLeft = _cfg.TaskbarAlignLeft && sysCentered; // 用户是否要在居中模式下强行居左

            // 2. 智能计算右侧的“小组件避让宽度”
            // 逻辑：如果系统是居中的，小组件在最左边，右边不需要避让，offset = 0
            //       如果系统是居左的，小组件可能在托盘左边，右边需要避让，offset = GetWidgetsWidth()
            int rightSideWidgetOffset = sysCentered ? 0 : GetWidgetsWidth();

            int leftScreen, topScreen;

            // Y轴计算 (保持不变)
            if (bottom) topScreen = _taskbarRect.Bottom - _taskbarHeight;
            else topScreen = _taskbarRect.Top;

            // X轴计算
            if (alignLeft)
            {
                // === 用户强制居左 (仅 Win11 居中时有效) ===
                // 基准：任务栏左边缘 + 6px
                // 避让：如果开启了小组件，这里需要向右推 160px (因为居中模式下小组件在最左边)
                int startX = _taskbarRect.Left + 6;
                // 检测 Win11 小组件是否存在 (TaskbarDa 注册表检测逻辑，或者复用 GetWidgetsWidth > 0)
                if (GetWidgetsWidth() > 0) 
                {
                    startX += GetWidgetsWidth(); // 或者是固定值 160
                }
                leftScreen = startX;
            }
            else
            {
                // === 居右模式 (默认) ===
                // 基准：托盘左边缘 - 插件宽 - 6px
                // 避让：应用上面计算的 rightSideWidgetOffset
                leftScreen = tray.left - panelWidth - 6 - rightSideWidgetOffset;
            }

            POINT pt = new POINT { X = leftScreen, Y = topScreen };
            ScreenToClient(_hTaskbar, ref pt);
            SetWindowPos(Handle, IntPtr.Zero, pt.X, pt.Y, panelWidth, _taskbarHeight, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        // -------------------------------------------------------------
        // 绘制
        // -------------------------------------------------------------
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 用当前的透明键颜色填充
            e.Graphics.Clear(_transparentKey);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_cols == null) return;
            var g = e.Graphics;

            // 可以尝试调整这里，ClearType 在 ColorKey 模式下可能不如 AntiAlias 干净
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

            // ⭐ 建议：如果上面的修改还是有轻微杂边，可以将下面这一行改为 AntiAlias
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            TaskbarRenderer.Render(g, _cols, _lastIsLightTheme);

        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // 保留 Layered 和 ToolWindow，但在任务栏模式下，必须允许鼠标交互
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW; 
                
                // ★★★ 删掉或注释掉下面这行鼠标穿透 WS_EX_TRANSPARENT ★★★
                // cp.ExStyle |= WS_EX_TRANSPARENT; 
                
                return cp;
            }
        }

    }


}