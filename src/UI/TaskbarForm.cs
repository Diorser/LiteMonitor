using Microsoft.Win32;
using System.Runtime.InteropServices;
using LiteMonitor.src.Core;
using System.Linq; // ★

namespace LiteMonitor
{
    public class TaskbarForm : Form
    {
        private Dictionary<uint, ToolStripItem> _commandMap = new Dictionary<uint, ToolStripItem>();
        private readonly Settings _cfg;
        private readonly UIController _ui;
        private readonly System.Windows.Forms.Timer _timer = new();

        private HorizontalLayout _layout;

        private IntPtr _hTaskbar = IntPtr.Zero;
        private IntPtr _hTray = IntPtr.Zero;
        private IntPtr _hRebar = IntPtr.Zero;
        private IntPtr _hTaskSw = IntPtr.Zero;
        private int lastbarWidth = 0;

        private Rectangle _taskbarRect = Rectangle.Empty;
        private int _taskbarHeight = 32;
        private bool _isWin11;

        private Color _transparentKey = Color.Black;
        private bool _lastIsLightTheme = false;
        // ★★★★★ 1. 必须在这里补充 TargetDevice 定义，解决 CS1061 报错 ★★★★★
        public string TargetDevice { get; private set; } = "";

        private System.Collections.Generic.List<Column>? _cols;
        private readonly MainForm _mainForm;
        private ContextMenuStrip? _currentMenu;

        private const int WM_RBUTTONUP = 0x0205;
        
