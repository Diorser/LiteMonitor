using System;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.UI;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.WebServer; // ★★★ 引用 WebServer 命名空间 ★★★
using LiteMonitor.src.Plugins;
using System.Linq;
using System.Collections.Generic;

namespace LiteMonitor.src.Core.Actions
{
    /// <summary>
    /// 全局动作执行器
    /// 封装所有“修改配置后需要立即生效”的业务逻辑
    /// 供 MenuManager (右键菜单) 和 SettingsForm (设置中心) 共同调用
    /// </summary>
    public static class AppActions
    {

        // ★★★ 新增：全局应用入口 ★★★
        public static void ApplyAllSettings(Settings cfg, MainForm mainForm, UIController ui, bool applyAutoStart = false)
        {
            // 0. [核心修复] 在任何逻辑执行前，记录当前的渲染模式
            // 防止 ApplyLanguage -> ApplyTheme 提前重置了 ui 的状态导致无法判断是否切换了模式
            bool wasHorizontal = ui.IsLayoutHorizontal;

            // 1. 语言变更 (注意：传 null 给 ui，防止它在此提前触发 ApplyTheme)
            ApplyLanguage(cfg, null, mainForm); 

            // 2. 系统级设置
            // 开机启动会调用 schtasks 写计划任务，只能在开关变化时执行，避免每次应用设置都触发杀软拦截。
            if (applyAutoStart) ApplyAutoStart(cfg);
            ApplyWindowAttributes(cfg, mainForm); // 基础窗口属性

            // 3. 界面布局与主题 (传入原始状态以判断是否需要居中)
            ApplyThemeAndLayout(cfg, ui, mainForm, wasHorizontal); 
            
            // 4. 子模块特定设置
            ApplyMonitorLayout(ui, mainForm); // 监控项、硬件源变更
            ApplyTaskbarStyle(cfg, ui);       // 任务栏样式

            // ★★★ 5. [新增] 应用网页服务设置 (重启服务以应用端口变更) ★★★
            ApplyWebServer(cfg);

            // 6. 应用插件设置 (重载实例并清除缓存)
            PluginManager.Instance.Reload(cfg);

            // 7. 可见性 (最后执行，避免闪烁)
            ApplyVisibility(cfg, mainForm);
        }

        
        // =============================================================
        // 1. 核心系统动作 (语言、开机自启)
        // =============================================================

        public static void ApplyLanguage(Settings cfg, UIController? ui, MainForm form)
        {
            // 1. 加载语言资源
            LanguageManager.Load(cfg.Language);
            
            // 2. 同步自定义名称 (防止语言包覆盖了用户的自定义重命名)
            cfg.SyncToLanguage();

            // 3. 刷新主题（这也同时刷新了字体、布局计算、Timer间隔等）
            ui?.ApplyTheme(cfg.Skin);
            
            // 4. 重建右键菜单（更新文字）
            form.RebuildMenus();
            
            // 5. 刷新任务栏窗口（如果有）
            ReloadTaskbarWindows();
        }

        public static void ApplyAutoStart(Settings cfg)
        {
            AutoStart.Set(cfg.AutoStart);
        }

        // =============================================================
        // 2. 窗口行为与属性 (置顶、穿透、自动隐藏、透明度)
        // =============================================================

        public static void ApplyWindowAttributes(Settings cfg, MainForm form)
        {
            // 置顶
            if (form.TopMost != cfg.TopMost) form.TopMost = cfg.TopMost;
            form.RefreshTopMost(forceReinsert: true);
            
            // 鼠标穿透
            form.SetClickThrough(cfg.ClickThrough);
            
            // 自动隐藏 (需要启动或停止 Timer)
            if (cfg.AutoHide) form.InitAutoHideTimer();
            else form.StopAutoHideTimer();

            // 透明度（背景与文字独立，由渲染层直接读取配置，只需重绘）
            form.RefreshAppearance();

            // 5. 刷新菜单 (确保透明度、置顶等勾选状态同步更新)
            form.RebuildMenus();
        }

        // =============================================================
        // 3. 窗口可见性管理 (主界面、托盘、任务栏) - 含防呆
        // =============================================================

        public static void ApplyVisibility(Settings cfg, MainForm form)
        {
            // --- 防呆逻辑 ---
            // 检查三者（任务栏显示、隐藏界面、托盘图标）是否至少保留一个
            if (!cfg.ShowTaskbar && cfg.HideMainForm && cfg.HideTrayIcon)
            {
                // 如果全关了，强制打开托盘图标
                cfg.HideTrayIcon = false; 
                // (注意：配置值的持久化Save由调用方负责，或者在这里Save也可以，但为了逻辑分离通常由调用方Save)
            }

            // --- 执行动作 ---
            
            // 1. 托盘
            if (cfg.HideTrayIcon) form.HideTrayIcon();
            else form.ShowTrayIcon();

            // 2. 主窗口
            // HideMainForm = true 意味着我们要执行 Hide()
            if (cfg.HideMainForm) form.Hide();
            else form.Show();

            // 3. 任务栏窗口
            form.ToggleTaskbar(cfg.ShowTaskbar);
            
            // 4. 刷新菜单
            // 因为可见性改变可能影响菜单项的勾选状态（尤其是防呆逻辑修正后），也可能影响“任务栏显示”等选项的状态
            form.RebuildMenus(); 
        }

