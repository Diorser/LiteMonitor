using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Drawing;
using LiteMonitor.src.System;
using LiteMonitor.src.Core;
using System.Collections.Generic;

namespace LiteMonitor
{
    public static class MenuManager
    {
        // [已删除] EnsureAtLeastOneVisible 方法已移入 src/Core/AppActions.cs 的 ApplyVisibility 中

        /// <summary>
        /// 构建 LiteMonitor 主菜单（右键菜单 + 托盘菜单）
        /// </summary>
        public static ContextMenuStrip Build(MainForm form, Settings cfg, UIController? ui)
        {
            var menu = new ContextMenuStrip();

            // ==================================================================================
            // 1. 基础功能区 (置顶、显示模式、任务栏开关、隐藏主界面/托盘)
            // ==================================================================================

            // =================================================================
            // [新增] 设置中心入口
            // =================================================================
            var itemSettings = new ToolStripMenuItem("设置中心"); 
            // 临时写死中文，等面板做完善了再换成 LanguageManager.T("Menu.Settings")
            
            itemSettings.Font = new Font(itemSettings.Font, FontStyle.Bold); 

            itemSettings.Click += (_, __) =>
            {
                try
                {
                    // 打开设置窗口
                    using (var f = new LiteMonitor.src.UI.SettingsForm(cfg, ui, form))
                    {
                        f.ShowDialog(form);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("设置面板启动失败: " + ex.Message);
                }
            };
            menu.Items.Add(itemSettings);
            
            menu.Items.Add(new ToolStripSeparator());
            // =================================================================

            // === 置顶 ===
            var topMost = new ToolStripMenuItem(LanguageManager.T("Menu.TopMost"))
            {
                Checked = cfg.TopMost,
                CheckOnClick = true
            };
            topMost.CheckedChanged += (_, __) =>
            {
                cfg.TopMost = topMost.Checked;
                cfg.Save();
                // ★ 统一调用
                AppActions.ApplyWindowAttributes(cfg, form);
            };
            menu.Items.Add(topMost);
            menu.Items.Add(new ToolStripSeparator());

            // === 显示模式 ===
            var modeRoot = new ToolStripMenuItem(LanguageManager.T("Menu.DisplayMode"));

            var vertical = new ToolStripMenuItem(LanguageManager.T("Menu.Vertical"))
            {
                Checked = !cfg.HorizontalMode
            };
            var horizontal = new ToolStripMenuItem(LanguageManager.T("Menu.Horizontal"))
            {
                Checked = cfg.HorizontalMode
            };

            // 辅助点击事件
            void SetMode(bool isHorizontal)
            {
                cfg.HorizontalMode = isHorizontal;
                cfg.Save();
                // ★ 统一调用 (含主题、布局刷新)
                AppActions.ApplyThemeAndLayout(cfg, ui, form);
            }

            vertical.Click += (_, __) => SetMode(false);
            horizontal.Click += (_, __) => SetMode(true);

            modeRoot.DropDownItems.Add(vertical);
            modeRoot.DropDownItems.Add(horizontal);
            modeRoot.DropDownItems.Add(new ToolStripSeparator());

            // === 任务栏显示 ===
            var taskbarMode = new ToolStripMenuItem(LanguageManager.T("Menu.TaskbarShow"))
            {
                Checked = cfg.ShowTaskbar
            };

            taskbarMode.Click += (_, __) =>
            {
                cfg.ShowTaskbar = !cfg.ShowTaskbar;
                // 保存
                cfg.Save(); 
                // ★ 统一调用 (含防呆检查、显隐逻辑、菜单刷新)
                AppActions.ApplyVisibility(cfg, form);
            };

            modeRoot.DropDownItems.Add(taskbarMode);
            menu.Items.Add(modeRoot);


            // === 隐藏托盘图标 ===
            var hideTrayIcon = new ToolStripMenuItem(LanguageManager.T("Menu.HideTrayIcon"))
            {
                Checked = cfg.HideTrayIcon,
                CheckOnClick = true
            };

            hideTrayIcon.CheckedChanged += (_, __) =>
            {
                // 注意：旧的 CheckIfAllowHide 逻辑已整合进 AppActions.ApplyVisibility 的防呆检查中
                // 这里只需修改配置并调用 Action 即可
                
                cfg.HideTrayIcon = hideTrayIcon.Checked;
                cfg.Save();
                // ★ 统一调用
                AppActions.ApplyVisibility(cfg, form);
            };

            modeRoot.DropDownItems.Add(new ToolStripSeparator());
            modeRoot.DropDownItems.Add(hideTrayIcon);


            // === 隐藏主窗口 ===
            var hideMainForm = new ToolStripMenuItem(LanguageManager.T("Menu.HideMainForm"))
            {
                Checked = cfg.HideMainForm,
                CheckOnClick = true
            };

            hideMainForm.CheckedChanged += (_, __) =>
            {
                cfg.HideMainForm = hideMainForm.Checked;
                cfg.Save();
                // ★ 统一调用
                AppActions.ApplyVisibility(cfg, form);
            };

            modeRoot.DropDownItems.Add(new ToolStripSeparator());
            modeRoot.DropDownItems.Add(hideMainForm);

            menu.Items.Add(new ToolStripSeparator());


            // ==================================================================================
            // 2. 显示监控项 (动态生成)
            // ==================================================================================

            var grpShow = new ToolStripMenuItem(LanguageManager.T("Menu.ShowItems"));
            menu.Items.Add(grpShow);

            // --- 内部辅助函数：最大值引导提示 (保留 UI 交互逻辑) ---
            void CheckAndRemind(string name)
            {
                if (cfg.MaxLimitTipShown) return;

                string msg = cfg.Language == "zh"
                    ? $"您是首次开启 {name}。\n\n建议设置一下电脑实际“最大{name}”，让进度条显示更准确。\n\n是否现在去设置？\n\n点“否”将不再提示，程序将在高负载时（如大型游戏时）进行动态学习最大值"
                    : $"You are enabling {name} for the first time.\n\nIt is recommended to set the actual 'Max {name}' for accurate progress bars.\n\nGo to settings now?\n\n(Select 'No' to suppress this prompt. The app will auto-learn the max value under high load.)";

                cfg.MaxLimitTipShown = true;
                cfg.Save();

                if (MessageBox.Show(msg, "LiteMonitor Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    var f = new ThresholdForm(cfg);
                    if (f.ShowDialog() == DialogResult.OK)
                    {
                        // 阈值设置完成后，也需要刷新布局
                        AppActions.ApplyMonitorLayout(ui, form);
                    }
                }
            }

            // --- 动态遍历 MonitorItems 列表 ---
            var sortedItems = cfg.MonitorItems.OrderBy(x => x.SortIndex).ToList();
            var itemGroups = sortedItems.GroupBy(x => x.Key.Split('.')[0]);

            foreach (var group in itemGroups)
            {
                var groupMenu = new ToolStripMenuItem(group.Key);

                foreach (var itemConfig in group)
                {
                    string label = !string.IsNullOrEmpty(itemConfig.UserLabel)
                        ? itemConfig.UserLabel
                        : LanguageManager.T("Items." + itemConfig.Key);
                    if (string.IsNullOrEmpty(label)) label = itemConfig.Key;

                    var menuItem = new ToolStripMenuItem(label)
                    {
                        Checked = itemConfig.VisibleInPanel,
                        CheckOnClick = true
                    };

                    menuItem.CheckedChanged += (_, __) =>
                    {
                        // 1. 修改配置
                        itemConfig.VisibleInPanel = menuItem.Checked;
                        cfg.Save();

                        // 2. ★ 统一调用 (刷新主界面、任务栏、菜单)
                        AppActions.ApplyMonitorLayout(ui, form);

                        // 3. 检查是否需要弹窗提示 (保留 UI 逻辑)
                        if (menuItem.Checked)
                        {
                            if (itemConfig.Key.Contains("Clock") || itemConfig.Key.Contains("Power"))
                            {
                                CheckAndRemind(label);
                            }
                        }
                    };
                    groupMenu.DropDownItems.Add(menuItem);
                }
                grpShow.DropDownItems.Add(groupMenu);
            }

            menu.Items.Add(new ToolStripSeparator());


            // ==================================================================================
            // 3. 主题、工具与更多功能
            // ==================================================================================

            // === 主题 ===
            var themeRoot = new ToolStripMenuItem(LanguageManager.T("Menu.Theme"));
            foreach (var name in ThemeManager.GetAvailableThemes())
            {
                var item = new ToolStripMenuItem(name)
                {
                    Checked = name.Equals(cfg.Skin, StringComparison.OrdinalIgnoreCase)
                };

                item.Click += (_, __) =>
                {
                    cfg.Skin = name;
                    cfg.Save();
                    // ★ 统一调用
                    AppActions.ApplyThemeAndLayout(cfg, ui, form);
                };
                themeRoot.DropDownItems.Add(item);
            }
            menu.Items.Add(themeRoot);
            menu.Items.Add(new ToolStripSeparator());

            // 网络测速 (独立窗口，保持原样)
            var speedWindow = new ToolStripMenuItem(LanguageManager.T("Menu.Speedtest"));
            speedWindow.Image = Properties.Resources.NetworkIcon;
            speedWindow.Click += (_, __) =>
            {
                var f = new SpeedTestForm();
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(form.Left + 20, form.Top + 20);
                f.Show();
            };
            menu.Items.Add(speedWindow);


            // 历史流量统计 (独立窗口，保持原样)
            var trafficItem = new ToolStripMenuItem(LanguageManager.T("Menu.Traffic"));
            trafficItem.Image = Properties.Resources.TrafficIcon;
            trafficItem.Click += (_, __) =>
            {
                var formHistory = new TrafficHistoryForm(cfg);
                formHistory.Show();
            };
            menu.Items.Add(trafficItem);

            // === 更多功能 ===
            var moreRoot = new ToolStripMenuItem(LanguageManager.T("Menu.More"));
            moreRoot.Image = Properties.Resources.MoreIcon;
            menu.Items.Add(moreRoot);

            // 主题编辑器 (独立窗口，保持原样)
            var themeEditor = new ToolStripMenuItem(LanguageManager.T("Menu.ThemeEditor"));
            themeEditor.Image = Properties.Resources.ThemeIcon;
            themeEditor.Click += (_, __) => new ThemeEditor.ThemeEditorForm().Show();
            moreRoot.DropDownItems.Add(themeEditor);
            moreRoot.DropDownItems.Add(new ToolStripSeparator());

            // 阈值设置 (独立窗口，回调刷新)
            var thresholdItem = new ToolStripMenuItem(LanguageManager.T("Menu.Thresholds"));
            thresholdItem.Image = Properties.Resources.Threshold;
            thresholdItem.Click += (_, __) =>
            {
                var f = new ThresholdForm(cfg);
                if (f.ShowDialog() == DialogResult.OK)
                {
                    // ★ 统一调用
                    AppActions.ApplyMonitorLayout(ui, form);
                }
            };
            moreRoot.DropDownItems.Add(thresholdItem);
            moreRoot.DropDownItems.Add(new ToolStripSeparator());

            // ================= 任务栏设置 =================
            string strTaskbar = LanguageManager.T("Menu.TaskbarSettings");
            var taskbarMenu = new ToolStripMenuItem(strTaskbar);

            // 1. 简洁显示
            bool isCompact = (Math.Abs(cfg.TaskbarFontSize - 9f) < 0.1f) && !cfg.TaskbarFontBold;
            string strCompact = LanguageManager.T("Menu.TaskbarStyleRegular");
            var itemCompact = new ToolStripMenuItem(strCompact)
            {
                Checked = isCompact,
                CheckOnClick = true
            };
            itemCompact.Click += (s, e) => {
                if (itemCompact.Checked)
                {
                    cfg.TaskbarFontSize = 9f;
                    cfg.TaskbarFontBold = false;
                }
                else
                {
                    cfg.TaskbarFontSize = 10f;
                    cfg.TaskbarFontBold = true;
                }
                cfg.Save();
                // ★ 统一调用
                AppActions.ApplyTaskbarStyle(cfg, ui);
            };
            taskbarMenu.DropDownItems.Add(itemCompact);

            // 2. 对齐方向 (仅 Win11 居中时显示)
            if (TaskbarForm.IsCenterAligned())
            {
                taskbarMenu.DropDownItems.Add(new ToolStripSeparator());

                string strAlign = LanguageManager.T("Menu.TaskbarAlign");
                var itemAlign = new ToolStripMenuItem(strAlign);
                string strRight = LanguageManager.T("Menu.TaskbarAlignRight");
                string strLeft = LanguageManager.T("Menu.TaskbarAlignLeft");
                var menuRight = new ToolStripMenuItem(strRight) { Checked = !cfg.TaskbarAlignLeft, CheckOnClick = true };
                var menuLeft = new ToolStripMenuItem(strLeft) { Checked = cfg.TaskbarAlignLeft, CheckOnClick = true };

                menuRight.Click += (s, e) => {
                    cfg.TaskbarAlignLeft = false; cfg.Save();
                    menuRight.Checked = true; menuLeft.Checked = false;
                    AppActions.ApplyTaskbarStyle(cfg, ui);
                };
                menuLeft.Click += (s, e) => {
                    cfg.TaskbarAlignLeft = true; cfg.Save();
                    menuRight.Checked = false; menuLeft.Checked = true;
                    AppActions.ApplyTaskbarStyle(cfg, ui);
                };
                itemAlign.DropDownItems.Add(menuRight);
                itemAlign.DropDownItems.Add(menuLeft);
                taskbarMenu.DropDownItems.Add(itemAlign);
            }
            else
            {
                if (cfg.TaskbarAlignLeft) { cfg.TaskbarAlignLeft = false; cfg.Save(); }
            }

            moreRoot.DropDownItems.Add(taskbarMenu);
            moreRoot.DropDownItems.Add(new ToolStripSeparator());


            // === 高温报警 (纯数据开关，不需要 AppActions) ===
            var alertItem = new ToolStripMenuItem(LanguageManager.T("Menu.AlertTemp") + " (>" + cfg.AlertTempThreshold + "°C)")
            {
                Checked = cfg.AlertTempEnabled,
                CheckOnClick = true
            };
            alertItem.CheckedChanged += (_, __) =>
            {
                cfg.AlertTempEnabled = alertItem.Checked;
                cfg.Save();
            };
            moreRoot.DropDownItems.Add(alertItem);


            // === 自动隐藏 ===
            var autoHide = new ToolStripMenuItem(LanguageManager.T("Menu.AutoHide"))
            {
                Checked = cfg.AutoHide,
                CheckOnClick = true
            };
            autoHide.CheckedChanged += (_, __) =>
            {
                cfg.AutoHide = autoHide.Checked;
                cfg.Save();
                // ★ 统一调用
                AppActions.ApplyWindowAttributes(cfg, form);
            };
            moreRoot.DropDownItems.Add(autoHide);

            // === 限制窗口拖出屏幕 (纯数据开关) ===
            var clampItem = new ToolStripMenuItem(LanguageManager.T("Menu.ClampToScreen"))
            {
                Checked = cfg.ClampToScreen,
                CheckOnClick = true
            };
            clampItem.CheckedChanged += (_, __) =>
            {
                cfg.ClampToScreen = clampItem.Checked;
                cfg.Save();
            };
            moreRoot.DropDownItems.Add(clampItem);

            // === 鼠标穿透 ===
            var clickThrough = new ToolStripMenuItem(LanguageManager.T("Menu.ClickThrough"))
            {
                Checked = cfg.ClickThrough,
                CheckOnClick = true
            };
            clickThrough.CheckedChanged += (_, __) =>
            {
                cfg.ClickThrough = clickThrough.Checked;
                cfg.Save();
                // ★ 统一调用
                AppActions.ApplyWindowAttributes(cfg, form);
            };
            moreRoot.DropDownItems.Add(clickThrough);

            moreRoot.DropDownItems.Add(new ToolStripSeparator());

            // === 刷新频率 ===
            var refreshRoot = new ToolStripMenuItem(LanguageManager.T("Menu.Refresh"));
            int[] presetRefresh = { 100, 200, 300, 500, 600, 700, 800, 1000, 1500, 2000, 3000 };

            foreach (var ms in presetRefresh)
            {
                var item = new ToolStripMenuItem($"{ms} ms")
                {
                    Checked = cfg.RefreshMs == ms
                };

                item.Click += (_, __) =>
                {
                    cfg.RefreshMs = ms;
                    cfg.Save();
                    // ★ 统一调用
                    AppActions.ApplyThemeAndLayout(cfg, ui, form);
                };
                refreshRoot.DropDownItems.Add(item);
            }

            moreRoot.DropDownItems.Add(refreshRoot);
            moreRoot.DropDownItems.Add(new ToolStripSeparator());

            // === 透明度 ===
            var opacityRoot = new ToolStripMenuItem(LanguageManager.T("Menu.Opacity"));
            double[] presetOps = { 1.0, 0.95, 0.9, 0.85, 0.8, 0.75, 0.7, 0.6, 0.5, 0.4, 0.3 };
            foreach (var val in presetOps)
            {
                var item = new ToolStripMenuItem($"{val * 100:0}%")
                {
                    Checked = Math.Abs(cfg.Opacity - val) < 0.01
                };

                item.Click += (_, __) =>
                {
                    cfg.Opacity = val;
                    cfg.Save();
                    // ★ 统一调用
                    AppActions.ApplyWindowAttributes(cfg, form);
                };
                opacityRoot.DropDownItems.Add(item);
            }
            moreRoot.DropDownItems.Add(opacityRoot);

            // === 界面宽度 ===
            var widthRoot = new ToolStripMenuItem(LanguageManager.T("Menu.Width"));
            int[] presetWidths = { 180, 200, 220, 240, 260, 280, 300, 360, 420, 480, 540, 600, 660, 720, 780, 840, 900, 960, 1020, 1080, 1140, 1200 };
            int currentW = cfg.PanelWidth;

            foreach (var w in presetWidths)
            {
                var item = new ToolStripMenuItem($"{w}px")
                {
                    Checked = Math.Abs(currentW - w) < 1
                };
                item.Click += (_, __) =>
                {
                    cfg.PanelWidth = w;
                    cfg.Save();
                    // ★ 统一调用
                    AppActions.ApplyThemeAndLayout(cfg, ui, form);
                };
                widthRoot.DropDownItems.Add(item);
            }
            moreRoot.DropDownItems.Add(widthRoot);

            // === 界面缩放 ===
            var scaleRoot = new ToolStripMenuItem(LanguageManager.T("Menu.Scale"));
            (double val, string key)[] presetScales =
            {
                (2.00, "200%"), (1.75, "175%"), (1.50, "150%"), (1.25, "125%"),
                (1.00, "100%"), (0.90, "90%"),  (0.85, "85%"),  (0.80, "80%"),
                (0.75, "75%"),  (0.70, "70%"),  (0.60, "60%"),  (0.50, "50%")
            };

            double currentScale = cfg.UIScale;
            foreach (var (scale, label) in presetScales)
            {
                var item = new ToolStripMenuItem(label)
                {
                    Checked = Math.Abs(currentScale - scale) < 0.01
                };

                item.Click += (_, __) =>
                {
                    cfg.UIScale = scale;
                    cfg.Save();
                    // ★ 统一调用
                    AppActions.ApplyThemeAndLayout(cfg, ui, form);
                };
                scaleRoot.DropDownItems.Add(item);
            }

            moreRoot.DropDownItems.Add(scaleRoot);
            moreRoot.DropDownItems.Add(new ToolStripSeparator());

            // === 磁盘来源 ===
            var diskRoot = new ToolStripMenuItem(LanguageManager.T("Menu.DiskSource"));
            var autoDisk = new ToolStripMenuItem(LanguageManager.T("Menu.Auto"))
            {
                Checked = string.IsNullOrWhiteSpace(cfg.PreferredDisk)
            };
            autoDisk.Click += (_, __) =>
            {
                cfg.PreferredDisk = "";
                cfg.Save();
                // ★ 统一调用
                AppActions.ApplyMonitorLayout(ui, form);
            };
            diskRoot.DropDownItems.Add(autoDisk);

            // 惰性加载
            diskRoot.DropDownOpening += (_, __) =>
            {
                autoDisk.Checked = string.IsNullOrWhiteSpace(cfg.PreferredDisk);
                while (diskRoot.DropDownItems.Count > 1) diskRoot.DropDownItems.RemoveAt(1);

                foreach (var name in HardwareMonitor.ListAllDisks())
                {
                    var item = new ToolStripMenuItem(name)
                    {
                        Checked = name == cfg.PreferredDisk
                    };
                    item.Click += (_, __) =>
                    {
                        cfg.PreferredDisk = name;
                        cfg.Save();
                        // ★ 统一调用
                        AppActions.ApplyMonitorLayout(ui, form);
                    };
                    diskRoot.DropDownItems.Add(item);
                }
            };
            moreRoot.DropDownItems.Add(diskRoot);

            // === 网络来源 ===
            var netRoot = new ToolStripMenuItem(LanguageManager.T("Menu.NetworkSource"));
            var autoNet = new ToolStripMenuItem(LanguageManager.T("Menu.Auto"))
            {
                Checked = string.IsNullOrWhiteSpace(cfg.PreferredNetwork)
            };
            autoNet.Click += (_, __) =>
            {
                cfg.PreferredNetwork = "";
                cfg.Save();
                // ★ 统一调用
                AppActions.ApplyMonitorLayout(ui, form);
            };
            netRoot.DropDownItems.Add(autoNet);

            // 惰性加载
            netRoot.DropDownOpening += (_, __) =>
            {
                autoNet.Checked = string.IsNullOrWhiteSpace(cfg.PreferredNetwork);
                while (netRoot.DropDownItems.Count > 1) netRoot.DropDownItems.RemoveAt(1);

                foreach (var name in HardwareMonitor.ListAllNetworks())
                {
                    var item = new ToolStripMenuItem(name)
                    {
                        Checked = name == cfg.PreferredNetwork
                    };
                    item.Click += (_, __) =>
                    {
                        cfg.PreferredNetwork = name;
                        cfg.Save();
                        // ★ 统一调用
                        AppActions.ApplyMonitorLayout(ui, form);
                    };
                    netRoot.DropDownItems.Add(item);
                }
            };
            moreRoot.DropDownItems.Add(netRoot);

            menu.Items.Add(new ToolStripSeparator());

            // === 语言切换 ===
            var langRoot = new ToolStripMenuItem(LanguageManager.T("Menu.Language"));
            string langDir = Path.Combine(AppContext.BaseDirectory, "resources/lang");

            if (Directory.Exists(langDir))
            {
                foreach (var file in Directory.EnumerateFiles(langDir, "*.json"))
                {
                    string code = Path.GetFileNameWithoutExtension(file);

                    var item = new ToolStripMenuItem(code.ToUpper())
                    {
                        Checked = cfg.Language.Equals(code, StringComparison.OrdinalIgnoreCase)
                    };

                    item.Click += (_, __) =>
                    {
                        cfg.Language = code;
                        cfg.Save();
                        // ★ 统一调用
                        AppActions.ApplyLanguage(cfg, ui, form);
                    };

                    langRoot.DropDownItems.Add(item);
                }
            }

            menu.Items.Add(langRoot);
            menu.Items.Add(new ToolStripSeparator());

            // === 开机启动 ===
            var autoStart = new ToolStripMenuItem(LanguageManager.T("Menu.AutoStart"))
            {
                Checked = cfg.AutoStart,
                CheckOnClick = true
            };
            autoStart.CheckedChanged += (_, __) =>
            {
                cfg.AutoStart = autoStart.Checked;
                cfg.Save();
                // ★ 统一调用
                AppActions.ApplyAutoStart(cfg);
            };
            menu.Items.Add(autoStart);

            // === 关于 ===
            var about = new ToolStripMenuItem(LanguageManager.T("Menu.About"));
            about.Click += (_, __) => new AboutForm().ShowDialog(form);
            menu.Items.Add(about);

            menu.Items.Add(new ToolStripSeparator());

            // === 退出 ===
            var exit = new ToolStripMenuItem(LanguageManager.T("Menu.Exit"));
            exit.Click += (_, __) => form.Close();
            menu.Items.Add(exit);

            return menu;
        }
    }
}