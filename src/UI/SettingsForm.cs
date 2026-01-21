using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;
using LiteMonitor.src.UI.SettingsPage;

namespace LiteMonitor.src.UI
{
    public class SettingsForm : Form
    {
        private Settings _cfg;
        private UIController _ui;
        private MainForm _mainForm;
        
        private FlowLayoutPanel _pnlNavContainer; 
        private BufferedPanel _pnlContent; // 使用现有的 BufferedPanel
        
        // 缓存所有页面实例
        private Dictionary<string, SettingsPageBase> _pages = new Dictionary<string, SettingsPageBase>();
        private string _currentKey = "";

        // 恢复 WS_EX_COMPOSITED 以防止闪烁，同时配合页面卸载机制解决卡顿
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
                return cp;
            }
        }

        protected override void OnResizeBegin(EventArgs e)
        {
            this.SuspendLayout();
            base.OnResizeBegin(e);
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            this.ResumeLayout(true);
        }

        public SettingsForm(Settings cfg, UIController ui, MainForm mainForm)
        { 
            _cfg = cfg; _ui = ui; _mainForm = mainForm;
            InitializeComponent(); 
            
            // ★★★ 关键点 1：构造时就初始化所有页面 ★★★
            InitPages(); 
        }

        private void InitializeComponent()
        {
            UIUtils.ScaleFactor = this.DeviceDpi / 96f;

            this.Size = new Size(UIUtils.S(820), UIUtils.S(680));
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Text = LanguageManager.T("Menu.SettingsPanel");
            this.Font = new Font("Microsoft YaHei UI", 9F);
            this.BackColor = UIColors.MainBg;
            this.ShowInTaskbar = false;

            // 侧边栏
            var pnlSidebar = new Panel { Dock = DockStyle.Left, Width = UIUtils.S(160), BackColor = UIColors.SidebarBg };
            
            _pnlNavContainer = new FlowLayoutPanel 
            { 
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, 
                Padding = UIUtils.S(new Padding(0, 20, 0, 0)), BackColor = UIColors.SidebarBg
            };
            
            var line = new Panel { Dock = DockStyle.Right, Width = 1, BackColor = UIColors.Border };
            pnlSidebar.Controls.Add(_pnlNavContainer);
            pnlSidebar.Controls.Add(line);
            this.Controls.Add(pnlSidebar);

            // 底部按钮
            var pnlBottom = new Panel { Dock = DockStyle.Bottom, Height = UIUtils.S(60), BackColor = UIColors.MainBg };
            pnlBottom.Paint += (s, e) => e.Graphics.DrawLine(new Pen(UIColors.Border), 0, 0, Width, 0);

            var flowBtns = new FlowLayoutPanel 
            { 
                Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, 
                Padding = UIUtils.S(new Padding(0, 14, 20, 0)), WrapContents = false, BackColor = Color.Transparent 
            };
            
            var btnOk = new LiteButton(LanguageManager.T("Menu.OK"), true);
            var btnCancel = new LiteButton(LanguageManager.T("Menu.Cancel"), false);
            var btnApply = new LiteButton(LanguageManager.T("Menu.Apply"), false);
            var btnReset = new LiteButton(LanguageManager.T("Menu.Reset"), false) { ForeColor = UIColors.TextWarn };

            btnOk.Click += (s, e) => { ApplySettings(); this.DialogResult = DialogResult.OK; this.Close(); };
            btnCancel.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; this.Close(); };
            btnApply.Click += (s, e) => { ApplySettings(); };
            
            btnReset.Click += (s, e) => 
            {
                if (MessageBox.Show(LanguageManager.T("Menu.ResetConfirm"), LanguageManager.T("Menu.Reset"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    try 
                    {
                        Settings.GlobalBlockSave = true;
                        var path = Path.Combine(AppContext.BaseDirectory, "settings.json");
                        if (File.Exists(path)) File.Delete(path);
                        Application.Restart();
                        Environment.Exit(0);
                    }
                    catch (Exception ex) { Settings.GlobalBlockSave = false; MessageBox.Show(ex.Message); }
                }
            };

            flowBtns.Controls.Add(btnOk); flowBtns.Controls.Add(btnCancel); flowBtns.Controls.Add(btnApply); flowBtns.Controls.Add(btnReset);
            pnlBottom.Controls.Add(flowBtns);
            this.Controls.Add(pnlBottom);

            // 内容区 - 使用 LiteUI.cs 中定义的 BufferedPanel
            _pnlContent = new BufferedPanel { Dock = DockStyle.Fill, Padding = new Padding(0) };
            this.Controls.Add(_pnlContent);
            
            pnlSidebar.BringToFront(); 
            pnlBottom.SendToBack(); 
            _pnlContent.BringToFront();
        }

        private void InitPages()
        {
            _pnlNavContainer.Controls.Clear();
            _pages.Clear();
            
            // 注册所有页面
            AddNav("MainPanel", "🖥️ " + LanguageManager.T("Menu.MainFormSettings"), new MainPanelPage());
            AddNav("Taskbar", "➖ " + LanguageManager.T("Menu.TaskbarSettings"), new TaskbarPage());
            AddNav("Monitor", "📊 " + LanguageManager.T("Menu.MonitorItemDisplay"), new MonitorPage());
            AddNav("Threshold", "🔔 " + LanguageManager.T("Menu.Thresholds"), new ThresholdPage());
            AddNav("System", "⚙️ " + LanguageManager.T("Menu.SystemHardwar"), new SystemHardwarPage());
            AddNav("Plugins", "🧩 " + LanguageManager.T("Menu.Plugins"), new PluginPage());

            _pnlNavContainer.PerformLayout();
            SwitchPage("MainPanel");
        }

        private void AddNav(string key, string text, SettingsPageBase page)
        {
            page.SetContext(_cfg, _mainForm, _ui);
            _pages[key] = page;
            var btn = new LiteNavBtn(text) { Tag = key };
            btn.Click += (s, e) => SwitchPage(key);
            _pnlNavContainer.Controls.Add(btn);
        }

        public void SwitchPage(string key)
        {
            if (_currentKey == key) return;
            _currentKey = key;

            // 更新导航按钮状态
            _pnlNavContainer.SuspendLayout();
            foreach (Control c in _pnlNavContainer.Controls)
                if (c is LiteNavBtn b) b.IsActive = ((string)b.Tag == key);
            _pnlNavContainer.ResumeLayout();
            _pnlNavContainer.Refresh(); 
            Application.DoEvents();

            if (_pages.ContainsKey(key))
            {
                // ★★★ 核心修复开始 ★★★
                
                // 1. 挂起父容器布局：告诉系统“在我操作完之前，千万不要重绘”
                _pnlContent.SuspendLayout(); 
                
                try 
                {
                    _pnlContent.Controls.Clear();
                    var targetPage = _pages[key];
                    
                    // 2. 关键技：手动预设尺寸
                    // 在 Dock 生效前，先强制把它设为和父容器一样大。
                    // 这样即使 Layout 有微小延迟，肉眼看到的也是填满的状态。
                    targetPage.Size = _pnlContent.ClientSize; 
                    targetPage.Dock = DockStyle.Fill; // 双保险

                    targetPage.OnShow();
                    _pnlContent.Controls.Add(targetPage);
                }
                finally
                {
                    // 3. 恢复布局：此时控件大小已正确，系统一次性绘制最终画面
                    _pnlContent.ResumeLayout(); 
                }
                // ★★★ 核心修复结束 ★★★
            }
        }

        private void ApplySettings()
        {
            // 保存逻辑顺序优化
            foreach (var kv in _pages) 
            {
                if (kv.Key != "Monitor") kv.Value.Save(); 
            }
            
            if (_pages.ContainsKey("Monitor")) 
            {
                _pages["Monitor"].Save();
            }
            
            _cfg.Save();
            AppActions.ApplyAllSettings(_cfg, _mainForm, _ui);
        }
    }
}