using System;
using System.Drawing;
using System.Windows.Forms;
using static LiteMonitor.src.UI.Helpers.NativeMethods;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// Win10 任务栏策略
    /// 机制：通过调整 MSTaskSwWClass (任务列表) 的位置来腾出空间 (挤占式)
    /// 特点：挂载到 ReBarWindow32，需要精确控制兄弟窗口位置
    /// </summary>
    public class TaskbarStrategyWin10 : ITaskbarStrategy
    {
        private readonly Form _form;
        
        // 关键句柄
        private IntPtr _hReBar = IntPtr.Zero;
        private IntPtr _hMin = IntPtr.Zero; // MSTaskSwWClass
        
        // 状态记录 (用于布局计算和恢复)
        private int _lastSqueezedWidth = -1;
        private int _lastUsedWidth = 0;
        private int _lastSqueezedHeight = -1;
        private int _lastUsedHeight = 0;
        private bool _lastAlignLeft = true;
        private bool _lastVertical = false;

        // 优化配置
        private const int GAP_TOLERANCE = 1; // 容忍 1px 的误差，防止反复重绘造成的闪烁
        public bool IsReady => _hReBar != IntPtr.Zero && _hMin != IntPtr.Zero;

        public bool HasInternalLayout => true;

        public TaskbarStrategyWin10(Form form)
        {
            _form = form;
        }

        public void Attach(IntPtr taskbarHandle)
        {
            InitializeHandles(taskbarHandle);

            IntPtr targetParent = taskbarHandle;
            // Win10下，为了与系统图标平级且正确遮挡，我们将父窗口设为 ReBarWindow32
            if (IsReady)
            {
                targetParent = _hReBar;
            }

            SetParent(_form.Handle, targetParent);
            TaskbarWinHelper.ApplyChildWindowStyle(_form.Handle);
        }

        public IntPtr GetExpectedParent(IntPtr taskbarHandle)
        {
            if (IsReady) return _hReBar;
            return taskbarHandle; // 如果初始化失败，回退到 Taskbar
        }

        public void SetPosition(IntPtr taskbarHandle, int left, int top, int w, int h, int manualOffset, bool alignLeft)
        {
            // 确保句柄已初始化
            if (!IsReady) InitializeHandles(taskbarHandle);

            if (TryAdjustLayout(w, h, manualOffset, alignLeft, out int win10X, out int win10Y))
            {
                SetWindowPos(_form.Handle, IntPtr.Zero, win10X, win10Y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
            }
            else
            {
                // 如果调整失败（例如找不到句柄），回退到简单定位
                // 注意：这里也应该应用 manualOffset，但通常 TaskbarBizHelper 对于非 internal layout 已经计算了
                SetWindowPos(_form.Handle, IntPtr.Zero, left, top, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }

        public void Restore()
        {
            if (IsReady)
            {
                // 恢复逻辑
                // 这里复用之前的 Restore 逻辑，但需要适配到当前类结构
                RestoreLayout();
            }
        }

        private void InitializeHandles(IntPtr hTaskbar)
        {
            // Win10 结构：Shell_TrayWnd -> ReBarWindow32 -> MSTaskSwWClass
            _hReBar = FindWindowEx(hTaskbar, IntPtr.Zero, "ReBarWindow32", null);
            if (_hReBar == IntPtr.Zero)
                _hReBar = FindWindowEx(hTaskbar, IntPtr.Zero, "WorkerW", null); 

            if (_hReBar != IntPtr.Zero)
            {
                _hMin = FindWindowEx(_hReBar, IntPtr.Zero, "MSTaskSwWClass", null);
                if (_hMin == IntPtr.Zero)
                    _hMin = FindWindowEx(_hReBar, IntPtr.Zero, "MSTaskListWClass", null); 
            }
        }

        // =================================================================
        // 核心布局逻辑 (从 TaskbarWin10LayoutHelper 移植)
        // =================================================================
        private bool TryAdjustLayout(int w, int h, int manualOffset, bool alignLeft, out int x, out int y)
        {
            x = 0; y = 0;
            if (!IsReady) return false;

            if (!GetWindowRect(_hMin, out RECT rMin) || !GetWindowRect(_hReBar, out RECT rBar))
                return false;

            Rectangle rcMin = Rectangle.FromLTRB(rMin.left, rMin.top, rMin.right, rMin.bottom);
            Rectangle rcBar = Rectangle.FromLTRB(rBar.left, rBar.top, rBar.right, rBar.bottom);

            bool isVertical = rcBar.Height > rcBar.Width;

            // 如果方向改变，重置状态
            if (isVertical != _lastVertical)
            {
                _lastSqueezedWidth = -1;
                _lastSqueezedHeight = -1;
                _lastVertical = isVertical;
            }

            if (isVertical)
            {
                return AdjustVerticalLayout(rcMin, rcBar, w, h, manualOffset, alignLeft, out x, out y);
            }
            else
            {
                return AdjustHorizontalLayout(rcMin, rcBar, w, h, manualOffset, alignLeft, out x, out y);
            }
        }

        private bool AdjustVerticalLayout(Rectangle rcMin, Rectangle rcBar, int w, int h, int manualOffset, bool alignLeft, out int x, out int y)
        {
            int currentRelTop = rcMin.Top - rcBar.Top;
            int originalRelTop = currentRelTop;
            int originalHeight = rcMin.Height;
            // [Fix 1] 增加容差判断，防止 1px 抖动导致的死循环闪烁
            bool isSqueezed = _lastSqueezedHeight > 0 && Math.Abs(rcMin.Height - _lastSqueezedHeight) <= GAP_TOLERANCE;

            if (isSqueezed)
            {
                originalHeight = rcMin.Height + _lastUsedHeight;
                if (_lastAlignLeft) // 之前是顶部挤占
                    originalRelTop = currentRelTop - _lastUsedHeight;
            }

            // [Fix 2] 挤占高度
            int effectiveHeight = h;
            int totalUsedHeight = h + manualOffset;
            bool needAdjust = !isSqueezed || (totalUsedHeight != _lastUsedHeight) || (alignLeft != _lastAlignLeft);

            int targetHeight = originalHeight;
            int targetRelTop = originalRelTop;
            
            if (alignLeft)
            {
                // 居顶模式
                // 任务栏图标起始位置 = 原始位置 + 插件高度 + 偏移量 (随插件一起下移)
                targetRelTop += effectiveHeight + manualOffset;
                // 剩余空间变小
                targetHeight = originalHeight - effectiveHeight - manualOffset;
                
                y = originalRelTop + manualOffset;
            }
            else
            {
                // 居底模式
                y = rcBar.Height - h - manualOffset; // Form 位置保持不变
                int maxTaskListHeight = y - originalRelTop;
                targetHeight = maxTaskListHeight;
            }

            if (needAdjust)
            {
                if (targetHeight > 0)
                {
                    MoveWindow(_hMin, 0, targetRelTop, rcMin.Width, targetHeight, true);
                    _lastSqueezedHeight = targetHeight;
                    _lastUsedHeight = originalHeight - targetHeight; 
                    _lastAlignLeft = alignLeft;
                }
            }
            else
            {
                _lastSqueezedHeight = rcMin.Height;
            }

            x = (rcBar.Width - w) / 2;
            return true;
        }

        private bool AdjustHorizontalLayout(Rectangle rcMin, Rectangle rcBar, int w, int h, int manualOffset, bool alignLeft, out int x, out int y)
        {
            int currentRelLeft = rcMin.Left - rcBar.Left;
            int originalRelLeft = currentRelLeft;
            int originalWidth = rcMin.Width;
            // [Fix 1] 增加容差判断，防止 1px 抖动导致的死循环闪烁
            bool isSqueezed = _lastSqueezedWidth > 0 && Math.Abs(rcMin.Width - _lastSqueezedWidth) <= GAP_TOLERANCE;

            if (isSqueezed)
            {
                originalWidth = rcMin.Width + _lastUsedWidth;
                if (_lastAlignLeft) 
                    originalRelLeft = currentRelLeft - _lastUsedWidth;
            }

            // [Fix 2] 挤占宽度
            int effectiveWidth = w;
            int totalUsedWidth = w + manualOffset;
            bool needAdjust = !isSqueezed || (totalUsedWidth != _lastUsedWidth) || (alignLeft != _lastAlignLeft);

            int targetWidth = originalWidth;
            int targetRelLeft = originalRelLeft;

            if (alignLeft)
            {
                // 居左模式
                // 任务栏图标起始位置 = 原始位置 + 插件宽度 + 偏移量 (随插件一起右移)
                targetRelLeft += effectiveWidth + manualOffset;
                // 剩余空间变小
                targetWidth = originalWidth - effectiveWidth - manualOffset;
                
                x = originalRelLeft + manualOffset;
            }
            else
            {
                // 居右模式
                x = rcBar.Width - w - manualOffset; // Form 位置保持不变
                int maxTaskListWidth = x - originalRelLeft;
                targetWidth = maxTaskListWidth;
            }

            if (needAdjust)
            {
                if (targetWidth > 0)
                {
                    MoveWindow(_hMin, targetRelLeft, 0, targetWidth, rcMin.Height, true);
                    _lastSqueezedWidth = targetWidth;
                    _lastUsedWidth = originalWidth - targetWidth;
                    _lastAlignLeft = alignLeft;
                }
            }
            else
            {
                _lastSqueezedWidth = rcMin.Width;
            }

            y = (rcBar.Height - h) / 2;
            return true;
        }

        private void RestoreLayout()
        {
            // 简单恢复：如果之前有记录挤占信息，尝试还原
            if (_lastSqueezedWidth != -1 || _lastSqueezedHeight != -1)
            {
                if (GetWindowRect(_hMin, out RECT rMin) && GetWindowRect(_hReBar, out RECT rBar))
                {
                    Rectangle rcMin = Rectangle.FromLTRB(rMin.left, rMin.top, rMin.right, rMin.bottom);
                    Rectangle rcBar = Rectangle.FromLTRB(rBar.left, rBar.top, rBar.right, rBar.bottom);
                    
                    bool isVertical = rcBar.Height > rcBar.Width;

                    if (isVertical)
                    {
                         // 垂直恢复
                         int currentRelTop = rcMin.Top - rcBar.Top;
                         int originalRelTop = currentRelTop;
                         if (_lastAlignLeft) originalRelTop = currentRelTop - _lastUsedHeight;
                         int originalHeight = rcMin.Height + _lastUsedHeight;

                         if (originalHeight > 0 && originalRelTop >= 0)
                            MoveWindow(_hMin, 0, originalRelTop, rcMin.Width, originalHeight, true);
                    }
                    else
                    {
                        // 水平恢复
                        int currentRelLeft = rcMin.Left - rcBar.Left;
                        int originalRelLeft = currentRelLeft;
                        if (_lastAlignLeft) originalRelLeft = currentRelLeft - _lastUsedWidth;
                        int originalWidth = rcMin.Width + _lastUsedWidth;

                        if (originalWidth > 0 && originalRelLeft >= 0)
                            MoveWindow(_hMin, originalRelLeft, 0, originalWidth, rcMin.Height, true);
                    }
                }
                _lastSqueezedWidth = -1;
                _lastSqueezedHeight = -1;
            }
        }
    }
}
