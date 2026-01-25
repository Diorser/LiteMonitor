using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using static LiteMonitor.src.UI.Helpers.TaskbarWinHelper;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 任务栏业务助手 (Business Helper)
    /// 职责：布局计算、位置定位、主题检测、菜单与双击逻辑
    /// </summary>
    public class TaskbarBizHelper
    {
        private readonly Form _form;
        private readonly Settings _cfg;
        private readonly TaskbarWinHelper _winHelper;
        
        private Rectangle _taskbarRect = Rectangle.Empty;
        private int _taskbarHeight = 32;
        private IntPtr _hTaskbar = IntPtr.Zero;
        private IntPtr _hTray = IntPtr.Zero;
        private bool _isWin11;
        
        // 样式相关
        private bool _lastIsLightTheme = false;
        private Color _transparentKey = Color.Black;

        public int Height => _taskbarHeight;
        public Rectangle Rect => _taskbarRect;
        public IntPtr HandleTaskbar => _hTaskbar;
        public Color TransparentKey => _transparentKey;
        public bool LastIsLightTheme => _lastIsLightTheme;

        public TaskbarBizHelper(Form form, Settings cfg, TaskbarWinHelper winHelper)
        {
            _form = form;
            _cfg = cfg;
            _winHelper = winHelper;
            _isWin11 = Environment.OSVersion.Version >= new Version(10, 0, 22000);
        }

        // =================================================================
        // 样式与主题
        // =================================================================
        public void CheckTheme(bool force = false)
        {
            bool isLight = _winHelper.IsSystemLightTheme();
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

            _winHelper.ApplyLayeredStyle(_transparentKey, _cfg.TaskbarClickThrough);
        }

        // =================================================================
        // 布局与定位
        // =================================================================
        public void FindHandles()
        {
            var handles = _winHelper.FindHandles(_cfg.TaskbarMonitorDevice);
            _hTaskbar = handles.hTaskbar;
            _hTray = handles.hTray;
        }

        public bool IsTaskbarValid()
        {
            if (_hTaskbar == IntPtr.Zero) return false;
            return TaskbarWinHelper.IsWindow(_hTaskbar);
        }

        public void AttachToTaskbar()
        {
            if (_hTaskbar == IntPtr.Zero) FindHandles();
            if (_hTaskbar == IntPtr.Zero) return;
            _winHelper.AttachToTaskbar(_hTaskbar);
        }

        public void UpdateTaskbarRect()
        {
            _taskbarRect = _winHelper.GetTaskbarRect(_hTaskbar, _cfg.TaskbarMonitorDevice);
            _taskbarHeight = Math.Max(24, _taskbarRect.Height);
        }

        public bool IsVertical()
        {
            return _taskbarRect.Height > _taskbarRect.Width;
        }

        public void UpdatePlacement(int panelWidth)
        {
            if (_hTaskbar == IntPtr.Zero) return;

            int leftScreen = _taskbarRect.Left;
            int topScreen;

            // ★★★ 垂直任务栏定位 ★★★
            if (IsVertical())
            {
                // 尝试定位到托盘上方
                int bottomLimit = _taskbarRect.Bottom;
                
                if (_hTray != IntPtr.Zero && _winHelper.GetWindowRectWrapper(_hTray, out Rectangle trayRect))
                {
                    if (trayRect.Top >= _taskbarRect.Top && trayRect.Bottom <= _taskbarRect.Bottom)
                    {
                        bottomLimit = trayRect.Top;
                    }
                }

                topScreen = bottomLimit - _form.Height - _cfg.TaskbarManualOffset - 6;
                if (topScreen < _taskbarRect.Top) topScreen = _taskbarRect.Top;

                _winHelper.SetPosition(_hTaskbar, leftScreen, topScreen, _taskbarRect.Width, _form.Height);
                return;
            }

            // ★★★ 水平任务栏定位 ★★★

            Screen currentScreen = Screen.FromRectangle(_taskbarRect);
            if (currentScreen == null) currentScreen = Screen.PrimaryScreen;
            
            bool sysCentered = TaskbarWinHelper.IsCenterAligned();
            bool isPrimary = currentScreen.Primary;
            
            int rawWidgetWidth = TaskbarWinHelper.GetWidgetsWidth();      
            int manualOffset = _cfg.TaskbarManualOffset; 
            int leftModeTotalOffset = rawWidgetWidth + manualOffset;
            int sysRightAvoid = sysCentered ? 0 : rawWidgetWidth;
            int rightModeTotalOffset = sysRightAvoid + manualOffset;

            int timeWidth = _isWin11 ? 90 : 0; 
            bool alignLeft = _cfg.TaskbarAlignLeft && sysCentered; 

            topScreen = _taskbarRect.Top;

            if (alignLeft)
            {
                int startX = _taskbarRect.Left + 6;
                if (leftModeTotalOffset > 0) startX += leftModeTotalOffset;
                leftScreen = startX;
            }
            else
            {
                if (isPrimary && _hTray != IntPtr.Zero && _winHelper.GetWindowRectWrapper(_hTray, out Rectangle tray))
                {
                    leftScreen = tray.Left - panelWidth - 6;
                    leftScreen -= rightModeTotalOffset;
                }
                else
                {
                    leftScreen = _taskbarRect.Right - panelWidth - 10;
                    leftScreen -= rightModeTotalOffset;
                    leftScreen -= timeWidth;
                }
            }

            _winHelper.SetPosition(_hTaskbar, leftScreen, topScreen, panelWidth, _taskbarHeight);
        }

        public void BuildVerticalLayout(List<Column> cols)
        {
            var s = _cfg.GetStyle(); 
            
            int w = _taskbarRect.Width;
            if (w < 20) w = 60; 

            int itemHeight = (int)(s.Size * 1.5f + 6); 
            if (itemHeight < 20) itemHeight = 20;

            int margin = Math.Max(0, s.Inner / 2);
            int contentWidth = w - (margin * 2);

            int y = 0;
            foreach (var col in cols)
            {
                col.Bounds = Rectangle.Empty;
                col.BoundsTop = Rectangle.Empty;
                col.BoundsBottom = Rectangle.Empty;

                if (col.Top != null)
                {
                    col.BoundsTop = new Rectangle(margin, y, contentWidth, itemHeight);
                    y += itemHeight;
                }

                if (col.Bottom != null)
                {
                    col.BoundsBottom = new Rectangle(margin, y, contentWidth, itemHeight);
                    y += itemHeight;
                }

                if (col.Top != null && col.Bottom == null)
                {
                    col.Bounds = col.BoundsTop;
                }

                y += s.VOff;
            }
            _form.Width = w;
            _form.Height = y;
        }

        // =================================================================
        // 交互动作
        // =================================================================
        public async void HandleDoubleClick(MainForm mainForm, UIController ui)
        {
            switch (_cfg.TaskbarDoubleClickAction)
            {
                case 1: try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr") { UseShellExecute = true }); } catch { } break;
                case 2: 
                    foreach (Form f in Application.OpenForms) { if (f is SettingsForm) { f.Activate(); return; } }
                    new SettingsForm(_cfg, ui, mainForm).Show(); 
                    break;
                case 3: 
                    foreach (Form f in Application.OpenForms) { if (f is TrafficHistoryForm) { f.Activate(); return; } }
                    new TrafficHistoryForm(_cfg).Show(); 
                    break;
                case 4: 
                    try { using (var form = new CleanMemoryForm()) await form.StartCleaningAsync(); } catch { } 
                    break;
                case 0: 
                default:
                    if (mainForm.Visible) mainForm.HideMainWindow(); else mainForm.ShowMainWindow();
                    break;
            }
        }
    }
}
