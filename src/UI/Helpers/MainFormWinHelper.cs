using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 主窗口底层助手 (Windows Helper)
    /// 职责：封装 Win32 API、窗口样式、圆角、穿透、透明度等底层操作
    /// </summary>
    public class MainFormWinHelper
    {
        private readonly Form _form;

        public MainFormWinHelper(Form form)
        {
            _form = form;
        }

        public void InitializeStyle(bool topMost, bool clickThrough)
        {
            _form.FormBorderStyle = FormBorderStyle.None;
            _form.ShowInTaskbar = false;
            _form.TopMost = topMost;


            
            // 解决 DoubleBuffered 访问权限问题
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(_form, true, null);

            // 原始逻辑还原：
            // 在原始代码中，Opacity 是在构造函数末尾启动异步任务来渐变的
            // 而不是在 InitializeStyle 这种早期阶段直接设为 0
            // 这里的 Opacity = 0 会导致窗体在 OnShown 之前就是透明的，
            // 但如果 SetStyle(ControlStyles.SupportsTransparentBackColor, true) 还没生效或冲突
            // 可能会导致这一瞬间的绘制异常（如黑色闪烁）
            // 
            // 修正：删除这里的 _form.Opacity = 0; 
            // 改回完全依赖 StartFadeIn 方法来控制，并且那个方法是在 OnShown 之后调用的
            
            ApplyRoundedCorners();
            if (clickThrough) SetClickThrough(true);

            // 绑定 Resize 事件以自动重绘圆角
            _form.Resize += (_, __) => ApplyRoundedCorners();
        }

        public bool IsTopMostStyleApplied()
        {
            try
            {
                if (_form.IsDisposed || !_form.IsHandleCreated) return false;

                int ex = GetWindowLong(_form.Handle, GWL_EXSTYLE);
                return (ex & WS_EX_TOPMOST) != 0;
            }
            catch
            {
                return false;
            }
        }

        public void RefreshTopMost(bool enabled, bool forceReinsert = false)
        {
            try
            {
                if (_form.IsDisposed || !_form.IsHandleCreated) return;

                _form.TopMost = enabled;
                uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;

                if (enabled)
                {
                    // 仅在明确恢复/用户操作时重插 Z 序，避免定时器频繁抢其它置顶窗口。
                    if (forceReinsert)
                    {
                        SetWindowPos(_form.Handle, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
                    }
                    SetWindowPos(_form.Handle, HWND_TOPMOST, 0, 0, 0, 0, flags);
                }
                else
                {
                    SetWindowPos(_form.Handle, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
                }
            }
            catch { }
        }

        // =================================================================
        // 透明度渐变
        // =================================================================
        public void StartFadeIn()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // 构造阶段句柄可能还没创建：等待句柄可用后再开始，否则会直接退出导致整窗 Alpha 停在 0
                    for (int i = 0; i < 60; i++)
                    {
                        if (_form.IsDisposed) return;
                        if (_form.IsHandleCreated) break;
                        await Task.Delay(16).ConfigureAwait(false);
                    }
                    if (_form.IsDisposed || !_form.IsHandleCreated) return;

                    double current = 0;
                    while (current < 1.0)
                    {
                        await Task.Delay(16).ConfigureAwait(false);
                        current += 0.05;
                        if (current > 1.0) current = 1.0;
                        double value = current;

                        if (_form.IsDisposed || !_form.IsHandleCreated) break;
                        try
                        {
                            _form.BeginInvoke(new Action(() =>
                            {
                                // 分层窗口使用自维护 Alpha，不再依赖 Form.Opacity
                                if (_form is MainForm main) main.SetFadeAlpha(value);
                            }));
                        }
                        catch { break; }

                        if (value >= 1.0) break;
                    }
                }
                catch { }
            });
        }

        // =================================================================
        // 分层窗口提交 (逐像素 Alpha)
        // =================================================================
        public void UpdateLayeredWindow(Bitmap bitmap)
        {
            if (bitmap == null || _form.IsDisposed || !_form.IsHandleCreated) return;

            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;

            try
            {
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                oldBitmap = SelectObject(memDc, hBitmap);

                SIZE size = new SIZE(bitmap.Width, bitmap.Height);
                POINT srcPoint = new POINT(0, 0);
                POINT topPoint = new POINT(_form.Left, _form.Top);
                BLENDFUNCTION blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };

                UpdateLayeredWindow(_form.Handle, screenDc, ref topPoint, ref size,
                    memDc, ref srcPoint, 0, ref blend, ULW_ALPHA);
            }
            catch { }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                {
                    SelectObject(memDc, oldBitmap);
                    DeleteObject(hBitmap);
                }
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // =================================================================
        // 鼠标穿透
        // =================================================================
        public void SetClickThrough(bool enable)
        {
            try
            {
                int ex = GetWindowLong(_form.Handle, GWL_EXSTYLE);
                if (enable)
                    SetWindowLong(_form.Handle, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED);
                else
                    SetWindowLong(_form.Handle, GWL_EXSTYLE, ex & ~WS_EX_TRANSPARENT);
            }
            catch { }
        }

        // =================================================================
        // 圆角处理 (Hybrid: Win11 DWM / Win10 Region)
        // =================================================================
        public void ApplyRoundedCorners()
        {
            try
            {
                bool isWin11 = Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000;

                if (isWin11)
                {
                    _form.Region = null;
                    int preference = DWMWCP_ROUND;
                    DwmSetWindowAttribute(_form.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
                    int borderColor = DWMWA_COLOR_NONE;
                    DwmSetWindowAttribute(_form.Handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
                }
                else
                {
                    var t = ThemeManager.Current;
                    int r = Math.Max(0, t.Layout.CornerRadius);

                    if (r == 0)
                    {
                        _form.Region = null;
                        return;
                    }

                    using var gp = new GraphicsPath();
                    int d = r * 2;
                    gp.AddArc(0, 0, d, d, 180, 90);
                    gp.AddArc(_form.Width - d, 0, d, d, 270, 90);
                    gp.AddArc(_form.Width - d, _form.Height - d, d, d, 0, 90);
                    gp.AddArc(0, _form.Height - d, d, d, 90, 90);
                    gp.CloseFigure();

                    _form.Region?.Dispose();
                    _form.Region = new Region(gp);
                }
            }
            catch { }
        }

        // =================================================================
        // Win32 API
        // =================================================================
        public static int RegisterTaskbarCreatedMessage()
        {
            int msgId = RegisterWindowMessage("TaskbarCreated");
            if (msgId != 0)
            {
                ChangeWindowMessageFilter((uint)msgId, MSGFLT_ADD);
            }
            return msgId;
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] public static extern int RegisterWindowMessage(string lpString);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);

        // 分层窗口
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; public POINT(int x, int y) { X = x; Y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int Width; public int Height; public SIZE(int w, int h) { Width = w; Height = h; } }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const int ULW_ALPHA = 0x00000002;

        public const uint MSGFLT_ADD = 1;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;

        public static void ActivateWindow(IntPtr handle) => SetForegroundWindow(handle);
    }
}
