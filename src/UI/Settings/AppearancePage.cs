using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class AppearancePage : SettingsPageBase
    {
        private Panel _container;
        private bool _isLoaded = false;

        private LiteComboBox _cmbTheme;
        private LiteComboBox _cmbOrientation;
        private LiteComboBox _cmbWidth;
        private LiteComboBox _cmbOpacity;
        private LiteComboBox _cmbScale;
        
        // 修改：改为下拉框
        private LiteComboBox _cmbTaskbarStyle;
        private LiteComboBox _cmbTaskbarAlign;

        public AppearancePage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);
            _container = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20) };
            this.Controls.Add(_container);
        }

        public override void OnShow()
        {
            if (Config == null || _isLoaded) return;
            _container.SuspendLayout();
            _container.Controls.Clear();

            CreateThemeCard();
            CreateTaskbarCard(); 

            _container.ResumeLayout();
            _isLoaded = true;
        }

        private void CreateThemeCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.Groups.Interface")); // 建议: 这里也可以用 "主界面设置" 或对应的Key

            _cmbTheme = new LiteComboBox();
            foreach (var t in ThemeManager.GetAvailableThemes()) _cmbTheme.Items.Add(t);
            SetComboVal(_cmbTheme, Config.Skin);
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.Theme"), _cmbTheme));

            _cmbOrientation = new LiteComboBox();
            // 优化1：读取语言文件
            _cmbOrientation.Items.Add(LanguageManager.T("Menu.Vertical"));   // 对应 index 0
            _cmbOrientation.Items.Add(LanguageManager.T("Menu.Horizontal")); // 对应 index 1
            _cmbOrientation.SelectedIndex = Config.HorizontalMode ? 1 : 0;
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.DisplayMode"), _cmbOrientation));

            _cmbWidth = new LiteComboBox();
            int[] widths =  { 180, 200, 220, 240, 260, 280, 300, 360, 420, 480, 540, 600, 660, 720, 780, 840, 900, 960, 1020, 1080, 1140, 1200 };
            foreach (var w in widths) _cmbWidth.Items.Add(w + " px");
            SetComboVal(_cmbWidth, Config.PanelWidth + " px");
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.Width"), _cmbWidth));

            _cmbScale = new LiteComboBox();
            double[] scales = { 0.5, 0.75, 0.9, 1.0, 1.25, 1.5, 1.75, 2.0 };
            foreach (var s in scales) _cmbScale.Items.Add((s * 100) + "%");
            SetComboVal(_cmbScale, (Config.UIScale * 100) + "%");
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.Scale"), _cmbScale));

            _cmbOpacity = new LiteComboBox();
            double[] presetOps = { 1.0, 0.95, 0.9, 0.85, 0.8, 0.75, 0.7, 0.6, 0.5, 0.4, 0.3 };
            foreach (var op in presetOps) _cmbOpacity.Items.Add((op * 100) + "%");
            SetComboVal(_cmbOpacity, Math.Round(Config.Opacity * 100) + "%");
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.Opacity"), _cmbOpacity));

            AddGroupToPage(group);
        }


        private void CreateTaskbarCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.TaskbarSettings"));

            // 优化2：任务栏样式 (大字模式 / 小字模式)
            _cmbTaskbarStyle = new LiteComboBox();
            // 0: 大字模式 (默认), 1: 小字紧凑
            // 请在语言文件中添加这两个Key
            _cmbTaskbarStyle.Items.Add(LanguageManager.T("Menu.TaskbarStyleBold")); 
            _cmbTaskbarStyle.Items.Add(LanguageManager.T("Menu.TaskbarStyleRegular"));

            // 判断当前是否为紧凑模式
            bool isCompact = (Math.Abs(Config.TaskbarFontSize - 9f) < 0.1f) && !Config.TaskbarFontBold;
            _cmbTaskbarStyle.SelectedIndex = isCompact ? 1 : 0;
            
            // 使用新Key: Menu.TaskbarStyle (任务栏样式)
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.TaskbarStyle"), _cmbTaskbarStyle));


            // 优化3：任务栏对齐 (左侧 / 右侧)
            _cmbTaskbarAlign = new LiteComboBox();
            // 0: 右侧, 1: 左侧 (与 bool TaskbarAlignLeft 对应逻辑：右=false, 左=true)
            _cmbTaskbarAlign.Items.Add(LanguageManager.T("Menu.TaskbarAlignRight"));
            _cmbTaskbarAlign.Items.Add(LanguageManager.T("Menu.TaskbarAlignLeft"));
            
            _cmbTaskbarAlign.SelectedIndex = Config.TaskbarAlignLeft ? 1 : 0;
            
            // 使用现有Key: Menu.TaskbarAlign (显示方向)
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.TaskbarAlign"), _cmbTaskbarAlign));


            var tips = new Label { 
                Text = "Note: Alignment only works when Win11 Taskbar is centered.", 
                AutoSize = true, ForeColor = Color.Gray, Font = new Font("Microsoft YaHei UI", 8F),
                Padding = new Padding(5)
            };
            group.AddFullItem(tips);

            AddGroupToPage(group);
        }

        private void AddGroupToPage(LiteSettingsGroup group)
        {
            var wrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 20) };
            wrapper.Controls.Add(group);
            _container.Controls.Add(wrapper);
            _container.Controls.SetChildIndex(wrapper, 0);
        }
        
        private void SetComboVal(LiteComboBox cmb, string val)
        {
            if (!cmb.Items.Contains(val)) cmb.Items.Insert(0, val);
            cmb.SelectedItem = val;
        }

        public override void Save()
        {
            if (!_isLoaded) return;

            // === 1. 收集数据 ===

            // 主题
            if (_cmbTheme.SelectedItem != null) Config.Skin = _cmbTheme.SelectedItem.ToString();
            
            // 显示模式 (根据索引判断: 0=Vertical, 1=Horizontal)
            Config.HorizontalMode = (_cmbOrientation.SelectedIndex == 1);
            
            Config.PanelWidth = ParseInt(_cmbWidth.Text);
            Config.UIScale = ParsePercent(_cmbScale.Text);
            Config.Opacity = ParsePercent(_cmbOpacity.Text);

            // 任务栏样式 (根据索引判断: 0=Normal, 1=Compact)
            if (_cmbTaskbarStyle.SelectedIndex == 1) {
                // Compact
                Config.TaskbarFontSize = 9f;
                Config.TaskbarFontBold = false;
            } else {
                // Normal
                Config.TaskbarFontSize = 10f;
                Config.TaskbarFontBold = true;
            }

            // 任务栏对齐 (根据索引判断: 0=Right, 1=Left)
            Config.TaskbarAlignLeft = (_cmbTaskbarAlign.SelectedIndex == 1);


            // === 2. 执行动作 (调用 AppActions) ===
            
            // A. 应用主题、布局、缩放、显示模式
            AppActions.ApplyThemeAndLayout(Config, UI, MainForm);

            // B. 应用窗口属性
            AppActions.ApplyWindowAttributes(Config, MainForm);

            // C. 应用任务栏样式
            AppActions.ApplyTaskbarStyle(Config, UI);
        }

        private int ParseInt(string s) {
            string clean = new string(s.Where(char.IsDigit).ToArray());
            return int.TryParse(clean, out int v) ? v : 0;
        }
        private double ParsePercent(string s) {
            int v = ParseInt(s);
            return v > 0 ? v / 100.0 : 1.0;
        }
    }
}