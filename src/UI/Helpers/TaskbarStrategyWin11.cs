using System;
using System.Drawing;
using System.Windows.Forms;
using static LiteMonitor.src.UI.Helpers.NativeMethods;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// Win11 任务栏策略
    /// 机制：使用独立的顶层窗口覆盖任务栏
    /// 特点：沿用业务层计算出的屏幕坐标定位，避免任务栏子窗口的 DPI/透明合成问题。
    /// </summary>
    public class TaskbarStrategyWin11 : ITaskbarStrategy
    {
        private readonly Form _form;

        public bool IsReady => true; // Win11 模式不需要特殊的预热

        public bool HasInternalLayout => false;

        public TaskbarStrategyWin11(Form form)
        {
            _form = form;
        }

        public void Attach(IntPtr taskbarHandle)
        {
            // Keep the form top-level. Per-pixel layered surfaces are reliably
            // composed by DWM for top-level windows on current Windows 11.
        }

        public void SetPosition(IntPtr taskbarHandle, int left, int top, int w, int h, int manualOffset, bool alignLeft)
        {
            // Win11 模式下，位置由外部业务逻辑计算 (TaskbarBizHelper) 且已包含 manualOffset，
            // 这里只负责转换坐标系，忽略 manualOffset 参数以避免重复偏移。
            // 如果已经 Attach，需要将屏幕坐标转换为 Client 坐标
            
            // Screen coordinates are intentional because this is a top-level
            // overlay rather than a child of Shell_TrayWnd.
            // HWND_TOPMOST places the overlay at the front of the topmost
            // group; Shell_TrayWnd itself is topmost and otherwise covers it.
            IntPtr insertAfter = (IntPtr)(-1);
            SetWindowPos(_form.Handle, insertAfter, left, top, w, h,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        public void Restore()
        {
            // Win11 模式没有修改系统窗口，不需要恢复
        }

        public IntPtr GetExpectedParent(IntPtr taskbarHandle)
        {
            return IntPtr.Zero;
        }
    }
}
