using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Drawing;
using LiteMonitor.src.System;
using LiteMonitor.src.Core;
namespace LiteMonitor
{
    public static class MenuManager
    {
        /// <summary>
        /// 构建 LiteMonitor 主菜单（右键菜单 + 托盘菜单）
        /// </summary>
        public static ContextMenuStrip Build(MainForm form, Settings cfg, UIController? ui)
        {
            var menu = new ContextMenuStrip();

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
                form.TopMost = cfg.TopMost;
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

            vertical.Click += (_, __) =>
            {
                cfg.HorizontalMode = false;
                cfg.Save();
                ui?.ApplyTheme(cfg.Skin);
                form.RebuildMenus();
            };

            horizontal.Click += (_, __) =>
            {
                cfg.HorizontalMode = true;
                cfg.Save();
                ui?.ApplyTheme(cfg.Skin);
                form.RebuildMenus();
            };

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
                cfg.Save();

                // 控制任务栏窗口显示/关闭
                form.ToggleTaskbar(cfg.ShowTaskbar);

                // 更新菜单勾选状态
                taskbarMode.Checked = cfg.ShowTaskbar;
            };

            // 将任务栏模式加入显示模式分组
            modeRoot.DropDownItems.Add(taskbarMode);

            menu.Items.Add(modeRoot);


            // === 隐藏主窗口（===
            var hideMainForm = new ToolStripMenuItem(LanguageManager.T("Menu.HideMainForm"))
            {
                Checked = cfg.HideMainForm,
                CheckOnClick = true
            };

            hideMainForm.CheckedChanged += (_, __) =>
            {
                cfg.HideMainForm = hideMainForm.Checked;
                cfg.Save();

                // 立即生效的行为（当前这次运行要不要立刻隐藏）
                if (cfg.HideMainForm)
                {
                    // 只隐藏主窗口，不影响任务栏
                    form.Hide();; // 下面会加这个方法
                }
                else
                {
                    // 如果用户取消，立刻把主窗口叫出来
                    form.Show();
                }
            };

            modeRoot.DropDownItems.Add(new ToolStripSeparator());
            modeRoot.DropDownItems.Add(hideMainForm);

            
            menu.Items.Add(new ToolStripSeparator());


              // === 显示项 ===
            var grpShow = new ToolStripMenuItem(LanguageManager.T("Menu.ShowItems"));
            menu.Items.Add(grpShow);

            void AddToggle(string key, Func<bool> get, Action<bool> set)
            {
                var item = new ToolStripMenuItem(LanguageManager.T(key))
                {
                    Checked = get(),
                    CheckOnClick = true
                };
                item.CheckedChanged += (_, __) =>
                {
                    set(item.Checked);
                    cfg.Save();
                    ui?.ApplyTheme(cfg.Skin);
                };
                grpShow.DropDownItems.Add(item);
            }

            AddToggle("Items.CPU.Load", () => cfg.Enabled.CpuLoad, v => cfg.Enabled.CpuLoad = v);
            AddToggle("Items.CPU.Temp", () => cfg.Enabled.CpuTemp, v => cfg.Enabled.CpuTemp = v);
            AddToggle("Items.GPU.Load", () => cfg.Enabled.GpuLoad, v => cfg.Enabled.GpuLoad = v);
            AddToggle("Items.GPU.Temp", () => cfg.Enabled.GpuTemp, v => cfg.Enabled.GpuTemp = v);
            AddToggle("Items.GPU.VRAM", () => cfg.Enabled.GpuVram, v => cfg.Enabled.GpuVram = v);
            AddToggle("Items.MEM.Load", () => cfg.Enabled.MemLoad, v => cfg.Enabled.MemLoad = v);

            AddToggle("Groups.DISK",
                () => cfg.Enabled.DiskRead || cfg.Enabled.DiskWrite,
                v => { cfg.Enabled.DiskRead = v; cfg.Enabled.DiskWrite = v; });

