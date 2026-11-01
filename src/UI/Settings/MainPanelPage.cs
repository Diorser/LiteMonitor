using System;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;
using System.Diagnostics;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class MainPanelPage : SettingsPageBase
    {
        private Panel _container;
        private bool _isLoaded = false;

        public MainPanelPage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);
            _container = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20) };
            this.Controls.Add(_container);
        }

        public override void OnShow()
        {
            base.OnShow();
            if (Config == null || _isLoaded) return;

            _container.SuspendLayout();
            ClearAndDispose(_container.Controls);

            CreateBehaviorCard();
            CreateAppearanceCard();
            CreateHorizontalModeGroup();
            CreateWebCard();

            _container.ResumeLayout();
            _isLoaded = true;
        }

        private void CreateBehaviorCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.MainFormSettings"));

            // 1. Hide Main Form (with logic)
            var chkHide = group.AddToggle(this, "Menu.HideMainForm", 
                () => Config.HideMainForm, 
                v => Config.HideMainForm = v);
            
            chkHide.CheckedChanged += (s, e) => EnsureSafeVisibility(chkHide, null, null);

            // 2. Toggles
            group.AddToggle(this, "Menu.TopMost", () => Config.TopMost, v => Config.TopMost = v);
            group.AddToggle(this, "Menu.ClampToScreen", () => Config.ClampToScreen, v => Config.ClampToScreen = v);
            group.AddToggle(this, "Menu.AutoHide", () => Config.AutoHide, v => Config.AutoHide = v);
            group.AddToggle(this, "Menu.ClickThrough", () => Config.ClickThrough, v => Config.ClickThrough = v);

            // 3. Double Click Action
            string[] actions = { 
                LanguageManager.T("Menu.ActionSwitchLayout"),
                LanguageManager.T("Menu.ActionTaskMgr"),
                LanguageManager.T("Menu.ActionSettings"),
                LanguageManager.T("Menu.ActionTrafficHistory"),
                LanguageManager.T("Menu.CleanMemory"),
                LanguageManager.T("Menu.OpenWeb")
            };
            group.AddComboIndex(this, "Menu.DoubleClickAction", actions,
                () => Config.MainFormDoubleClickAction,
                idx => Config.MainFormDoubleClickAction = idx
            );

            AddGroupToPage(group);
        }

        private void CreateAppearanceCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.Appearance"));

            // 1. Theme
            group.AddCombo(this, "Menu.Theme", ThemeManager.GetAvailableThemes(), 
                () => Config.Skin, 
                v => Config.Skin = v);

            // 2. Orientation
            group.AddComboIndex(this, "Menu.DisplayMode", 
                new[] { LanguageManager.T("Menu.Vertical"), LanguageManager.T("Menu.Horizontal") },
                () => Config.HorizontalMode ? 1 : 0, 
                idx => Config.HorizontalMode = (idx == 1));

            // 3. Width
            int[] widths = { 180, 200, 220, 240, 260, 280, 300, 360, 420, 480, 540, 600, 660, 720, 780, 840, 900, 960, 1020, 1080, 1140, 1200 };
            group.AddCombo(this, "Menu.Width", 
                widths.Select(w => w + " px"), 
                () => Config.PanelWidth + " px",
                s => Config.PanelWidth = MetricUtils.ParseInt(s));

            // 4. Background Opacity
            double[] opacities = { 1.0, 0.95, 0.9, 0.85, 0.8, 0.75, 0.7, 0.6, 0.5, 0.4, 0.3 };
            group.AddCombo(this, "Menu.Opacity",
                opacities.Select(o => Math.Round(o * 100) + "%"),
                () => Math.Round(Config.Opacity * 100) + "%",
                s => Config.Opacity = MetricUtils.ParseDouble(s) / 100.0);

            // 5. Text Opacity
            group.AddCombo(this, "Menu.TextOpacity",
                opacities.Select(o => Math.Round(o * 100) + "%"),
                () => Math.Round(Config.TextOpacity * 100) + "%",
                s => Config.TextOpacity = MetricUtils.ParseDouble(s) / 100.0);

            // 5. Scale
            double[] scales = { 2.0, 1.75, 1.5, 1.25, 1.0, 0.9, 0.85, 0.8, 0.75, 0.7, 0.6, 0.5 };
            group.AddCombo(this, "Menu.Scale",
                scales.Select(s => (s * 100) + "%"),
                () => (Config.UIScale * 100) + "%",
                s => Config.UIScale = MetricUtils.ParseDouble(s) / 100.0);

            AddGroupToPage(group);

        }

        private void CreateHorizontalModeGroup()
        {
            // --- Group: Horizontal Mode ---
            var groupHorz = new LiteSettingsGroup(LanguageManager.T("Menu.Horizontal"));

          

            // 1. Follow Taskbar
            groupHorz.AddToggle(this, "Menu.HorizontalFollowsTaskbar", 
                () => Config.HorizontalFollowsTaskbar, 
                v => Config.HorizontalFollowsTaskbar = v);
                
            // 2. Single Line
            groupHorz.AddToggle(this, "Menu.TaskbarSingleLine",
                () => Config.HorizontalSingleLine,
                v => Config.HorizontalSingleLine = v);

            // 3. Spacing
            groupHorz.AddInt(this, "Menu.TaskbarItemSpacing", "px",
                () => Config.HorizontalItemSpacing,
                v => Config.HorizontalItemSpacing = v);

            groupHorz.AddInt(this, "Menu.TaskbarInnerSpacing", "px",
                () => Config.HorizontalInnerSpacing,
                v => Config.HorizontalInnerSpacing = v);
            
            AddGroupToPage(groupHorz);
        }

        private void CreateWebCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.WebServer")); 

            // A. Header with Action Button
            group.AddButton(LanguageManager.T("Menu.WebServerTip"), LanguageManager.T("Menu.OpenWeb"), () => 
            {
                Core.Actions.WebActions.OpenWebMonitor(Config);
            });

            // B. Settings
            group.AddToggle(this, "Menu.WebServer", 
                () => Config.WebServerEnabled, 
                v => Config.WebServerEnabled = v
            );

            group.AddInt(this, "Menu.WebServerPort", "", 
                () => Config.WebServerPort, 
                v => Config.WebServerPort = v,
                60
            );

            

            // Password
            group.AddInput(this, "Menu.WebServerPassword",
                () => Config.WebServerPassword,
                v => Config.WebServerPassword = v,
                LanguageManager.T("Menu.WebServerPasswordTip"),
                100, HorizontalAlignment.Center);

            // C. API Link
            bool isChinese = !string.IsNullOrEmpty(Config.Language) && 
                                     Config.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            
            group.AddLink("API (JSON): http://<IP>:<Port>/api/snapshot?pwd=123456 ", 
                isChinese ? "内网无法连接？" : "Connection Issue?", 
                () => ShowFirewallHelp(isChinese));

            AddGroupToPage(group);
        }

        private void ShowFirewallHelp(bool isChinese)
        {
            try
            {
                Process.Start(new ProcessStartInfo("firewall.cpl") { UseShellExecute = true });

                string msg;
                if (isChinese)
                {
                    msg = "如果您在首次启动时的【Windows 防火墙授权弹窗】中点击了“取消”，\n" +
                          "或者是直接关闭了弹窗，会导致手机无法内网访问网页。\n\n" +
                          "已为你打开防火墙设置，请按以下步骤手动“解封”：\n" +
                          "------------------------------\n" +
                          "1. 【关键】请点击新窗口左上侧的【允许应用或功能通过 Windows Defender 防火墙】。\n" +
                          "2. 点击右上角的【更改设置】按钮（如果是灰色的）。\n" +
                          "3. 在列表中找到【LiteMonitor】。\n" +
                          "4. ★ 必须打三个勾（缺一不可）：\n" +
                          "   ✅ 左侧名字【LiteMonitor】\n" +
                          "   ✅ 右侧【专用】\n" +
                          "   ✅ 右侧【公用】\n" +
                          "5. 点击底部的【确定】保存。";
                }
                else
                {
                    msg = "If you clicked 'Cancel' or closed the firewall permission dialog on the first launch,\n" +
                          "your mobile device will not be able to connect to the web page.\n\n" +
                          "The firewall settings have been opened for you. Please follow these steps:\n" +
                          "------------------------------\n" +
                          "1. [Critical] Click 'Allow an app or feature through Windows Defender Firewall' on the top-left.\n" +
                          "2. Click the 'Change settings' button (if greyed out).\n" +
                          "3. Find 'LiteMonitor' in the list.\n" +
                          "4. ★ You MUST check all three boxes:\n" +
                          "   ✅ Left Name [LiteMonitor]\n" +
                          "   ✅ Right [Private]\n" +
                          "   ✅ Right [Public]\n" +
                          "5. Click [OK] at the bottom to save.";
                }
                MessageBox.Show(msg, isChinese ? "内网连接修复指引" : "Connection Fix Guide", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void AddGroupToPage(LiteSettingsGroup group)
        {
            var wrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 20) };
            wrapper.Controls.Add(group);
            _container.Controls.Add(wrapper);
            _container.Controls.SetChildIndex(wrapper, 0);
        }
    }
}
