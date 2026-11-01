using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.UI;
using LiteMonitor.src.UI.Helpers;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiteMonitor
{
    public class MainForm : Form
    {
        private readonly Settings _cfg = Settings.Load();
        private UIController? _ui;
        
        // ★★★ 双助手架构 ★★★
        private readonly MainFormWinHelper _winHelper;
        private readonly MainFormBizHelper _bizHelper;
        private readonly int _wmTaskbarCreated;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private CancellationTokenSource _displayChangeCts;

        private Point _dragOffset;
        private bool _uiDragging = false;

        // ==== 分层窗口渲染 ====
        // 背景与文字透明度需要逐像素 Alpha，Form.Opacity 只能整体淡化，因此改用 UpdateLayeredWindow。
        private Bitmap? _renderBitmap;
        private float _renderBitmapDpi = 0f;
        private double _fadeAlpha = 0.0;

        // 防止 Win11 自动隐藏无边框 + 无任务栏窗口
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW
                cp.ExStyle &= ~0x00040000; // WS_EX_APPWINDOW
                // 始终启用分层窗口（逐像素 Alpha 的前提）
                cp.ExStyle |= 0x80000; // WS_EX_LAYERED
                if (_cfg != null && _cfg.TopMost)
                {
                    cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST，防止句柄重建后丢失置顶样式
                }
                
                // [Fix] 启动时应用鼠标穿透配置，防止因句柄重建导致样式丢失
                if (_cfg != null && _cfg.ClickThrough)
                {
                    cp.ExStyle |= 0x20; // WS_EX_TRANSPARENT
                }

                return cp;
            }
        }

        // ========== 代理方法 (保持兼容性) ==========
        public void SetClickThrough(bool enable) => _winHelper.SetClickThrough(enable);
        public void InitAutoHideTimer() => _bizHelper.StartTimer();
        public void StopAutoHideTimer() => _bizHelper.StopTimer();
        public void HideTrayIcon() => _bizHelper.SetTrayVisible(false);
        public void ShowTrayIcon() => _bizHelper.SetTrayVisible(true);
        public void RebuildMenus() => _bizHelper.RebuildMenus();
        public void ShowNotification(string title, string text, ToolTipIcon icon) => _bizHelper.ShowNotification(title, text, icon);
        
        // 供 Helper 调用
        public void ToggleLayoutMode() => _bizHelper.ToggleLayoutMode();
        public void EnsureVisibleAndSavePos() => _bizHelper.SavePos();
        public void ApplyRoundedCorners() => _winHelper.ApplyRoundedCorners();
        public void RefreshTopMost(bool forceReinsert = false) => _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert);
        
        // 供外部调用
        public void OpenTaskManager() => _bizHelper.OpenTaskManager();
        public void OpenSettings() => _bizHelper.OpenSettings();
        public void OpenTrafficHistory() => _bizHelper.OpenTrafficHistory();
        public void CleanMemory() => _bizHelper.CleanMemory();

        // ==== 任务栏显示 ====
        private TaskbarForm? _taskbar;

        public void ToggleTaskbar(bool show)
        {
            if (show)
            {
                if (_taskbar != null && !_taskbar.IsDisposed)
                {
                    if (_taskbar.TargetDevice != _cfg.TaskbarMonitorDevice)
                    {
                        _taskbar.Close();
                        _taskbar.Dispose();
                        _taskbar = null;
                    }
                }

                if (_taskbar == null || _taskbar.IsDisposed)
                {
                    if (_ui != null)
                    {
                        _taskbar = new TaskbarForm(_cfg, _ui, this);
                        _taskbar.Show();
                    }
                }
                else
                {
                    if (!_taskbar.Visible)
                    {
                        _taskbar.Show();
                        _taskbar.ReloadLayout();
                    }
                }
            }
            else
            {
                if (_taskbar != null)
                {
                    _taskbar.Close();
                    _taskbar.Dispose();
                    _taskbar = null;
                }
            }
        }

        // ========== 构造函数 ==========
        public MainForm()
        {
            // 语言加载
            if (string.IsNullOrEmpty(_cfg.Language))
            {
                string sysLang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLower();
                string langPath = Path.Combine(AppContext.BaseDirectory, "resources/lang", $"{sysLang}.json");
                _cfg.Language = File.Exists(langPath) ? sysLang : "en";
            }
            LanguageManager.Load(_cfg.Language);
            _cfg.SyncToLanguage();

            // 1. 初始化业务
            // ★★★ Fix: 初始化全局 DPI 缩放系数，防止未打开设置面板时弹窗排版异常 ★★★
            UIUtils.ScaleFactor = this.DeviceDpi / 96f;

            TrafficLogger.Load();
            HardwareHistoryLogger.Load();
            src.Plugins.PluginManager.Instance.LoadPlugins(Path.Combine(AppContext.BaseDirectory, "resources", "plugins"));
            src.Plugins.PluginManager.Instance.Start();
            _ui = new UIController(_cfg, this);
            new src.WebServer.LiteWebServer(_cfg);

            // 5. 设置背景色 (这是关键！解耦时漏掉了这行，导致背景是系统默认色而非透明或皮肤色)
            BackColor = ThemeManager.ParseColor(ThemeManager.Current.Color.Background);

            // 2. 初始化双助手
            _winHelper = new MainFormWinHelper(this);
            
            //资源管理器重启监听
            _wmTaskbarCreated = MainFormWinHelper.RegisterTaskbarCreatedMessage();

            // ★★★ 关键修复：补全 SetStyle 调用，开启透明支持 ★★★
            // 原始代码中这里调用了 SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            // 解耦时漏掉了这一行，导致背景无法透明，显示为黑色或系统默认色
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            _winHelper.InitializeStyle(_cfg.TopMost, _cfg.ClickThrough);

            // 分层窗口：初始完全透明，由渐入动画逐帧提高 Alpha
            _fadeAlpha = 0.0;

            _bizHelper = new MainFormBizHelper(this, _cfg, _ui, _winHelper);
            _bizHelper.Initialize();

            // === 渐入透明度 ===
            _winHelper.StartFadeIn();

            // 3. 事件绑定
            BindEvents();
        }

        private void BindEvents()
        {
            // 拖拽
            MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    _ui?.SetDragging(true);
                    _uiDragging = true;
                    _bizHelper.IsDragging = true;
                    _dragOffset = e.Location;
                }
            };
            MouseMove += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    if (Math.Abs(e.X - _dragOffset.X) + Math.Abs(e.Y - _dragOffset.Y) < 1) return;
                    Location = new Point(Left + e.X - _dragOffset.X, Top + e.Y - _dragOffset.Y);
                }
            };
            MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    _ui?.SetDragging(false);
                    _uiDragging = false;
                    _bizHelper.IsDragging = false;
                    _bizHelper.ClampToScreen(); 
                    _bizHelper.SavePos();
                }
            };

            // 双击
            this.DoubleClick += (_, __) => _bizHelper.HandleDoubleClick();
            
            // DPI / Resize
            this.Resize += (_, __) => _winHelper.ApplyRoundedCorners();
            this.VisibleChanged += (_, __) => { if (Visible) { _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert: true); RenderNow(); } };
            // 句柄重建会丢失分层窗口内容，必须立即重绘
            this.HandleCreated += (_, __) => { _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert: true); RenderNow(); };
        }

        public void ShowMainWindow()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
            _cfg.HideMainForm = false;
            _cfg.Save();

            _bizHelper.ForceShow();
            _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert: true);
            _bizHelper.RebuildMenus();
        }

        public void HideMainWindow()
        {
            this.Hide();
            _cfg.HideMainForm = true;
            _cfg.Save();
            _bizHelper.RebuildMenus();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == _wmTaskbarCreated && _wmTaskbarCreated != 0)
            {
                // [Fix] Explorer 重启后，子窗口 TaskbarForm 会被销毁或失效。
                // 必须由主窗口感知并重建 TaskbarForm，才能恢复显示。
                if (_cfg.ShowTaskbar)
                {
                    // 延迟执行，确保 Explorer 初始化基本完成，同时避免阻塞消息泵
                    this.BeginInvoke(new Action(async () =>
                    {
                        try 
                        {
                            await Task.Delay(3000); // 等待3秒让Explorer缓口气
                            ToggleTaskbar(false);   // 彻底销毁旧实例
                            ToggleTaskbar(true);    // 创建新实例(新实例自带重试机制)
                        }
                        catch { }
                    }));
                }
            }

            if (m.Msg == WM_DISPLAYCHANGE)
            {
                // [Fix #288] 分辨率改变后，延迟执行位置恢复，确保 Screen.AllScreens 已完全更新
                // 增加防抖机制，避免短时间内多次触发
                _displayChangeCts?.Cancel();
                _displayChangeCts = new System.Threading.CancellationTokenSource();
                var token = _displayChangeCts.Token;

                Task.Delay(500, token).ContinueWith(t => 
                {
                    if (!t.IsCanceled)
                    {
                        this.BeginInvoke(new Action(() => _bizHelper?.RestorePos()));
                    }
                });
            }

            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // 分层窗口的内容由 UpdateLayeredWindow 提供，这里不再使用 e.Graphics
            RenderLayered();
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        /// <summary>
        /// 由渐入动画调用，设置整体淡入系数并重绘
        /// </summary>
        public void SetFadeAlpha(double alpha)
        {
            _fadeAlpha = Math.Clamp(alpha, 0.0, 1.0);
            RenderNow();
        }

        /// <summary>
        /// 透明度设置变更后立即重绘（背景/文字均由渲染层读取配置）
        /// </summary>
        public void RefreshAppearance() => RenderNow();

        /// <summary>
        /// 立即渲染一帧。
        /// UpdateLayeredWindow 分层窗口不会再收到 WM_PAINT，因此所有刷新都必须直接驱动渲染，
        /// 不能依赖 Invalidate()/Update()。
        /// </summary>
        public void RenderNow() => RenderLayered();

        /// <summary>
        /// 渲染到 32bpp 位图并通过 UpdateLayeredWindow 提交，实现文字与背景独立透明度
        /// </summary>
        private void RenderLayered()
        {
            if (_ui == null || _winHelper == null || IsDisposed || !IsHandleCreated) return;

            // 布局可能改变窗口尺寸，必须在创建位图前完成
            var size = _ui.EnsureLayout();
            if (size.Width <= 0 || size.Height <= 0) return;

            // 字体只按 UIScale 缩放，DPI 缩放由绘制表面承担，
            // 因此位图必须使用窗体当前 DPI，否则高 DPI 下文字会偏小、与布局错位。
            float dpi = DeviceDpi > 0 ? DeviceDpi : 96f;

            if (_renderBitmap == null || _renderBitmap.Size != size || Math.Abs(_renderBitmapDpi - dpi) > 0.1f)
            {
                _renderBitmap?.Dispose();
                // PArgb：像素为预乘 Alpha，符合 UpdateLayeredWindow(AC_SRC_ALPHA) 的要求
                _renderBitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                _renderBitmap.SetResolution(dpi, dpi);
                _renderBitmapDpi = dpi;
            }

            using (var g = Graphics.FromImage(_renderBitmap))
            {
                g.Clear(Color.Transparent);
                // 透明表面上使用 GDI+ 抗锯齿，避免 ClearType 产生有色边缘
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                _ui.Render(g, _fadeAlpha);
            }

            _winHelper.UpdateLayeredWindow(_renderBitmap);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            _ui?.ApplyTheme(_cfg.Skin);
            _winHelper.ApplyRoundedCorners();
            RenderNow();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            
            // 恢复可见性
            if (_cfg.HideMainForm) this.Hide();
            
            // 分层窗口没有 WM_PAINT，用直接渲染代替 Update()
            RenderNow();
            
            // 恢复位置
            _bizHelper.RestorePos();

            // 确保渲染尺寸正确 (横屏模式)
            if (_cfg.HorizontalMode && _ui != null)
            {
                this.Size = new Size(this.Width, this.Height);
            }
            
            // 移除了 StartFadeIn 调用，因为它已经还原回构造函数了
            _winHelper.ApplyRoundedCorners();
            _bizHelper.KeepVisible(3.0); // 启动保护期

            if (_cfg.ShowTaskbar) ToggleTaskbar(true);

            // 启动 WebServer
            if (_cfg.WebServerEnabled)
            {
                if (src.WebServer.LiteWebServer.Instance?.Start(out string err) == false)
                {
                     ShowNotification("WebServer Error", 
                         (_cfg.Language == "zh" ? "Web服务启动失败: " : "Web Server Failed: ") + err, 
                         ToolTipIcon.Error);
                }
            }
            // 这样既检查了驱动，也检查了更新，以及置顶 透明度 穿透 等，而且时机完美（窗口显示后）
            if (_bizHelper != null)
            {
                 _ = _bizHelper.RunStartupChecksAsync();
            }
            // [Fix] 强制置顶刷新，增加重试机制确保在某些系统环境下依然生效
            if (_cfg.TopMost)
            {
                this.BeginInvoke(new Action(async () =>
                {
                    await Task.Delay(3000);
                    _winHelper.RefreshTopMost(true, forceReinsert: true);
                }));
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _cfg.Save(); 
            TrafficLogger.Save(); 
            HardwareHistoryLogger.Save();
            src.WebServer.LiteWebServer.Instance?.Stop();
            
            base.OnFormClosed(e);
            
            _ui?.Dispose();
            _bizHelper.Dispose();

            _renderBitmap?.Dispose();
            _renderBitmap = null;
        }
    }
}