        // =============================================================
        // 4. 外观与布局 (主题、缩放、宽度、刷新率、显示模式)
        // =============================================================

        public static void ApplyThemeAndLayout(Settings cfg, UIController? ui, MainForm form, bool? wasHorizontal = null, bool retainData = false)
        {
            // 1. 确定是否发生了模式切换
            // 如果外部没传 wasHorizontal，则尝试从 ui 获取当前渲染状态
            bool oldMode = wasHorizontal ?? (ui?.IsLayoutHorizontal ?? cfg.HorizontalMode);
            bool modeChanged = (oldMode != cfg.HorizontalMode);

            Point? center = null;
            if (modeChanged && form.Visible && form.WindowState == FormWindowState.Normal)
            {
                center = new Point(form.Left + form.Width / 2, form.Top + form.Height / 2);
            }

            // 2. 应用主题
            ui?.ApplyTheme(cfg.Skin, retainData);
            
            // 3. 强制立即刷新布局以获得正确的新尺寸 (关键：否则 form.Width 还是旧的)
            //ui?.RebuildLayout();
            
            // 如果切换了横竖屏模式，菜单结构会变，需要重建
            form.RebuildMenus();
            ReloadTaskbarWindows();

            // 4. 执行居中重定位
            if (center.HasValue)
            {
                form.ApplyRoundedCorners();
                form.Location = new Point(center.Value.X - form.Width / 2, center.Value.Y - form.Height / 2);
                form.EnsureVisibleAndSavePos();
            }
        }

        // =============================================================
        // 5. 数据源与监控项 (磁盘/网络源、监控开关)
        // =============================================================

        public static void ApplyMonitorLayout(UIController? ui, MainForm form, bool rebuildMenus = true)
        {
            // ★★★ [新增] 动态检查硬件开启需求 (热切换) ★★★
            // 如果用户开启了风扇/水泵，自动开启 USB 控制器；反之关闭
            HardwareMonitor.Instance?.RefreshHardwareConfig();

            // 重新计算哪些格子要显示 (主界面和任务栏的数据列都会重建)
            ui?.RebuildLayout();
            
            // 因为监控项变了（比如开启了GPU），菜单里的勾选状态也得变
            // 优化：在右键菜单直接操作时，无需重建菜单，避免冗余
            if (rebuildMenus)
            {
                form.RebuildMenus();
            }
            
            // 任务栏窗口的内容也取决于监控项配置，必须刷新
            ReloadTaskbarWindows();
        }

        // =============================================================
        // 6. 任务栏样式 (字体、对齐、紧凑模式)
        // =============================================================
        
        public static void ApplyTaskbarStyle(Settings cfg, UIController? ui)
        {
            // 1. 刷新所有任务栏窗口
            // 这一步会触发 TaskbarForm.ReloadLayout()，进而自动读取 cfg 中的新颜色和穿透设置
            ReloadTaskbarWindows();
            
            // 如果样式影响了主程序计算（极少情况），可解开下面注释
            ui?.ApplyTheme(cfg.Skin); 
        }

        // =============================================================
        // 7. 网页服务 (新增逻辑)
        // =============================================================
        public static void ApplyWebServer(Settings cfg)
        {
            var server = LiteWebServer.Instance;
            if (server != null)
            {
                bool shouldRun = cfg.WebServerEnabled;
                bool isRunning = server.IsRunning;
                int targetPort = cfg.WebServerPort;
                int currentPort = server.CurrentRunningPort;
                string targetPwd = cfg.WebServerPassword;
                string currentPwd = server.CurrentPassword;

                if (shouldRun)
                {
                    // 端口变更 或 密码变更 均触发重启
                    if (!isRunning || targetPort != currentPort || targetPwd != currentPwd)
                    {
                        server.Stop();
                        if (!server.Start(out string err))
                        {
                            System.Diagnostics.Debug.WriteLine("WebServer restart failed: " + err);
                        }
                    }
                }
                else
                {
                    // 如果需要关闭，且当前正在运行 -> 关闭
                    if (isRunning)
                    {
                        server.Stop();
                    }
                }
            }
        }

        // --- 内部辅助 ---
        private static void ReloadTaskbarWindows()
        {
            // [Fix] 使用 ToList() 创建副本，防止 ReloadLayout -> TooltipHelper -> Dispose 修改 OpenForms 集合导致枚举异常
            var targets = Application.OpenForms.OfType<TaskbarForm>().ToList();
            foreach (var tf in targets)
            {
                tf.ReloadLayout();
            }
        }
    }
}