            AddToggle("Groups.NET",
                () => cfg.Enabled.NetUp || cfg.Enabled.NetDown,
                v => { cfg.Enabled.NetUp = v; cfg.Enabled.NetDown = v; });


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
                    form.Opacity = Math.Clamp(val, 0.1, 1.0);

                    foreach (ToolStripMenuItem other in opacityRoot.DropDownItems)
                        other.Checked = false;

                    item.Checked = true;
                };
                opacityRoot.DropDownItems.Add(item);
            }
            menu.Items.Add(opacityRoot);


          


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

                    foreach (ToolStripMenuItem other in themeRoot.DropDownItems)
                        other.Checked = false;

                    item.Checked = true;

                    ui?.ApplyTheme(name);
                };

                themeRoot.DropDownItems.Add(item);
            }
            menu.Items.Add(themeRoot);
            menu.Items.Add(new ToolStripSeparator());


            // === 更多 ===
            var moreRoot = new ToolStripMenuItem(LanguageManager.T("Menu.More"));
            menu.Items.Add(moreRoot);

            // 主题编辑器
            var themeEditor = new ToolStripMenuItem(LanguageManager.T("Menu.ThemeEditor"));
            themeEditor.Click += (_, __) => new ThemeEditor.ThemeEditorForm().Show();
            moreRoot.DropDownItems.Add(themeEditor);
            moreRoot.DropDownItems.Add(new ToolStripSeparator());

           

            // 自动隐藏
            var autoHide = new ToolStripMenuItem(LanguageManager.T("Menu.AutoHide"))
            {
                Checked = cfg.AutoHide,
                CheckOnClick = true
            };
            autoHide.CheckedChanged += (_, __) =>
            {
                cfg.AutoHide = autoHide.Checked;
                cfg.Save();
                if (cfg.AutoHide) form.InitAutoHideTimer();
                else form.StopAutoHideTimer();
            };
            moreRoot.DropDownItems.Add(autoHide);

            // ★ 新增：限制窗口拖出屏幕
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

             // 鼠标穿透
            var clickThrough = new ToolStripMenuItem(LanguageManager.T("Menu.ClickThrough"))
            {
                Checked = cfg.ClickThrough,
                CheckOnClick = true
            };
            clickThrough.CheckedChanged += (_, __) =>
            {
                cfg.ClickThrough = clickThrough.Checked;
                cfg.Save();
                form.SetClickThrough(clickThrough.Checked);
            };
            moreRoot.DropDownItems.Add(clickThrough);

            moreRoot.DropDownItems.Add(new ToolStripSeparator());

            // === 刷新频率 ===
            var refreshRoot = new ToolStripMenuItem(LanguageManager.T("Menu.Refresh"));
            int[] presetRefresh = { 100, 200, 300, 500,600,700, 800, 1000, 1500, 2000,3000 };

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

                    // 立即应用新刷新时间（UIController 会自动在下次 Tick 使用）
                    ui?.ApplyTheme(cfg.Skin); // 触发 UI 重建 & Timer 重载

                    foreach (ToolStripMenuItem other in refreshRoot.DropDownItems)
                        other.Checked = false;

                    item.Checked = true;
                };

                refreshRoot.DropDownItems.Add(item);
            }

            moreRoot.DropDownItems.Add(refreshRoot);
            moreRoot.DropDownItems.Add(new ToolStripSeparator());


            // 界面宽度
            var widthRoot = new ToolStripMenuItem(LanguageManager.T("Menu.Width"));
            int[] presetWidths = { 180, 200, 220, 240, 260, 280, 300, 360, 420 };
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
                    ui?.ApplyTheme(cfg.Skin);

                    foreach (ToolStripMenuItem other in widthRoot.DropDownItems)
                        other.Checked = false;

                    item.Checked = true;
                };

                widthRoot.DropDownItems.Add(item);
            }
            moreRoot.DropDownItems.Add(widthRoot);

            // 界面缩放
            var scaleRoot = new ToolStripMenuItem(LanguageManager.T("Menu.Scale"));
            (double val, string key)[] presetScales =
            {
                (2.00, "200%"),
                (1.75, "175%"),
                (1.50, "150%"),
                (1.25, "125%"),
                (1.00, "100%"),
                (0.90, "90%"),
                (0.85, "85%"),
                (0.80, "80%"),
                (0.75, "75%"),
                (0.70, "70%"),
                (0.60, "60%"),
                (0.50, "50%")
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

                    ui?.ApplyTheme(cfg.Skin);

                    foreach (ToolStripMenuItem other in scaleRoot.DropDownItems)
                        other.Checked = false;

                    item.Checked = true;
                };

                scaleRoot.DropDownItems.Add(item);
            }

            moreRoot.DropDownItems.Add(scaleRoot);
            
            moreRoot.DropDownItems.Add(new ToolStripSeparator());





            // === 磁盘来源 ===
            var diskRoot = new ToolStripMenuItem(LanguageManager.T("Menu.DiskSource"));

            // 自动项
            var autoDisk = new ToolStripMenuItem(LanguageManager.T("Menu.Auto"))
            {
                Checked = string.IsNullOrWhiteSpace(cfg.PreferredDisk)
            };

            autoDisk.Click += (_, __) =>
            {
                cfg.PreferredDisk = "";
                cfg.Save();
                ui?.RebuildLayout();
            };

            diskRoot.DropDownItems.Add(autoDisk);

            // === 惰性加载 ===
            diskRoot.DropDownOpening += (_, __) =>
            {
                // 每次打开都同步自动项的勾选状态
                autoDisk.Checked = string.IsNullOrWhiteSpace(cfg.PreferredDisk);

                while (diskRoot.DropDownItems.Count > 1)
                    diskRoot.DropDownItems.RemoveAt(1);

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
                        ui?.RebuildLayout();
                    };

                    diskRoot.DropDownItems.Add(item);
                }
            };

            moreRoot.DropDownItems.Add(diskRoot);




            // === 网络来源 ===
            var netRoot = new ToolStripMenuItem(LanguageManager.T("Menu.NetworkSource"));

            // 自动项
            var autoNet = new ToolStripMenuItem(LanguageManager.T("Menu.Auto"))
            {
                Checked = string.IsNullOrWhiteSpace(cfg.PreferredNetwork)
            };

            autoNet.Click += (_, __) =>
            {
                cfg.PreferredNetwork = "";
                cfg.Save();
                ui?.RebuildLayout();
            };

            netRoot.DropDownItems.Add(autoNet);

            // === 惰性加载 ===
            netRoot.DropDownOpening += (_, __) =>
            {
                // 每次打开都同步自动项的勾选状态
                autoNet.Checked = string.IsNullOrWhiteSpace(cfg.PreferredNetwork);

                // 清理之前的（自动项保留）
                while (netRoot.DropDownItems.Count > 1)
                    netRoot.DropDownItems.RemoveAt(1);

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
                        ui?.RebuildLayout();
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

                        ui?.ApplyTheme(cfg.Skin);

                        // 让 MainForm 来重建菜单（最优雅）
                        form.RebuildMenus();
                    };

                    langRoot.DropDownItems.Add(item);
                }
            }

            menu.Items.Add(langRoot);
            menu.Items.Add(new ToolStripSeparator());


            // === 自启动 ===
            var autoStart = new ToolStripMenuItem(LanguageManager.T("Menu.AutoStart"))
            {
                Checked = cfg.AutoStart,
                CheckOnClick = true
            };
            autoStart.CheckedChanged += (_, __) =>
            {
                cfg.AutoStart = autoStart.Checked;
                cfg.Save();
                AutoStart.Set(cfg.AutoStart);
            };
            menu.Items.Add(autoStart);
            menu.Items.Add(new ToolStripSeparator());


            // === 检查更新 ===
            var checkUpdate = new ToolStripMenuItem(LanguageManager.T("Menu.CheckUpdate"));
            checkUpdate.Click += async (_, __) => await UpdateChecker.CheckAsync(showMessage: true);
            menu.Items.Add(checkUpdate);

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
