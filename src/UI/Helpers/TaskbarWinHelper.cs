using Microsoft.Win32;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using static LiteMonitor.src.UI.Helpers.NativeMethods;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 任务栏集成策略接口
    /// 定义了不同系统版本下任务栏挂载和布局的统一行为
    /// </summary>
    public interface ITaskbarStrategy
    {
        /// <summary>
        /// 是否准备就绪
        /// </summary>
        bool IsReady { get; }

        /// <summary>
        /// 挂载到任务栏
        /// </summary>
        void Attach(IntPtr taskbarHandle);

        /// <summary>
        /// 设置位置和大小
        /// </summary>
        void SetPosition(IntPtr taskbarHandle, int left, int top, int w, int h, int manualOffset, bool alignLeft);

        /// <summary>
        /// 恢复任务栏原始布局（如果做过修改）
        /// </summary>
        void Restore();

        /// <summary>
        /// 获取期望的父窗口句柄（用于检测是否脱离）
        /// </summary>
        IntPtr GetExpectedParent(IntPtr taskbarHandle);

        /// <summary>
        /// 是否拥有内部布局逻辑（如 Win10 挤占模式）
        /// </summary>
        bool HasInternalLayout { get; }
    }

    internal static class NativeMethods
    {
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int WS_CHILD = 0x40000000;
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_CLIPSIBLINGS = 0x04000000;
        public const int WS_EX_LAYERED = 0x80000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const uint LWA_COLORKEY = 0x00000001;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string? name);
        [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int idx);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int idx, int value);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);
        [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Auto)] public static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
        [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    }

    /// <summary>
    /// 任务栏窗口底层助手 (Windows Helper) - Facade
    /// 职责：作为统一入口，根据系统版本委托给具体的策略 (Strategy) 处理挂载和布局
    /// </summary>
    public class TaskbarWinHelper
    {
        private readonly Form _form;
        private readonly Settings _cfg;
        private readonly ITaskbarStrategy _strategy;

        // ★★★ 性能优化缓存 ★★★
        private Rectangle _lastWindowRect = Rectangle.Empty;
        private Rectangle _cachedResult = Rectangle.Empty;
        private bool _isCacheValid = false;

        // [Optimization] 静态缓存系统版本检测结果
        private static readonly bool _isWin11 = Environment.OSVersion.Version.Major == 10 && Environment.OSVersion.Version.Build >= 22000;

        public bool UsesInternalLayout => _strategy.HasInternalLayout;
        
        public TaskbarWinHelper(Form form, Settings cfg)
        {
            _form = form;
            _cfg = cfg;
            // 策略工厂模式：根据系统版本选择合适的集成策略
            if (_isWin11)
            {
                _strategy = new TaskbarStrategyWin11(form);
            }
            else
            {
                _strategy = new TaskbarStrategyWin10(form);
            }
        }

        // =================================================================
        // 样式与图层
        // =================================================================
        public void ApplyLayeredStyle(Color transparentKey, bool clickThrough)
        {
            _form.BackColor = transparentKey;
            
            if (_form.IsHandleCreated)
            {
                uint colorKey = (uint)(transparentKey.R | (transparentKey.G << 8) | (transparentKey.B << 16));
                SetLayeredWindowAttributes(_form.Handle, colorKey, 0, LWA_COLORKEY);
            }

            int exStyle = GetWindowLong(_form.Handle, GWL_EXSTYLE);
            if (clickThrough) exStyle |= WS_EX_TRANSPARENT; 
            else exStyle &= ~WS_EX_TRANSPARENT; 
            SetWindowLong(_form.Handle, GWL_EXSTYLE, exStyle);
            
            _form.Invalidate();
        }

        public bool IsSystemLightTheme()
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

        // =================================================================
        // 挂载逻辑 (委托给策略)
        // =================================================================
        public void AttachToTaskbar(IntPtr taskbarHandle)
        {
            _strategy.Attach(taskbarHandle);
        }

        public void SetPosition(IntPtr taskbarHandle, int left, int top, int w, int h, int manualOffset = 0, bool alignLeft = true)
        {
            // 检查父窗口是否正确，防止脱离
            IntPtr currentParent = GetParent(_form.Handle);
            IntPtr expectedParent = _strategy.GetExpectedParent(taskbarHandle);

            if (currentParent != expectedParent)
            {
                AttachToTaskbar(taskbarHandle);
            }

            _strategy.SetPosition(taskbarHandle, left, top, w, h, manualOffset, alignLeft);
        }

        public void RestoreTaskbar()
        {
            _strategy.Restore();
        }

        public void InvalidateCache()
        {
            _isCacheValid = false;
        }

        // =================================================================
        // 句柄与信息获取 (通用逻辑)
        // =================================================================
        public (IntPtr hTaskbar, IntPtr hTray) FindHandles(string targetDevice)
        {
            Screen target = Screen.PrimaryScreen;
            if (!string.IsNullOrEmpty(targetDevice))
            {
                target = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == targetDevice) ?? Screen.PrimaryScreen;
            }

            if (target.Primary)
            {
                IntPtr hTaskbar = FindWindow("Shell_TrayWnd", null);
                IntPtr hTray = FindWindowEx(hTaskbar, IntPtr.Zero, "TrayNotifyWnd", null);
                return (hTaskbar, hTray);
            }
            else
            {
                IntPtr hTaskbar = FindSecondaryTaskbar(target);
                return (hTaskbar, IntPtr.Zero);
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

        public Rectangle GetTaskbarRect(IntPtr hTaskbar, string targetDevice)
        {
            if (hTaskbar == IntPtr.Zero) return Rectangle.Empty;

            // 1. 获取物理矩形
            if (!GetWindowRect(hTaskbar, out RECT r)) return Rectangle.Empty;
            var rectPhys = Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);

            // 缓存检查
            if (_isCacheValid && rectPhys == _lastWindowRect) return _cachedResult;

            Rectangle finalRect = rectPhys;

            // =========================================================================
            // [SIMPLIFIED FIX] 极简稳定方案：基于 WorkingArea 和 强制高度修正 (DPI适配版)
            // =========================================================================
            try
            {
                Screen screen = null;
                if (!string.IsNullOrEmpty(targetDevice)) 
                    screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == targetDevice);
                if (screen == null) 
                    screen = Screen.FromHandle(hTaskbar);

                if (screen != null)
                {
                    Rectangle workArea = screen.WorkingArea;
                    Rectangle screenBounds = screen.Bounds;
                    int reservedBottom = screenBounds.Bottom - workArea.Bottom;

                    // 场景 A: 锚定模式
                    if (reservedBottom > 2) 
                    {
                        finalRect = new Rectangle(rectPhys.Left, workArea.Bottom, rectPhys.Width, reservedBottom);
                    }
                    // 场景 B: 悬浮模式
                    else
                    {
                        if (_isWin11)
                        {
                            // [Fix] 增加对垂直任务栏的判断，防止误判
                            // StartAllBack 等软件可能启用垂直任务栏，此时不应应用底部水平任务栏的高度修正
                            bool isVertical = rectPhys.Height > rectPhys.Width;

                            if (!isVertical)
                            {
                                int dpi = GetTaskbarDpi();
                                int baseHeight = _cfg?.Win11TaskbarHeight ?? 48;
                                int standardHeight = (int)Math.Round((double)baseHeight * dpi / 96.0);

                                if (rectPhys.Height > (standardHeight * 0.8))
                                {
                                    finalRect = new Rectangle(
                                        rectPhys.Left, 
                                        rectPhys.Bottom - standardHeight, 
                                        rectPhys.Width, 
                                        standardHeight);
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            _lastWindowRect = rectPhys;
            _cachedResult = finalRect;
            _isCacheValid = true;

            return _cachedResult;
        }

        public bool GetWindowRectWrapper(IntPtr hWnd, out Rectangle rect)
        {
            if (GetWindowRect(hWnd, out RECT r))
            {
                rect = Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
                return true;
            }
            rect = Rectangle.Empty;
            return false;
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
            int dpi = GetTaskbarDpi();
            if (_isWin11)
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string pkg = Path.Combine(local, "Packages");
                bool hasWidgetPkg = false;
                try { hasWidgetPkg = Directory.GetDirectories(pkg, "MicrosoftWindows.Client.WebExperience*").Any(); } catch {}
                
                if (!hasWidgetPkg) return 0;

                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                if (key == null) return 0;

                object? val = key.GetValue("TaskbarDa");
                if (val is int i && i != 0) return 150 * dpi / 96;
            }
            return 0;
        }

        public static void ActivateWindow(IntPtr handle) => SetForegroundWindow(handle);

        public static bool IsWindow(IntPtr hWnd) => NativeMethods.IsWindow(hWnd);

        public static void ApplyChildWindowStyle(IntPtr hWnd)
        {
            int style = GetWindowLong(hWnd, GWL_STYLE);
            style &= (int)~0x80000000; 
            style |= WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS;
            SetWindowLong(hWnd, GWL_STYLE, style);
        }
    }
}
