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
        private Panel _pnlContent;
        private Dictionary<string, SettingsPageBase> _pages = new Dictionary<string, SettingsPageBase>();
        private SettingsPageBase _currentPage;
        private string _currentKey = "";

        public SettingsForm() { InitializeComponent(); }
        public SettingsForm(Settings cfg, UIController ui, MainForm mainForm) : this() { _cfg = cfg; _ui = ui; _mainForm = mainForm; InitPages(); }

        private void InitializeComponent()
        {
            // ★★★ DPI 修复：计算缩放系数并应用 ★★★
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

            // === 1. 侧边栏 ===
            // ★★★ 修改：宽度缩放
            var pnlSidebar = new Panel { Dock = DockStyle.Left, Width = UIUtils.S(160), BackColor = UIColors.SidebarBg };
            
            
            _pnlNavContainer = new FlowLayoutPanel 
            { 
                Dock = DockStyle.Fill, 
                FlowDirection = FlowDirection.TopDown, 
                WrapContents = false, 
                // ★★★ 修改：Padding 缩放
                Padding = UIUtils.S(new Padding(0, 20, 0, 0)),
                BackColor = UIColors.SidebarBg
            };
            
            var line = new Panel { Dock = DockStyle.Right, Width = 1, BackColor = UIColors.Border };
            pnlSidebar.Controls.Add(_pnlNavContainer);
            pnlSidebar.Controls.Add(line);
            this.Controls.Add(pnlSidebar);

            // === 2. 底部按钮 ===
            // ★★★ 修改：高度缩放
            var pnlBottom = new Panel { Dock = DockStyle.Bottom, Height = UIUtils.S(60), BackColor = UIColors.MainBg };
            pnlBottom.Paint += (s, e) => e.Graphics.DrawLine(new Pen(UIColors.Border), 0, 0, Width, 0);

            var flowBtns = new FlowLayoutPanel 
            { 
                Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, 
                // ★★★ 修改：Padding 缩放
                Padding = UIUtils.S(new Padding(0, 14, 20, 0)), WrapContents = false, BackColor = Color.Transparent 
            };
            
            var btnOk = new LiteButton(LanguageManager.T("Menu.OK"), true);
            var btnCancel = new LiteButton(LanguageManager.T("Menu.Cancel"), false);
            var btnApply = new LiteButton(LanguageManager.T("Menu.Apply"), false);

            // [需求1] 增加恢复默认设置按钮
            var btnReset = new LiteButton(LanguageManager.T("Menu.Reset"), false);
            btnReset.ForeColor = UIColors.TextWarn; // 使用警告色提示

            btnOk.Click += (s, e) => { ApplySettings(); this.DialogResult = DialogResult.OK; this.Close(); };
            btnCancel.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; this.Close(); };
            btnApply.Click += (s, e) => { ApplySettings(); };
            
            // [需求1] 恢复默认逻辑 - 修复版
            btnReset.Click += (s, e) => 
            {
                if (MessageBox.Show(LanguageManager.T("Menu.ResetConfirm"), LanguageManager.T("Menu.Reset"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    try 
                    {
                        // ★★★ 核心修复：开启全局保存锁 ★★★
                        // 阻止程序退出时自动保存旧配置
                        Settings.GlobalBlockSave = true;
                        
                        var path = Path.Combine(AppContext.BaseDirectory, "settings.json");
                        if (File.Exists(path)) File.Delete(path);
                        
                        Application.Restart();
                        Environment.Exit(0);
                    }
                    catch (Exception ex) 
                    {
                        // 如果重置失败（例如文件占用），必须解开锁，否则用户后续无法正常保存
                        Settings.GlobalBlockSave = false; 
                        MessageBox.Show("Error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            };

            flowBtns.Controls.Add(btnOk); flowBtns.Controls.Add(btnCancel); flowBtns.Controls.Add(btnApply); flowBtns.Controls.Add(btnReset);
            pnlBottom.Controls.Add(flowBtns);
            this.Controls.Add(pnlBottom);

            // === 3. 内容区 ===
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
            //AddNav("General", "基础设置", new SettingsPageBase()); // 占位
            // 在 InitPages() 中
            
            // 1. 主面板 (Main Panel) -> 🖥️
            AddNav("MainPanel", "🖥️ " + LanguageManager.T("Menu.MainFormSettings"), new MainPanelPage());
            
            // 2. 任务栏 (Taskbar) -> 📌 (或者 ➖)
            AddNav("Taskbar", "➖ " + LanguageManager.T("Menu.TaskbarSettings"), new TaskbarPage());

            // 3. 监控项 (Monitor Items) -> 📊
            AddNav("Monitor", "📊 " + LanguageManager.T("Menu.MonitorItemDisplay"), new MonitorPage());
            
            // 4. 告警阈值 (Thresholds) -> 🔔
            AddNav("Threshold", "🔔 " + LanguageManager.T("Menu.Thresholds"), new ThresholdPage());

            // 5. 系统硬件 (System) -> ⚙️
            AddNav("System", "⚙️ " + LanguageManager.T("Menu.SystemHardwar"), new SystemHardwarPage());
            
            // 6. 插件管理 (Plugins) -> 🧩
            AddNav("Plugins", LanguageManager.T("Menu.Plugins"), new PluginPage());

            // 强制刷新一次布局，防止按钮不可见
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

            // 更新侧边栏
            _pnlNavContainer.SuspendLayout();
            foreach (Control c in _pnlNavContainer.Controls)
                if (c is LiteNavBtn b) b.IsActive = ((string)b.Tag == key);
            _pnlNavContainer.ResumeLayout();
            _pnlNavContainer.Refresh(); 
            Application.DoEvents();

            // 更新内容
            if (_pages.ContainsKey(key))
            {
                // ★★★ 核心修复开始 ★★★
                
                // 1. 挂起父容器布局：告诉系统“在我操作完之前，千万不要重绘”
                _pnlContent.SuspendLayout(); 
                
                try 
                {
                    _pnlContent.Controls.Clear();
                    _currentPage = _pages[key];
                    
                    // 2. 关键技：手动预设尺寸
                    // 在 Dock 生效前，先强制把它设为和父容器一样大。
                    // 这样即使 Layout 有微小延迟，肉眼看到的也是填满的状态。
                    _currentPage.Size = _pnlContent.ClientSize; 
                    _currentPage.Dock = DockStyle.Fill; // 双保险

                    _currentPage.OnShow();
                    _pnlContent.Controls.Add(_currentPage);
                }
                finally
                {
                    // 3. 恢复布局：此时控件大小已正确，系统一次性绘制最终画面
                    _pnlContent.ResumeLayout(); 
                }
                // ★★★ 核心修复结束 ★★★
            }
        }

        // ★★★ 极致瘦身后的 ApplySettings ★★★
        private void ApplySettings()
        {
            // 1. 【保存阶段】让所有页面把 UI 数据写回 Config 对象
            // (SettingsPageBase.Save 会自动执行所有 Bind 的 setter)
            foreach (var page in _pages.Values) 
            {
                page.Save(); 
            }
            
            // 2. 【持久化阶段】写入 JSON 文件
            _cfg.Save();

            // 3. 【应用阶段】统一触发全局刷新
            // 此时 Config 对象已是最新，AppActions 读取它并生效
            AppActions.ApplyAllSettings(_cfg, _mainForm, _ui);
        }
    }
}