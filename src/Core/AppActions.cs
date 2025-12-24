using System;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.UI;
using LiteMonitor.src.System;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 全局动作执行器
    /// 封装所有“修改配置后需要立即生效”的业务逻辑
    /// 供 MenuManager (右键菜单) 和 SettingsForm (设置中心) 共同调用
    /// </summary>
    public static class AppActions
    {
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
            
            // 鼠标穿透
            form.SetClickThrough(cfg.ClickThrough);
            
            // 自动隐藏 (需要启动或停止 Timer)
            if (cfg.AutoHide) form.InitAutoHideTimer();
            else form.StopAutoHideTimer();

            // 透明度
            if (Math.Abs(form.Opacity - cfg.Opacity) > 0.01)
                form.Opacity = Math.Clamp(cfg.Opacity, 0.1, 1.0);
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

        public static void ApplyThemeAndLayout(Settings cfg, UIController? ui, MainForm form)
        {
            // ApplyTheme 内部已经处理了：
            // - Skin 解析
            // - UIScale 和 PanelWidth 的计算
            // - RefreshMs (重置 Timer)
            // - HorizontalMode (切换渲染器)
            ui?.ApplyTheme(cfg.Skin);
            
            // 如果切换了横竖屏模式，菜单结构会变，需要重建
            form.RebuildMenus();
            
            // 刷新任务栏窗口
            ReloadTaskbarWindows();
        }

        // =============================================================
        // 5. 数据源与监控项 (磁盘/网络源、监控开关)
        // =============================================================

        public static void ApplyMonitorLayout(UIController? ui, MainForm form)
        {
            // 重新计算哪些格子要显示 (主界面和任务栏的数据列都会重建)
            ui?.RebuildLayout();
            
            // 因为监控项变了（比如开启了GPU），菜单里的勾选状态也得变
            form.RebuildMenus(); 
            
            // 任务栏窗口的内容也取决于监控项配置，必须刷新
            ReloadTaskbarWindows();
        }

        // =============================================================
        // 6. 任务栏样式 (字体、对齐、紧凑模式)
        // =============================================================
        
        public static void ApplyTaskbarStyle(Settings cfg, UIController? ui)
        {
            // 任务栏样式改变通常不需要完全重载主题，只要刷新 TaskbarForm 即可
            // 某些样式（如字体大小计算）可能依赖 ApplyTheme，为了保险也可以调 ApplyTheme
            // 这里为了轻量化，只刷新任务栏窗口，若有问题可改为 ApplyThemeAndLayout
           
            ReloadTaskbarWindows();
            
            // 如果样式影响了主程序计算（极少情况），可解开下面注释
            ui?.ApplyTheme(cfg.Skin); 
        }

        // --- 内部辅助 ---
        private static void ReloadTaskbarWindows()
        {
            foreach (Form f in Application.OpenForms)
            {
                if (f is TaskbarForm tf) tf.ReloadLayout();
            }
        }
    }
}