using System;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class ThresholdPage : SettingsPageBase
    {
        private Panel _container;
        private bool _isLoaded = false;

        public ThresholdPage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);
            // ★★★ 修改：Padding 缩放
            _container = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = UIUtils.S(new Padding(20)) }; 
                this.Controls.Add(_container);
        }

        public override void OnShow()
        {
            base.OnShow();
            if (Config == null || _isLoaded) return;

            _container.SuspendLayout();
            _container.Controls.Clear();

            // === 1. 高温告警分组 ===
            var grpAlert = new LiteSettingsGroup(LanguageManager.T("Menu.AlertTemp"));
            
            // 使用工厂：开关
            AddBool(grpAlert, "Menu.AlertTemp", 
                () => Config.AlertTempEnabled, 
                v => Config.AlertTempEnabled = v);

            // 使用工厂：数字输入 (Int, 红色)
            AddNumberInt(grpAlert, "Menu.AlertThreshold", "°C", 
                () => Config.AlertTempThreshold, 
                v => Config.AlertTempThreshold = v, 
                width: 80, color: UIColors.TextCrit);

            AddGroupToPage(grpAlert);

            // === 2. 硬件负载 ===
            var grpHardware = new LiteSettingsGroup(LanguageManager.T("Menu.GeneralHardware"));
            grpHardware.AddFullItem(new LiteNote(LanguageManager.T("Menu.ThresholdsTips"), 0));
            
            AddDoubleThresholdRow(grpHardware, LanguageManager.T("Menu.HardwareLoad"), "%", Config.Thresholds.Load);
            AddDoubleThresholdRow(grpHardware, LanguageManager.T("Menu.HardwareTemp"), "°C", Config.Thresholds.Temp);

            AddGroupToPage(grpHardware);

            // === 3. 网络与磁盘 ===
            var grpNet = new LiteSettingsGroup(LanguageManager.T("Menu.NetworkDiskSpeed"));
            
            AddDoubleThresholdRow(grpNet, LanguageManager.T("Menu.DiskIOSpeed"), "MB/s", Config.Thresholds.DiskIOMB);
            AddDoubleThresholdRow(grpNet, LanguageManager.T("Menu.UploadSpeed"), "MB/s", Config.Thresholds.NetUpMB);
            AddDoubleThresholdRow(grpNet, LanguageManager.T("Menu.DownloadSpeed"), "MB/s", Config.Thresholds.NetDownMB);

            AddGroupToPage(grpNet);

            // === 4. 流量限额 ===
            var grpData = new LiteSettingsGroup(LanguageManager.T("Menu.DailyTraffic"));

            AddDoubleThresholdRow(grpData, LanguageManager.T("Items.DATA.DayUp"), "MB", Config.Thresholds.DataUpMB);
            AddDoubleThresholdRow(grpData, LanguageManager.T("Items.DATA.DayDown"), "MB", Config.Thresholds.DataDownMB);

            AddGroupToPage(grpData);

            _container.ResumeLayout();
            _isLoaded = true;
        }

        // 专门用于 "警告 -> 严重" 这种双输入的特殊行，保留在此处
        private void AddDoubleThresholdRow(LiteSettingsGroup group, string title, string unit, ValueRange range)
        {
            // ★★★ 修改：Height 缩放
            var panel = new Panel { Height = UIUtils.S(40), Margin = new Padding(0), Padding = new Padding(0) };
            
            // 标题
            var lblTitle = new Label {
                Text = title, AutoSize = true, 
                Font = new Font("Microsoft YaHei UI", 9F), ForeColor = UIColors.TextMain,
                TextAlign = ContentAlignment.MiddleLeft
            };
            panel.Controls.Add(lblTitle);

            // 右侧容器
            var rightBox = new FlowLayoutPanel {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false, 
                BackColor = Color.Transparent, Padding = new Padding(0)
            };

            // 创建两个输入框
            var inputWarn = new LiteNumberInput("0", unit, LanguageManager.T("Menu.ValueWarnColor"), 140, UIColors.TextWarn);
            // ★★★ 修复：手动调整 Padding
            inputWarn.Padding = UIUtils.S(new Padding(0, 5, 0, 1));
            
            // ★★★ 修改：Margin 缩放
            var arrow = new Label { Text = "➜", AutoSize = true, ForeColor = Color.LightGray, Font = new Font("Microsoft YaHei UI", 9F), Margin = UIUtils.S(new Padding(5, 4, 5, 0)) };
            
            var inputCrit = new LiteNumberInput("0", unit, LanguageManager.T("Menu.ValueCritColor"), 140, UIColors.TextCrit);
            // ★★★ 修复：手动调整 Padding
            inputCrit.Padding = UIUtils.S(new Padding(0, 5, 0, 1));

            // 复用 BindDouble 逻辑
            BindDouble(inputWarn, () => range.Warn, v => range.Warn = v);
            BindDouble(inputCrit, () => range.Crit, v => range.Crit = v);

            rightBox.Controls.Add(inputWarn);
            rightBox.Controls.Add(arrow);
            rightBox.Controls.Add(inputCrit);
            panel.Controls.Add(rightBox);

            // 布局与画线
            panel.Layout += (s, e) => {
                lblTitle.Location = new Point(0, (panel.Height - lblTitle.Height) / 2);
                rightBox.Location = new Point(panel.Width - rightBox.Width, (panel.Height - rightBox.Height) / 2);
            };
            panel.Paint += (s, e) => {
                using(var p = new Pen(Color.FromArgb(240, 240, 240))) e.Graphics.DrawLine(p, 0, panel.Height-1, panel.Width, panel.Height-1);
            };

            group.AddFullItem(panel);
        }

        private void AddGroupToPage(LiteSettingsGroup group)
        {
            // ★★★ 修改：Padding 缩放
            var wrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = UIUtils.S(new Padding(0, 0, 0, 20)) };
            wrapper.Controls.Add(group);
            _container.Controls.Add(wrapper);
            _container.Controls.SetChildIndex(wrapper, 0);
        }
    }
}