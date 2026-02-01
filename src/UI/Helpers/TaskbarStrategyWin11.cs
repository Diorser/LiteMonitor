using System;
using System.Drawing;
using System.Windows.Forms;
using static LiteMonitor.src.UI.Helpers.NativeMethods;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// Win11 任务栏策略
    /// 机制：使用 SetParent 将窗口挂载到 Shell_TrayWnd 或 Shell_SecondaryTrayWnd
    /// 特点：依赖系统自身的布局，不需要手动挤占空间
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
            // Win11 直接挂载到任务栏句柄
            SetParent(_form.Handle, taskbarHandle);
            TaskbarWinHelper.ApplyChildWindowStyle(_form.Handle);
        }

        public void SetPosition(IntPtr taskbarHandle, int left, int top, int w, int h, int manualOffset, bool alignLeft)
        {
            // Win11 模式下，位置由外部业务逻辑计算 (TaskbarBizHelper) 且已包含 manualOffset，
            // 这里只负责转换坐标系，忽略 manualOffset 参数以避免重复偏移。
            // 如果已经 Attach，需要将屏幕坐标转换为 Client 坐标
            
            IntPtr currentParent = GetParent(_form.Handle);
            bool isAttached = (currentParent == taskbarHandle);

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
                // 如果意外脱离，尝试使用 HWND_TOPMOST 保持可见
                IntPtr HWND_TOPMOST = (IntPtr)(-1);
                SetWindowPos(_form.Handle, HWND_TOPMOST, finalX, finalY, w, h, SWP_NOACTIVATE);
            }
        }

        public void Restore()
        {
            // Win11 模式没有修改系统窗口，不需要恢复
        }

        public IntPtr GetExpectedParent(IntPtr taskbarHandle)
        {
            return taskbarHandle;
        }
    }
}
