using System;
using System.Runtime.InteropServices;

namespace LiteMonitor
{
    /// <summary>
    /// Windows10 小组件（News & Interests）检测器
    /// 
    /// 提供：
    /// ① 是否存在小组件
    /// ② 当前显示模式（关闭 / 图标 / 文本）
    /// ③ 小组件窗口宽度（用于任务栏偏移）
    /// </summary>
    public static class WidgetDetector
    {
        // 小组件窗口类名（Windows 10 News & Interests）
        private const string WIDGET_CLASS = "Windows.UI.Composition.DesktopWindowContentBridge";

        public enum WidgetMode
        {
            Off,        // 完全关闭
            Icon,       // 仅图标（40 左右）
            Text        // 图标 + 文本（150 ~ 200）
        }

        // Win32 API
        [DllImport("user32.dll")] private static extern IntPtr FindWindow(string cls, string? name);
        [DllImport("user32.dll")] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        /// <summary>
        /// 返回 Windows10 小组件窗口句柄（可能为 IntPtr.Zero）
        /// </summary>
        public static IntPtr FindWidgetHandle()
        {
            IntPtr hTaskbar = FindWindow("Shell_TrayWnd", null);
            if (hTaskbar == IntPtr.Zero) return IntPtr.Zero;

            return FindWindowEx(hTaskbar, IntPtr.Zero, WIDGET_CLASS, null);
        }

        /// <summary>
        /// 返回小组件是否开启
        /// </summary>
        public static bool Exists() => FindWidgetHandle() != IntPtr.Zero;

        /// <summary>
        /// 获取小组件窗口宽度（DPI 已修正）
        /// 找不到窗口则返回 0
        /// </summary>
        public static int GetWidgetWidth()
        {
            IntPtr hwnd = FindWidgetHandle();
            if (hwnd == IntPtr.Zero) return 0;

            if (!GetWindowRect(hwnd, out RECT r)) return 0;

            int rawWidth = r.right - r.left;
            return ApplyDpiScale(rawWidth);
        }

        /// <summary>
        /// 判断小组件的显示模式（关闭 / 图标 / 文本）
        /// </summary>
        public static WidgetMode GetMode()
        {
            IntPtr hwnd = FindWidgetHandle();
            if (hwnd == IntPtr.Zero)
                return WidgetMode.Off;

            if (!GetWindowRect(hwnd, out RECT r))
                return WidgetMode.Off;

            int width = ApplyDpiScale(r.right - r.left);

            // ----- 判断逻辑 -----
            if (width < 60) return WidgetMode.Icon;   // 纯图标
            if (width < 140) return WidgetMode.Icon;  // 某些机器图标模式~60-100

            return WidgetMode.Text;                   // 文本模式（宽度明显更大）
        }

        /// <summary>
        /// 获取系统缩放比例
        /// </summary>
        private static int ApplyDpiScale(int px)
        {
            try
            {
                using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
                float dpi = g.DpiX;   // 96 → 1.0, 120 → 1.25, 144 → 1.5
                float scale = dpi / 96f;
                return (int)(px / scale);
            }
            catch
            {
                return px;
            }
        }
    }
}
