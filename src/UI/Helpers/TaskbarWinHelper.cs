using Microsoft.Win32;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 任务栏窗口底层助手 (Windows Helper)
    /// 职责：Win32 API、句柄查找、挂载逻辑、样式与图层设置
    /// </summary>
    public class TaskbarWinHelper
    {
        private readonly Form _form;

        // ★★★ 性能优化缓存 ★★★
        private Rectangle _lastWindowRect = Rectangle.Empty;
        private Rectangle _cachedDwmRect = Rectangle.Empty;
        private bool _isCacheValid = false;

        public TaskbarWinHelper(Form form)
        {
            _form = form;
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
        // 挂载逻辑
        // =================================================================
        public void AttachToTaskbar(IntPtr taskbarHandle)
        {
            SetParent(_form.Handle, taskbarHandle);

            int style = GetWindowLong(_form.Handle, GWL_STYLE);
            style &= (int)~0x80000000; 
            style |= WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS;
            SetWindowLong(_form.Handle, GWL_STYLE, style);
        }

        public void SetPosition(IntPtr taskbarHandle, int left, int top, int w, int h)
        {
            IntPtr currentParent = GetParent(_form.Handle);
            bool isAttached = (currentParent == taskbarHandle);

            if (!isAttached)
            {
                AttachToTaskbar(taskbarHandle);
                currentParent = GetParent(_form.Handle);
                isAttached = currentParent == taskbarHandle;
            }

            int finalX = left;
            int finalY = top;
            
            if (isAttached)
            {
                POINT pt = new POINT { X = left, Y = top };
                ScreenToClient(taskbarHandle, ref pt);
                finalX = pt.X;
                finalY = pt.Y;
                SetWindowPos(_form.Handle, IntPtr.Zero, finalX, finalY, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
            }
            else
            {
                IntPtr HWND_TOPMOST = (IntPtr)(-1);
                SetWindowPos(_form.Handle, HWND_TOPMOST, finalX, finalY, w, h, SWP_NOACTIVATE);
            }
        }

        // =================================================================
        // 句柄与信息获取
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
            // 优先使用 GetWindowRect 获取真实窗口大小
            // 修复 #213: Surface 等二合一设备在键盘连接/断开切换时，SHAppBarMessage 可能返回错误的缓存高度，导致显示异常
            if (hTaskbar != IntPtr.Zero && GetWindowRect(hTaskbar, out RECT r))
            {
                var rectW = Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);

                // ★★★ 性能优化：缓存机制 ★★★
                // 只有当物理窗口大小/位置发生变化，或者缓存无效时，才去查询 DWM
                if (_isCacheValid && rectW == _lastWindowRect)
                {
                    return _cachedDwmRect;
                }

                _lastWindowRect = rectW;
                _cachedDwmRect = rectW; // 默认回退值
                _isCacheValid = true;

                // 修复 #231: 多显示器/DPI切换后，GetWindowRect 可能返回包含透明区域的虚高尺寸
                // 尝试使用 DWM 获取实际视觉边界 (Extended Frame Bounds)
                try
                {
                    if (DwmGetWindowAttribute(hTaskbar, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT dwmRect, Marshal.SizeOf(typeof(RECT))) == 0)
                    {
                        var rectD = Rectangle.FromLTRB(dwmRect.left, dwmRect.top, dwmRect.right, dwmRect.bottom);

                        // 如果 DWM 返回的高度有效且更小 (说明 WindowRect 包含了虚空区域)
                        // 且高度差异超过 2px (忽略微小误差)，优先使用 DWM 的视觉边界
                        if (rectD.Height > 0 && rectD.Height < rectW.Height && (rectW.Height - rectD.Height > 2))
                        {
                            _cachedDwmRect = rectD;
                        }
                    }
                }
                catch { /* Ignore DWM errors */ }

                return _cachedDwmRect;
            }

            // Fallback (通常不会走到这里，除非句柄无效)
            bool isPrimary = (hTaskbar == FindWindow("Shell_TrayWnd", null));

            if (isPrimary)
            {
                APPBARDATA abd = new APPBARDATA();
                abd.cbSize = Marshal.SizeOf(abd);
                uint res = SHAppBarMessage(ABM_GETTASKBARPOS, ref abd);
                if (res != 0)
                {
                    return Rectangle.FromLTRB(abd.rc.left, abd.rc.top, abd.rc.right, abd.rc.bottom);
                }
                else
                {
                    var s = Screen.PrimaryScreen;
                    if (s != null)
                        return new Rectangle(s.Bounds.Left, s.Bounds.Bottom - 40, s.Bounds.Width, 40);
                }
            }
            else
            {
                // 副屏兜底
                Screen target = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == targetDevice) ?? Screen.PrimaryScreen;
                return new Rectangle(target.Bounds.Left, target.Bounds.Bottom - 40, target.Bounds.Width, 40);
            }
            return Rectangle.Empty;
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
            if (Environment.OSVersion.Version >= new Version(10, 0, 22000))
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

        // -------------------------------------------------------------
        // Win32 API
        // -------------------------------------------------------------
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
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] public static extern uint RegisterWindowMessage(string lpString);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("shell32.dll")] private static extern uint SHAppBarMessage(uint msg, ref APPBARDATA pData);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

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
        private const uint ABM_GETTASKBARPOS = 5;

        public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
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

        public static void ActivateWindow(IntPtr handle) => SetForegroundWindow(handle);
    }
}
