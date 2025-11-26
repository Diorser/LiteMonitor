using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Drawing.Text;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>
    /// LiteMonitor 主题编辑器主窗口
    /// 左侧：主题管理
    /// 中间：TabControl（Layout / Font / Colors / Threshold）
    /// 右侧：ThemePreviewControl（实时预览）
    /// </summary>
    public class ThemeEditorForm : Form
    {
        // 左栏：主题管理
        private ListBox? lstThemes;
        private Button? btnNew;
        private Button? btnRename;
        private Button? btnCopy;
        private Button? btnDelete;

        // 中间：TabControl + 编辑页
        private TabControl? tab;
        private Panel? pageLayout;
        private Panel? pageFont;
        private Panel? pageColors;
        private Panel? pageThreshold;

        // 右栏：预览
        private ThemePreviewControl? preview;

        private Theme? _theme;
        private string _currentThemeName = "";

        // 系统字体缓存
        private InstalledFontCollection _fontCollection;

        public ThemeEditorForm()
        {
            Text = "LiteMonitor Theme Editor";
            StartPosition = FormStartPosition.CenterScreen;
            
            // DPI 缩放支持
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            
            // 基础尺寸（96 DPI 下的尺寸）
            int baseWidth = 1050;
            int baseHeight = 720;
            
            // 计算 DPI 缩放后的尺寸
            using (Graphics g = CreateGraphics())
            {
                float dpiScaleX = g.DpiX / 96f;
                float dpiScaleY = g.DpiY / 96f;
                
                Width = (int)(baseWidth * dpiScaleX);
                Height = (int)(baseHeight * dpiScaleY);
                MinimumSize = new Size((int)(baseWidth * dpiScaleX), (int)(baseHeight * dpiScaleY));
            }

            DoubleBuffered = true;

            _fontCollection = new InstalledFontCollection();

            BuildUI();
            LoadThemeList();
        }

        // ============================================================
        // 初始化 UI 布局
        // ============================================================
        private void BuildUI()
        {
            // 获取 DPI 缩放比例
            float dpiScale = GetDpiScale();
            
            // ------------- 中间：TabControl -------------
            tab = new TabControl
            {
                Dock = DockStyle.Fill,
                Alignment = TabAlignment.Top,
                Font = new Font("Microsoft YaHei UI", 10) // 字体自动 DPI 缩放
            };
            
            pageLayout = new Panel { AutoScroll = true, Dock = DockStyle.Fill };
            pageFont = new Panel { AutoScroll = true, Dock = DockStyle.Fill };
            pageColors = new Panel { AutoScroll = true, Dock = DockStyle.Fill };
            pageThreshold = new Panel { AutoScroll = true, Dock = DockStyle.Fill };
            
            
            var tpColors = new TabPage(" 颜色 / Colors ");
            tpColors.Controls.Add(pageColors);
            tab.TabPages.Add(tpColors);

            var tpFont = new TabPage(" 字体 / Font ");
            tpFont.Controls.Add(pageFont);
            tab.TabPages.Add(tpFont);

            var tpLayout = new TabPage(" 界面布局 / Layout ");
            tpLayout.Controls.Add(pageLayout);
            tab.TabPages.Add(tpLayout);

            var tpThreshold = new TabPage(" 报警阈值 / Threshold ");
            tpThreshold.Controls.Add(pageThreshold);
            tab.TabPages.Add(tpThreshold);

           

            
            
            // ------------- 右侧：预览 -------------
            preview = new ThemePreviewControl
            {
                Dock = DockStyle.Right,
                Width = (int)((this.Width / 3.0) * dpiScale),
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            
            // ------------- 左侧：主题管理 -------------
            var leftPanel = new Panel
            {
                Dock = DockStyle.Left,
                Width = (int)(220 * dpiScale),
                Padding = new Padding((int)(10 * dpiScale)),
                BackColor = Color.WhiteSmoke
            };
            
            lstThemes = new ListBox
            {
                Dock = DockStyle.Top,
                Height = (int)(500 * dpiScale),
                Font = new Font("Microsoft YaHei UI", 10), // 字体自动 DPI 缩放
            };
            lstThemes.SelectedIndexChanged += (_, __) => LoadSelectedTheme();
            leftPanel.Controls.Add(lstThemes);

            
            btnDelete = new Button { Text = "删除主题 / Delete Theme", Dock = DockStyle.Top, Height = (int)(40 * dpiScale) };
            btnDelete.Click += (_, __) => CmdDeleteTheme();
            leftPanel.Controls.Add(btnDelete);

            btnCopy = new Button { Text = "复制主题 / Copy Theme", Dock = DockStyle.Top, Height = (int)(40 * dpiScale) };
            btnCopy.Click += (_, __) => CmdCopyTheme();
            leftPanel.Controls.Add(btnCopy);

            btnRename = new Button { Text = "重命名主题 / Rename Theme", Dock = DockStyle.Top, Height = (int)(40 * dpiScale) };
            btnRename.Click += (_, __) => CmdRenameTheme();
            leftPanel.Controls.Add(btnRename);

             
            btnNew = new Button { Text = "新建主题 / New Theme", Dock = DockStyle.Top, Height = (int)(40 * dpiScale) };
            btnNew.Click += (_, __) => CmdNewTheme();
            leftPanel.Controls.Add(btnNew);
            
            // 按照正确的顺序添加控件到主窗体
            Controls.Add(tab);      // 先添加填充中间的控件
            Controls.Add(preview);  // 再添加右侧控件
            Controls.Add(leftPanel); // 最后添加左侧控件

            // 确保预览区域自适应宽度
            void UpdatePreviewWidth()
            {
                // 计算预览区域宽度，留出左侧面板和中间选项卡的空间
                int remainingWidth = this.Width - leftPanel.Width - (int)(20 * dpiScale); // 减去左侧面板宽度和一些边距
                preview.Width = Math.Max((int)(240 * dpiScale), remainingWidth / 3); // 确保最小宽度为240
            }
            
            // 初始化时设置宽度
            UpdatePreviewWidth();
            
            // 窗口大小改变时更新宽度
            this.Resize += (_, __) =>
            {
                UpdatePreviewWidth();
            };
        }

        // ============================================================
        // 加载主题列表
        // ============================================================
        private void LoadThemeList()
        {
            lstThemes.Items.Clear();
            foreach (var t in ThemeFileService.ListThemes())
                lstThemes.Items.Add(t);

            if (lstThemes.Items.Count > 0)
                lstThemes.SelectedIndex = 0;
        }

        // ============================================================
        // 获取 DPI 缩放比例
        // ============================================================
        private float GetDpiScale()
        {
            using (Graphics g = CreateGraphics())
            {
                return g.DpiX / 96f;
            }
        }

        // ============================================================
        // 加载选中的主题
        // ============================================================
        private void LoadSelectedTheme()
        {
            if (lstThemes.SelectedItem == null) return;

            _currentThemeName = lstThemes.SelectedItem.ToString()!;
            try
            {
                _theme = ThemeFileService.LoadTheme(_currentThemeName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法加载主题：\n" + ex.Message);
                return;
            }

            BuildLayoutPage();
            BuildFontPage();
            BuildColorsPage();
            BuildThresholdPage();

            preview.SetTheme(_theme);
        }


        // ============================================================
        // 页面构建：Layout
        // ============================================================
        private void BuildLayoutPage()
        {
            pageLayout.Controls.Clear();
            if (_theme == null) return;

            float dpiScale = GetDpiScale();
            int y = (int)(10 * dpiScale);

            NumericUpDown AddNum(string label, Func<int> get, Action<int> set)
            {
                pageLayout.Controls.Add(new Label
                {
                    Text = label,
                    Location = new Point((int)(10 * dpiScale), y + (int)(4 * dpiScale)),
                    Width = (int)(200 * dpiScale)
                });

                var n = new NumericUpDown
                {
                    Location = new Point((int)(220 * dpiScale), y),
                    Width = (int)(100 * dpiScale),
                    Minimum = 0,
                    Maximum = 2000,
                    Value = get()
                };
                n.ValueChanged += (_, __) => { set((int)n.Value); preview.SetTheme(_theme!); };
                pageLayout.Controls.Add(n);

                y += (int)(36 * dpiScale);
                return n;
            }

            var L = _theme.Layout;

            AddNum("界面宽度 / Width (px)", () => L.Width, v => L.Width = v);
            AddNum("行高 / RowHeight (px)", () => L.RowHeight, v => L.RowHeight = v);
            AddNum("内边距 / Padding (px)", () => L.Padding, v => L.Padding = v);
            AddNum("窗体圆角 / CornerRadius (px)", () => L.CornerRadius, v => L.CornerRadius = v);
            AddNum("组块圆角 / GroupRadius (px)", () => L.GroupRadius, v => L.GroupRadius = v);
            AddNum("组块内边距 / GroupPadding (px)", () => L.GroupPadding, v => L.GroupPadding = v);
            AddNum("组块间距 / GroupSpacing (px)", () => L.GroupSpacing, v => L.GroupSpacing = v);
            AddNum("组块底部留白 / GroupBottom (px)", () => L.GroupBottom, v => L.GroupBottom = v);
            AddNum("监控项间距 / ItemGap (px)", () => L.ItemGap, v => L.ItemGap = v);
            AddNum("组标题偏移 / GroupTitleOffset (px)", () => L.GroupTitleOffset, v => L.GroupTitleOffset = v);

            AddSaveButton(pageLayout);
        }


        // ============================================================
        // 页面构建：Font
        // ============================================================
        private void BuildFontPage()
        {
            pageFont.Controls.Clear();
            if (_theme == null) return;

            float dpiScale = GetDpiScale();
            int y = (int)(10 * dpiScale);

            // 系统字体
            var fonts = _fontCollection.Families.Select(f => f.Name).OrderBy(n => n).ToList();

            ComboBox AddFontCombo(string label, Func<string> get, Action<string> set)
            {
                pageFont.Controls.Add(new Label
                {
                    Text = label,
                    Location = new Point((int)(10 * dpiScale), y + (int)(4 * dpiScale)),
                    Width = (int)(180 * dpiScale)
                });

                var combo = new ComboBox
                {
                    Location = new Point((int)(200 * dpiScale), y),
                    Width = (int)(180 * dpiScale),
                    DropDownStyle = ComboBoxStyle.DropDownList
                };

                combo.Items.AddRange(fonts.ToArray());
                combo.SelectedItem = get();
                combo.SelectedIndexChanged += (_, __) =>
                {
                    set(combo.SelectedItem.ToString()!);
                    _theme!.BuildFonts(); // 添加这一行来构建新的字体
                    preview.SetTheme(_theme!);
                };

                pageFont.Controls.Add(combo);
                y += (int)(36 * dpiScale);

                return combo;
            }

            NumericUpDown AddNum(string label, Func<double> get, Action<int> set)
            {
                pageFont.Controls.Add(new Label
                {
                    Text = label,
                    Location = new Point((int)(10 * dpiScale), y + (int)(4 * dpiScale)),
                    Width = (int)(180 * dpiScale)
                });

                var n = new NumericUpDown
                {
                    Location = new Point((int)(200 * dpiScale), y),
                    Width = (int)(150 * dpiScale),
                    Minimum = 8,
                    Maximum = 72,
                    Value = (decimal)get()
                };
                n.ValueChanged += (_, __) =>
                {
                    set((int)n.Value);
                    _theme!.BuildFonts();
                    preview.SetTheme(_theme!);
                };
                pageFont.Controls.Add(n);

                y += (int)(36 * dpiScale);
                return n;
            }

            var F = _theme.Font;

            AddFontCombo("主字体 / Font Family", () => F.Family, v => F.Family = v);
            AddFontCombo("数值字体 / Value Font", () => F.ValueFamily, v => F.ValueFamily = v);

            AddNum("标题字号 / Title Size (pt)", () => F.Title, v => F.Title = v);
            AddNum("组标题字号 / GroupTitle Size (pt)", () => F.Group, v => F.Group = v);
            AddNum("项目字号 / Item Size (pt)", () => F.Item, v => F.Item = v);
            AddNum("数值字号 / Value Size (pt)", () => F.Value, v => F.Value = v);

            var chkBold = new CheckBox
            {
                Text = "加粗 / Bold",   
                Location = new Point((int)(10 * dpiScale), y),
                Checked = F.Bold
            };
            chkBold.CheckedChanged += (_, __) =>
            {
                F.Bold = chkBold.Checked;
                _theme.BuildFonts();
                preview.SetTheme(_theme!);
            };
            pageFont.Controls.Add(chkBold);

            y += (int)(40 * dpiScale);

            AddSaveButton(pageFont);
        }


        // ============================================================
        // 页面构建：Colors
        // ============================================================
        private void BuildColorsPage()
        {
            pageColors.Controls.Clear();
            if (_theme == null) return;

            float dpiScale = GetDpiScale();
            int y = (int)(10 * dpiScale);

            Button AddColor(string label, Func<string> get, Action<string> set)
            {
                pageColors.Controls.Add(new Label
                {
                    Text = label,
                    Location = new Point((int)(10 * dpiScale), y + (int)(6 * dpiScale)),
                    Width = (int)(210 * dpiScale)
                });

                // 获取当前颜色
                Color currentColor = ThemeManager.ParseColor(get());
                
                // 简单的对比度计算：根据颜色亮度决定文字颜色
                var brightness = (currentColor.R * 0.299 + currentColor.G * 0.587 + currentColor.B * 0.114);
                var textColor = brightness > 127 ? Color.Black : Color.White;
                
                var btn = new Button
                {
                    Location = new Point((int)(220 * dpiScale), y),
                    Width = (int)(120 * dpiScale),
                    Height = (int)(30 * dpiScale), // 稍微增加高度
                    Text = get(),
                    BackColor = currentColor, // 设置按钮背景色为当前颜色
                    ForeColor = textColor // 根据背景亮度设置文字颜色
                };

                btn.Click += (_, __) =>
                {
                    using var cd = new ColorDialog();
                    cd.Color = ThemeManager.ParseColor(get());
                    // 配置新版颜色选择器选项
                    cd.AllowFullOpen = true; // 允许用户自定义颜色
                    cd.FullOpen = true; // 默认展开自定义颜色面板
                    cd.AnyColor = true; // 允许选择任何颜色
                    cd.SolidColorOnly = false; // 允许选择非纯色
                    cd.ShowHelp = false; // 不显示帮助按钮
                    
                    if (cd.ShowDialog() == DialogResult.OK)
                    {
                        string hex = $"#{cd.Color.R:X2}{cd.Color.G:X2}{cd.Color.B:X2}";
                        set(hex);
                        btn.Text = hex;
                        btn.BackColor = cd.Color; // 更新按钮背景色
                        // 更新文字颜色
                        var newBrightness = (cd.Color.R * 0.299 + cd.Color.G * 0.587 + cd.Color.B * 0.114);
                        btn.ForeColor = newBrightness > 127 ? Color.Black : Color.White;
                        preview.SetTheme(_theme!);
                    }
                };

                pageColors.Controls.Add(btn);

                y += (int)(45 * dpiScale); // 增加垂直间距以适应更高的按钮
                return btn;
            }

            var C = _theme.Color;

            AddColor("背景色 / Background", () => C.Background, v => C.Background = v);
            AddColor("组背景 / GroupBackground", () => C.GroupBackground, v => C.GroupBackground = v);

            AddColor("标题字体 / TextTitle", () => C.TextTitle, v => C.TextTitle = v);
            AddColor("组标题字体 / TextGroup", () => C.TextGroup, v => C.TextGroup = v);
            AddColor("普通字体 / TextPrimary", () => C.TextPrimary, v => C.TextPrimary = v);

            AddColor("安全值颜色 / ValueSafe", () => C.ValueSafe, v => C.ValueSafe = v);    
            AddColor("警告值颜色 / ValueWarn", () => C.ValueWarn, v => C.ValueWarn = v);
            AddColor("严重值颜色 / ValueCrit", () => C.ValueCrit, v => C.ValueCrit = v);

            AddColor("进度条背景 / BarBackground", () => C.BarBackground, v => C.BarBackground = v);
            AddColor("进度条低位 / BarLow", () => C.BarLow, v => C.BarLow = v);
            AddColor("进度条中位 / BarMid", () => C.BarMid, v => C.BarMid = v);
            AddColor("进度条高位 / BarHigh", () => C.BarHigh, v => C.BarHigh = v);  

            AddSaveButton(pageColors);
        }


        // ============================================================
        // 页面构建：Thresholds
        // ============================================================
        private void BuildThresholdPage()
        {
            pageThreshold.Controls.Clear();
            if (_theme == null) return;

            float dpiScale = GetDpiScale();
            int y = (int)(10 * dpiScale);

            void AddPair(string name, Func<double> gw, Func<double> gc, Action<double> sw, Action<double> sc)
            {
                pageThreshold.Controls.Add(new Label
                {
                    Text = name,
                    Location = new Point((int)(10 * dpiScale), y + (int)(6 * dpiScale)),
                    Width = (int)(200 * dpiScale) // 减少字段名宽度，整体左移
                });

                var warn = new NumericUpDown
                {
                    Location = new Point((int)(220 * dpiScale), y), // 整体左移，让布局更紧凑
                    Width = (int)(80 * dpiScale),
                    DecimalPlaces = 0,
                    Maximum = 9999,
                    Value = (decimal)gw()
                };
                warn.ValueChanged += (_, __) => { sw((double)warn.Value); preview.SetTheme(_theme!); };
                pageThreshold.Controls.Add(warn);

                var crit = new NumericUpDown
                {
                    Location = new Point((int)(310 * dpiScale), y), // 整体左移，让输入框之间更紧凑
                    Width = (int)(80 * dpiScale),
                    DecimalPlaces = 0,
                    Maximum = 9999,
                    Value = (decimal)gc()
                };
                crit.ValueChanged += (_, __) => { sc((double)crit.Value); preview.SetTheme(_theme!); };
                pageThreshold.Controls.Add(crit);

                y += (int)(40 * dpiScale);
            }

            var T = _theme.Thresholds;

            // 添加报警类型说明标签
            pageThreshold.Controls.Add(new Label
            {
                Text = "指标类型 / Metric Type",
                Location = new Point((int)(10 * dpiScale), y + (int)(6 * dpiScale)),
                Width = (int)(200 * dpiScale), // 增加宽度以完整显示字段名称
                Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold) // 字体自动 DPI 缩放
            });

            pageThreshold.Controls.Add(new Label
            {
                Text = "警告 / Warn",
                Location = new Point((int)(210 * dpiScale), y + 6), // 调整位置以完整显示标签内容
                Width = (int)(100 * dpiScale), // 增加宽度以完整显示标签内容
                Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter
            });

            pageThreshold.Controls.Add(new Label
            {
                Text = "严重 / Crit",
                Location = new Point((int)(300 * dpiScale), y + 6), // 调整位置与下方输入框对齐
                Width = (int)(90 * dpiScale), // 增加宽度以完整显示标签内容
                Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter
            });

            y += (int)(35 * dpiScale); // 增加垂直间距，应用DPI缩放

            AddPair("负载阈值 / Load (%)", () => T.Load.Warn, () => T.Load.Crit,
                v => T.Load.Warn = v, v => T.Load.Crit = v);

            AddPair("温度阈值 / Temp (°C)", () => T.Temp.Warn, () => T.Temp.Crit,
                v => T.Temp.Warn = v, v => T.Temp.Crit = v);

            AddPair("内存阈值 / Mem (%)", () => T.Mem.Warn, () => T.Mem.Crit,
                v => T.Mem.Warn = v, v => T.Mem.Crit = v);

            AddPair("显存阈值 / Vram (%)", () => T.Vram.Warn, () => T.Vram.Crit,
                v => T.Vram.Warn = v, v => T.Vram.Crit = v);

            AddPair("网络阈值 / Net (KB/s)", () => T.NetKBps.Warn, () => T.NetKBps.Crit,
                v => T.NetKBps.Warn = v, v => T.NetKBps.Crit = v);

            AddSaveButton(pageThreshold);
        }


        // ============================================================
        // 通用保存按钮
        // ============================================================
        private void AddSaveButton(Panel page)
        {
            float dpiScale = GetDpiScale();
            
            var btn = new Button
            {
                Text = "保存主题 / Save Theme",
                Width = (int)(200 * dpiScale),
                Height = (int)(40 * dpiScale),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Location = new Point((int)(10 * dpiScale), 0) // 初始位置设为顶部，让Anchor属性自动处理到底部的定位
            };

            // 为了确保按钮在面板加载完成后正确定位到底部，添加Layout事件处理
            page.Layout += (sender, e) => {
                if (page.Controls.Contains(btn))
                {
                    btn.Top = page.ClientSize.Height - (int)(80 * dpiScale); // 增加底部间距，应用DPI缩放
                }
            };

            btn.Click += (_, __) => SaveCurrentTheme();
            page.Controls.Add(btn);
        }

        private void SaveCurrentTheme()
        {
            if (_theme == null) return;

            try
            {
                ThemeFileService.SaveTheme(_currentThemeName, _theme);

                // 刷新主界面菜单的主题列表
                RefreshMainFormThemeMenu();

                // 合并保存成功和是否应用主题的弹窗
                var result = MessageBox.Show("主题已保存 / Saved Theme: " + _currentThemeName +
                    "\n\n是否立即应用此主题？\nApply this theme now?",
                    "保存成功 / Save Success",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    ApplyCurrentThemeToMainForm();
                    RefreshMainFormThemeMenu();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败 / Save Failed:\n" + ex.Message,
                    "错误 / Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 刷新主界面的主题菜单列表
        /// </summary>
        private void RefreshMainFormThemeMenu()
        {
            // 查找主窗体实例
            var mainForm = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
            if (mainForm != null)
            {
                // 调用主窗体的菜单重建方法
                mainForm.RebuildMenus();
            }
        }

        /// <summary>
        /// 应用当前主题到主窗体
        /// </summary>
        private void ApplyCurrentThemeToMainForm()
        {
            // 查找主窗体实例
            var mainForm = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
            if (mainForm != null)
            {
                // 获取主窗体的UIController
                var uiControllerField = typeof(MainForm).GetField("_ui",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var settingsField = typeof(MainForm).GetField("_cfg",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (uiControllerField != null && settingsField != null)
                {
                    var uiController = uiControllerField.GetValue(mainForm) as UIController;
                    var settings = settingsField.GetValue(mainForm) as Settings;

                    if (uiController != null && settings != null)
                    {
                        // 更新设置中的主题名称
                        settings.Skin = _currentThemeName;
                        settings.Save();

                        // 应用新主题
                        uiController.ApplyTheme(_currentThemeName);

                    }
                }
            }
        }


        // ============================================================
        // 左侧菜单功能：新建、复制、重命名、删除
        // ============================================================
        private void CmdNewTheme()
        {
            string name = InputBox("输入新主题名称 / New Theme Name:");
            if (string.IsNullOrWhiteSpace(name)) return;

            try
            {
                ThemeFileService.CreateTheme(name);
                LoadThemeList();
                lstThemes.SelectedItem = name;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void CmdRenameTheme()
        {
            if (_currentThemeName == "") return;

            string name = InputBox("输入新的主题名称 / Rename Theme Name:", _currentThemeName);
            if (string.IsNullOrWhiteSpace(name)) return;

            try
            {
                ThemeFileService.RenameTheme(_currentThemeName, name);
                LoadThemeList();
                lstThemes.SelectedItem = name;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void CmdCopyTheme()
        {
            if (_currentThemeName == "") return;

            string name = InputBox("复制的主题名称 / Copy Theme Name:", _currentThemeName + "_copy");
            if (string.IsNullOrWhiteSpace(name)) return;

            try
            {
                ThemeFileService.DuplicateTheme(_currentThemeName, name);
                LoadThemeList();
                lstThemes.SelectedItem = name;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void CmdDeleteTheme()
        {
            if (_currentThemeName == "") return;

            if (MessageBox.Show($"确定删除主题 / Delete Theme: {_currentThemeName}？", "提示 / Confirm:",
                MessageBoxButtons.YesNo) != DialogResult.Yes)
                return;

            try
            {
                ThemeFileService.DeleteTheme(_currentThemeName);
                LoadThemeList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        // ============================================================
        // 简易输入框
        // ============================================================
        private string InputBox(string title, string defaultText = "")
        {
            float dpiScale = GetDpiScale();
            
            Form f = new Form
            {
                Text = title,
                Width = (int)(400 * dpiScale),
                Height = (int)(150 * dpiScale),
                StartPosition = FormStartPosition.CenterParent
            };

            TextBox tb = new TextBox
            {
                Left = (int)(20 * dpiScale),
                Top = (int)(20 * dpiScale),
                Width = (int)(340 * dpiScale),
                Text = defaultText
            };

            Button ok = new Button
            {
                Text = "确定 / OK",
                Left = (int)(80 * dpiScale),
                Top = (int)(60 * dpiScale),
                Width = (int)(100 * dpiScale),
                Height = (int)(30 * dpiScale) // 添加DPI缩放的高度
            };
            ok.Click += (_, __) => { f.DialogResult = DialogResult.OK; f.Close(); };

            Button cancel = new Button
            {
                Text = "取消 / Cancel",
                Left = (int)(180 * dpiScale),
                Top = (int)(60 * dpiScale),
                Width = (int)(100 * dpiScale),
                Height = (int)(30 * dpiScale) // 添加DPI缩放的高度
            };
            cancel.Click += (_, __) => { f.DialogResult = DialogResult.Cancel; f.Close(); };

            f.Controls.Add(tb);
            f.Controls.Add(ok);
            f.Controls.Add(cancel);

            return f.ShowDialog() == DialogResult.OK ? tb.Text : "";
        }
    }
}