        public void ReloadLayout()
        {
            _layout = new HorizontalLayout(ThemeManager.Current, 300, LayoutMode.Taskbar, _cfg);
            SetClickThrough(_cfg.TaskbarClickThrough);
            CheckTheme(true);

            // 初始化时也尝试构建一次，避免闪烁
            if (_cols != null && _cols.Count > 0)
            {
                // 这里的逻辑将在 Tick 中被动态覆盖，这里仅作初始化
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
            _mainForm = mainForm;

            // ★★★ 2. 在构造函数中记录目标屏幕 ★★★
            TargetDevice = _cfg.TaskbarMonitorDevice;
            
            ReloadLayout();

            _isWin11 = Environment.OSVersion.Version >= new Version(10, 0, 22000);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ControlBox = false;
            TopMost = false;
            DoubleBuffered = true;

            CheckTheme(true);
            FindHandles();
            
            AttachToTaskbar();
            // ★★★ 补充：挂载到任务栏后，再次强制刷新一下穿透状态 ★★★
            SetClickThrough(_cfg.TaskbarClickThrough);

            _timer.Interval = Math.Max(_cfg.RefreshMs, 60);
            _timer.Tick += (_, __) => Tick();
            _timer.Start();

            Tick();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                RestoreTaskSwitchWindow();
                _timer.Stop();
                _timer.Dispose();
                _currentMenu?.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void WndProc(ref Message m)
        {
            if (!_isWin11 && m.Msg == WM_RBUTTONUP)
            {
                this.BeginInvoke(new Action(ShowContextMenu));
                return; 
            }
            base.WndProc(ref m);
        }

        // 2. 修改 ShowContextMenu 方法：
        private void ShowContextMenu()
        {
            // ★★★ 修复方案：显示新菜单前，强制销毁上一次的菜单 ★★★
            if (_currentMenu != null)
            {
                _currentMenu.Dispose();
                _currentMenu = null;
            }

            _currentMenu = MenuManager.Build(_mainForm, _cfg, _ui);
            
            // 必须确保窗口激活，否则点击菜单外无法自动关闭
            SetForegroundWindow(this.Handle);
            
            // 监听菜单关闭事件，自动清理（可选，但推荐）
            _currentMenu.Closed += (s, e) => 
            {
                // 延迟清理或不做处理，等待下次 Show 时清理均可
            };

            _currentMenu.Show(Cursor.Position);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right)
            {
                ShowContextMenu();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left)
            {
                switch (_cfg.TaskbarDoubleClickAction)
                {
                    case 1: 
                        _mainForm.OpenTaskManager();
                        break;
                    case 2: 
                        _mainForm.OpenSettings();
                        break;
                    case 3: 
                        _mainForm.OpenTrafficHistory();
                        break;
                    case 0: 
                    default:
                        if (_mainForm.Visible)
                            _mainForm.HideMainWindow();
                        else
                            _mainForm.ShowMainWindow();
                        break;
                }
            }
        }

        // -------------------------------------------------------------
        // Win32 API
        // -------------------------------------------------------------
        [DllImport("user32.dll", SetLastError = true)] static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
        [DllImport("user32.dll")] private static extern IntPtr FindWindow(string cls, string? name);
        [DllImport("user32.dll")] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int idx);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int idx, int value);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);
        [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Auto)] private static extern IntPtr GetParent(IntPtr hWnd);

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
            if (!force && isLight == _lastIsLightTheme) return;
            _lastIsLightTheme = isLight;

            if (_cfg.TaskbarCustomStyle)
            {
                try 
                {
                    Color customColor = ColorTranslator.FromHtml(_cfg.TaskbarColorBg);
                    if (customColor.R == customColor.G && customColor.G == customColor.B)
                    {
                        int r = customColor.R;
                        int g = customColor.G;
                        int b = customColor.B;
                        if (b >= 255) b = 254; else b += 1;
                        _transparentKey = Color.FromArgb(r, g, b);
                    }
                    else
                    {
                        _transparentKey = customColor;
                    }
                } 
                catch { _transparentKey = Color.Black; }
            }
            else
            {
                if (isLight) _transparentKey = Color.FromArgb(210, 210, 211); 
                else _transparentKey = Color.FromArgb(40, 40, 41);       
            }

            BackColor = _transparentKey;
            if (IsHandleCreated) ApplyLayeredAttribute();
            Invalidate();
        }

        public void SetClickThrough(bool enable)
        {
            int exStyle = GetWindowLong(Handle, GWL_EXSTYLE);
            if (enable) exStyle |= WS_EX_TRANSPARENT; 
            else exStyle &= ~WS_EX_TRANSPARENT; 
            SetWindowLong(Handle, GWL_EXSTYLE, exStyle);
        }

        private void ApplyLayeredAttribute()
        {
            uint colorKey = (uint)(_transparentKey.R | (_transparentKey.G << 8) | (_transparentKey.B << 16));
            SetLayeredWindowAttributes(Handle, colorKey, 0, LWA_COLORKEY);
        }

        // -------------------------------------------------------------
        // ★★★ 核心逻辑：多屏支持 ★★★
        // -------------------------------------------------------------
        private void FindHandles()
        {
            // 1. 确定目标屏幕
            Screen target = Screen.PrimaryScreen;
            if (!string.IsNullOrEmpty(_cfg.TaskbarMonitorDevice))
            {
                target = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _cfg.TaskbarMonitorDevice) ?? Screen.PrimaryScreen;
            }

            // 2. 根据屏幕类型查找句柄
            if (target.Primary)
            {
                _hTaskbar = FindWindow("Shell_TrayWnd", null);
                _hTray = FindWindowEx(_hTaskbar, IntPtr.Zero, "TrayNotifyWnd", null);
                _hRebar = FindWindowEx(_hTaskbar, IntPtr.Zero, "ReBarWindow32", null);
                _hTaskSw = FindWindowEx(_hRebar, IntPtr.Zero, "MSTaskSwWClass", null);
            }
            else
            {
                // 副屏任务栏类名通常为 Shell_SecondaryTrayWnd
                _hTaskbar = FindSecondaryTaskbar(target);
                _hTray = IntPtr.Zero; // 副屏通常没有 TrayNotifyWnd
            }
        }

        private IntPtr FindSecondaryTaskbar(Screen screen)
        {
            IntPtr hWnd = IntPtr.Zero;
            while ((hWnd = FindWindowEx(IntPtr.Zero, hWnd, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            {
                GetWindowRect(hWnd, out RECT rect);
                Rectangle r = Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
                if (screen.Bounds.Contains(r.Location) || screen.Bounds.IntersectsWith(r))
                    return hWnd;
            }
            return FindWindow("Shell_TrayWnd", null);
        }

        private void AttachToTaskbar()
        {
            if (_hTaskbar == IntPtr.Zero) FindHandles();
            if (_hTaskbar == IntPtr.Zero) return;

            SetParent(Handle, _hTaskbar);

            int style = GetWindowLong(Handle, GWL_STYLE);
            style &= (int)~0x80000000; 
            style |= WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS;
            SetWindowLong(Handle, GWL_STYLE, style);

            ApplyLayeredAttribute();
        }

        // 判断任务栏是否是垂直放置 (左右侧)
        private bool IsVertical()
        {
            return _taskbarRect.Height > _taskbarRect.Width;
        }

        private void Tick()
        {
            if (Environment.TickCount % 5000 < _cfg.RefreshMs) CheckTheme();

            _cols = _ui.GetTaskbarColumns();
            if (_cols == null || _cols.Count == 0) return;
            
            UpdateTaskbarRect(); 
            
            // ★★★ 新增：垂直任务栏布局支持 ★★★
            if (IsVertical())
            {
                BuildVerticalLayout();
            }
            else
            {
                // 现有的水平布局逻辑
                _layout.Build(_cols, _taskbarHeight);
                Width = _layout.PanelWidth;
                Height = _taskbarHeight;
            }
            
            UpdatePlacement(Width);
            Invalidate();
        }

        // ★★★ 新增：构建垂直列表布局 ★★★
        private void BuildVerticalLayout()
        {
            int w = _taskbarRect.Width;
            // 确保宽度有效
            if (w < 20) w = 60; 

            // 根据字体计算行高，留一点边距
            int itemHeight = (int)(_cfg.TaskbarFontSize * 1.5f + 6);
            if (itemHeight < 20) itemHeight = 20;

            // ★★★ 新增：定义左右边距 ★★★
            int margin = 4; // 这里设置 4 像素边距，你可以根据喜好调整（如 2~6）
            int contentWidth = w - (margin * 2);

            int y = 0;
            foreach (var col in _cols)
            {
                // 垂直堆叠：先 Top 项，后 Bottom 项
                if (col.Top != null)
                {
                    col.BoundsTop = new Rectangle(margin, y, contentWidth, itemHeight);
                    y += itemHeight;
                }
                else col.BoundsTop = Rectangle.Empty;

                if (col.Bottom != null)
                {
                    col.BoundsBottom = new Rectangle(margin, y, contentWidth, itemHeight);
                    y += itemHeight;
                }
                else col.BoundsBottom = Rectangle.Empty;
                
                // 项之间微小间距
                // y += 2; 
            }

            this.Width = w;
            this.Height = y;
        }

        // -------------------------------------------------------------
        // 定位与辅助
        // -------------------------------------------------------------
        private void UpdateTaskbarRect()
        {
            bool isPrimary = (_hTaskbar == FindWindow("Shell_TrayWnd", null));

            if (isPrimary)
            {
                APPBARDATA abd = new APPBARDATA();
                abd.cbSize = Marshal.SizeOf(abd);
                uint res = SHAppBarMessage(ABM_GETTASKBARPOS, ref abd);
                if (res != 0)
                {
                    _taskbarRect = Rectangle.FromLTRB(abd.rc.left, abd.rc.top, abd.rc.right, abd.rc.bottom);
                }
                else
                {
                    var s = Screen.PrimaryScreen;
                    if (s != null)
                        _taskbarRect = new Rectangle(s.Bounds.Left, s.Bounds.Bottom - 40, s.Bounds.Width, 40);
                }
            }
            else
            {
                if (_hTaskbar != IntPtr.Zero && GetWindowRect(_hTaskbar, out RECT r))
                {
                    _taskbarRect = Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
                }
                else
                {
                    Screen target = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _cfg.TaskbarMonitorDevice) ?? Screen.PrimaryScreen;
                    _taskbarRect = new Rectangle(target.Bounds.Left, target.Bounds.Bottom - 40, target.Bounds.Width, 40);
                }
            }
            
            _taskbarHeight = Math.Max(24, _taskbarRect.Height);
        }

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

        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
        public static int GetTaskbarDpi()
        {
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero)
            {
                try { return (int)GetDpiForWindow(taskbar); } catch { }
            }
            return 96;
        }

        public static int GetWidgetsWidth()
        {
            int dpi = TaskbarForm.GetTaskbarDpi();
            if (Environment.OSVersion.Version >= new Version(10, 0, 22000))
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string pkg = Path.Combine(local, "Packages");
                bool hasWidgetPkg = false;
                try { hasWidgetPkg = Directory.GetDirectories(pkg, "MicrosoftWindows.Client.WebExperience*").Any(); } catch {}
                
                if (!hasWidgetPkg) return 0;

                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                if (key == null) return 0;

                object? val = key.GetValue("TaskbarDa");
                if (val is int i && i != 0) return 150 * dpi / 96;
            }
            return 0;
        }

        // ★★★ 3. UpdatePlacement 方法：增加垂直任务栏定位逻辑 ★★★
        private void UpdatePlacement(int panelWidth)
        {
            int leftScreen = _taskbarRect.Left;
            int topScreen;
            if (_hTaskbar == IntPtr.Zero) return;

            // ★★★ 新增：垂直任务栏定位分支 ★★★
            if (IsVertical())
            {
                
                
                // 尝试定位到托盘上方 (Vertical Taskbar通常Tray在底部)
                // 获取托盘位置（仅主屏有效，副屏直接置底）
                int bottomLimit = _taskbarRect.Bottom;
                
                if (_hTray != IntPtr.Zero && GetWindowRect(_hTray, out RECT trayRect))
                {
                    // 简单的有效性检查，防止托盘飞了
                    if (trayRect.top >= _taskbarRect.Top && trayRect.bottom <= _taskbarRect.Bottom)
                    {
                        bottomLimit = trayRect.top;
                    }
                }

                // 计算顶部位置：基准线 - 自身高度 - 手动偏移 - 间距
                topScreen = bottomLimit - this.Height - _cfg.TaskbarManualOffset - 6;

                // 防止超出顶部
                if (topScreen < _taskbarRect.Top) topScreen = _taskbarRect.Top;

                SetPosition(leftScreen, topScreen, _taskbarRect.Width, this.Height);
                return;
            }

            // --- 以下为原有的水平任务栏定位逻辑 ---

            Screen currentScreen = Screen.FromRectangle(_taskbarRect);
            if (currentScreen == null) currentScreen = Screen.PrimaryScreen;
            
            bool bottom = _taskbarRect.Top >= currentScreen.Bounds.Bottom - _taskbarHeight - 10;
            bool sysCentered = IsCenterAligned();
            bool isPrimary = currentScreen.Primary;
            
            int rawWidgetWidth = GetWidgetsWidth();      
            int manualOffset = _cfg.TaskbarManualOffset; 
            int leftModeTotalOffset = rawWidgetWidth + manualOffset;
            int sysRightAvoid = sysCentered ? 0 : rawWidgetWidth;
            int rightModeTotalOffset = sysRightAvoid + manualOffset;

            int timeWidth = _isWin11 ? 90 : 0; 
            bool alignLeft = _cfg.TaskbarAlignLeft && sysCentered; 

            if (bottom) topScreen = _taskbarRect.Top;
            else topScreen = _taskbarRect.Top;

            if (alignLeft)
            {
                int startX = _taskbarRect.Left + 6;
                if (leftModeTotalOffset > 0) startX += leftModeTotalOffset;
                leftScreen = startX;
            }
            else
            {
                if (isPrimary && _hTray != IntPtr.Zero && GetWindowRect(_hTray, out RECT tray))
                {
                    leftScreen = tray.left - panelWidth - 6;
                    leftScreen -= rightModeTotalOffset;
                }
                else
                {
                    leftScreen = _taskbarRect.Right - panelWidth - 10;
                    leftScreen -= rightModeTotalOffset;
                    leftScreen -= timeWidth;
                }
            }

            SetPosition(leftScreen, topScreen, panelWidth, _taskbarHeight);
            AdjustTaskSwitchWindow(panelWidth, _taskbarHeight);
        }
        //调整MSTaskSwWClass窗口位置和大小
        private void AdjustTaskSwitchWindow(int panelWidth, int panelHeight)
        {
            if (_hTaskSw != IntPtr.Zero && _hRebar != IntPtr.Zero)
            {
                RECT rcReBar, rcTaskSw;
                GetWindowRect(_hRebar, out rcReBar);
                //GetWindowRect(_hTaskSw, out rcTaskSw);
                int barWidth = rcReBar.right - rcReBar.left - panelWidth;
                if (barWidth != lastbarWidth)
                {
                    lastbarWidth = barWidth;
                    MoveWindow(_hTaskSw, 0, 0, barWidth, panelHeight, true);
                }
            }
        }
        //恢复MSTaskSwWClass窗口位置和大小
        public void RestoreTaskSwitchWindow()
        {
            if (_hTaskSw != IntPtr.Zero && _hRebar != IntPtr.Zero)
            {
                RECT rcReBar;
                GetWindowRect(_hRebar, out rcReBar);
                int barWidth = rcReBar.right - rcReBar.left;
                MoveWindow(_hTaskSw, 0, 0, barWidth, _taskbarHeight, true);
            }
        }
        // 提取出的通用设置位置方法
        private void SetPosition(int leftScreen, int topScreen, int w, int h)
        {
            IntPtr currentParent = GetParent(Handle);
            bool isAttached = (currentParent == _hTaskbar);

            if (!isAttached)
            {
                AttachToTaskbar();
                currentParent = GetParent(Handle);
                isAttached = currentParent == _hTaskbar;
            }

            int finalX = leftScreen;
            int finalY = topScreen;
            
            if (isAttached)
            {
                POINT pt = new POINT { X = leftScreen, Y = topScreen };
                ScreenToClient(_hTaskbar, ref pt);
                finalX = pt.X;
                finalY = pt.Y;
                SetWindowPos(Handle, IntPtr.Zero, finalX, finalY, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
            }
            else
            {
                IntPtr HWND_TOPMOST = (IntPtr)(-1);
                SetWindowPos(Handle, HWND_TOPMOST, finalX, finalY, w, h, SWP_NOACTIVATE);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(_transparentKey);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_cols == null) return;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            TaskbarRenderer.Render(g, _cols, _lastIsLightTheme);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
                if (_cfg != null && _cfg.TaskbarClickThrough)
                {
                    cp.ExStyle |= WS_EX_TRANSPARENT;
                }
                return cp;
            }
        }
    }
}