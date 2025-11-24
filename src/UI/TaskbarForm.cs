using Microsoft.Win32;
using System.Runtime.InteropServices;
// 需要新增 using
using LiteMonitor.src.Core;

namespace LiteMonitor
{
    public class TaskbarForm : Form
    {
        private Dictionary<uint, ToolStripItem> _commandMap = new Dictionary<uint, ToolStripItem>();
        private readonly Settings _cfg;
        private readonly UIController _ui;
        private readonly System.Windows.Forms.Timer _timer = new();

        private readonly HorizontalLayout _layout;

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

        public TaskbarForm(Settings cfg, UIController ui)
        {
            _cfg = cfg;
            _ui = ui;
            // 初始化：LayoutMode.Taskbar
            _layout = new HorizontalLayout(
                ThemeManager.Current,
                300,                 // 给一个初始宽度，不影响最终结果，只避免 0 值问题
                LayoutMode.Taskbar
            );

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

            // 强制创建句柄
            //CreateControl();
            CreateHandle();

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
                    object val = key.GetValue("SystemUsesLightTheme");
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

            // 核心修复：
            // 浅色模式 -> 背景设为指定色值（R:210, G:210, B:211）
            // 深色模式 -> 背景设为黑色
            if (isLight)
                _transparentKey = Color.FromArgb(210, 210, 211);
            else
                _transparentKey = Color.Black;

            // 更新 WinForms 属性
            BackColor = _transparentKey;

            // 更新 API 属性 (如果句柄已创建)
            if (IsHandleCreated)
            {
                ApplyLayeredAttribute();
            }
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
            // 1. 检查主题是否改变，如果改变会自动更新背景色 Key
            CheckTheme();

            _cols = _ui.GetHorizontalColumns();
            if (_cols == null || _cols.Count == 0) return;

            // 更新任务栏高度
            UpdateTaskbarRect();

            // 使用新的任务栏布局计算列宽
            _layout.Build(_cols);
            Width = _layout.PanelWidth;
            Height = _taskbarHeight;

            // 更新任务栏位置
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
                _taskbarRect = new Rectangle(s.WorkingArea.Left, s.WorkingArea.Bottom, s.WorkingArea.Width, s.Bounds.Bottom - s.WorkingArea.Bottom);
            }
            _taskbarHeight = Math.Max(24, _taskbarRect.Height);
        }

        private bool IsCenterAligned()
        {
            if (!_isWin11) return false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                int v = (int)(key?.GetValue("TaskbarAl", 1) ?? 1);
                return v == 1;
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



        /// <summary>
        /// 获取任务栏“小组件 / 新闻和兴趣” 实际占用的宽度
        /// Win11 Widgets：150
        /// Win10 News & Interests：150
        /// Win10 小图标模式：40
        /// 全部关闭：0
        /// </summary>
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

            // ==========================
            //  Windows 10 News & Interests
            // ==========================
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Feeds");

                if (key == null)
                    return 0;

                object? val = key.GetValue("ShellFeedsTaskbarViewMode");
                if (val is not int mode)
                    return 0;

                return mode switch
                {
                    0 => 150,  // 文字 + 天气图标
                    1 => 40,   // 小图标模式（只天气图标）
                    2 => 0,    // 关闭
                    _ => 0
                };
            }
            catch
            {
                return 0;
            }
        }


        private void UpdatePlacement(int panelWidth)
        {
            if (_hTaskbar == IntPtr.Zero) return;

            var scr = Screen.PrimaryScreen;
            bool bottom = _taskbarRect.Bottom >= scr.Bounds.Bottom - 2;
            bool centered = IsCenterAligned();
            int widgetsWidth = GetWidgetsWidth();
            int leftScreen, topScreen;

            if (bottom)
            {
                topScreen = _taskbarRect.Bottom - _taskbarHeight;
                if (centered) leftScreen = _taskbarRect.Left + widgetsWidth + 6;
                else
                {
                    GetWindowRect(_hTray, out RECT tray);
                    leftScreen = tray.left - widgetsWidth - panelWidth - 6;
                }
            }
            else
            {
                topScreen = _taskbarRect.Top;
                if (centered) leftScreen = _taskbarRect.Left + 6;
                else
                {
                    GetWindowRect(_hTray, out RECT tray);
                    leftScreen = tray.left - panelWidth - 6;
                }
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

            TaskbarRenderer.Render(g, _cols, _taskbarHeight);

        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
                return cp;
            }
        }

    }


}


