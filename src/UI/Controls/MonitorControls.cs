using System;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.UI.Controls
{
    public static class MonitorLayout
    {
        public static readonly int H_ROW = UIUtils.S(44);
        
        public static readonly int X_COL1 = UIUtils.S(20);  
        public static readonly int X_COL2 = UIUtils.S(140); 
        // [需求4] 显示列右移到 380
        public static readonly int X_COL3 = UIUtils.S(380); 
        // [需求4] 排序列相应右移到 520
        public static readonly int X_COL4 = UIUtils.S(500); 

        // 兼容
        public static readonly int X_ID = X_COL1;
        public static readonly int X_NAME = X_COL2;
        public static readonly int X_SWITCH = X_COL3;
        public static readonly int X_SORT = X_COL4;
    }

    public class MonitorItemRow : Panel
    {
        public MonitorItemConfig Config { get; private set; }

        private Label _lblId;           
        private Label _lblName;         
        
        private LiteUnderlineInput _inputName;  
        private LiteUnderlineInput _inputShort; 
        
        private LiteCheck _chkPanel;            
        private LiteCheck _chkTaskbar;          
        
        private LiteSortBtn _btnUp;
        private LiteSortBtn _btnDown;

        public event EventHandler MoveUp;
        public event EventHandler MoveDown;

        public MonitorItemRow(MonitorItemConfig item)
        {
            this.Config = item;
            this.Dock = DockStyle.Top;
            this.Height = MonitorLayout.H_ROW;
            this.BackColor = Color.White;

            // 1. ID Label (主界面模式)
            _lblId = new Label
            {
                Text = item.Key,
                Location = new Point(MonitorLayout.X_COL1, UIUtils.S(14)),
                Size = new Size(UIUtils.S(110), UIUtils.S(20)),
                AutoEllipsis = true,
                ForeColor = UIColors.TextSub,
                Font = UIFonts.Regular(8F)
            };

            // 2. Name Label (任务栏模式)
            string defName = LanguageManager.T(UIUtils.Intern("Items." + item.Key));
            string valName = string.IsNullOrEmpty(item.UserLabel) ? defName : item.UserLabel;
            _lblName = new Label
            {
                Text = valName, 
                Location = new Point(MonitorLayout.X_COL1, UIUtils.S(14)),
                Size = new Size(UIUtils.S(110), UIUtils.S(20)),
                AutoEllipsis = true,
                // [需求2] 字体样式与主界面Tab(_lblId)保持一致
                ForeColor = UIColors.TextSub, 
                Font = UIFonts.Regular(8F),
                Visible = false 
            };

            // 3. Name Input
            _inputName = new LiteUnderlineInput(valName, "", "", 140, UIColors.TextMain) 
            { Location = new Point(MonitorLayout.X_COL2, UIUtils.S(8)) };

            // 4. Short Input
            string defShortKey = UIUtils.Intern("Short." + item.Key);
            string defShort = LanguageManager.T(defShortKey);
            if (defShort.StartsWith("Short.")) defShort = item.Key.Split('.')[1]; 
            string valShort = string.IsNullOrEmpty(item.TaskbarLabel) ? defShort : item.TaskbarLabel;
            
            _inputShort = new LiteUnderlineInput(valShort, "", "", 80, UIColors.TextMain) 
            { Location = new Point(MonitorLayout.X_COL2, UIUtils.S(8)), Visible = false };

            // 5. Checkboxes
            _chkPanel = new LiteCheck(item.VisibleInPanel, LanguageManager.T("Menu.MainForm")) 
            { Location = new Point(MonitorLayout.X_COL3, UIUtils.S(10)) };
            
            _chkTaskbar = new LiteCheck(item.VisibleInTaskbar, LanguageManager.T("Menu.Taskbar")) 
            { Location = new Point(MonitorLayout.X_COL3, UIUtils.S(10)), Visible = false };

            // 6. Sort Buttons
            _btnUp = new LiteSortBtn("▲") { Location = new Point(MonitorLayout.X_COL4, UIUtils.S(10)) };
            _btnDown = new LiteSortBtn("▼") { Location = new Point(MonitorLayout.X_COL4 + UIUtils.S(36), UIUtils.S(10)) };
            
            _btnUp.Click += (s, e) => MoveUp?.Invoke(this, EventArgs.Empty);
            _btnDown.Click += (s, e) => MoveDown?.Invoke(this, EventArgs.Empty);

            this.Controls.AddRange(new Control[] { 
                _lblId, _lblName, 
                _inputName, _inputShort, 
                _chkPanel, _chkTaskbar, 
                _btnUp, _btnDown 
            });
        }

        // [需求2] 暴露方法供组开关调用
        public void SetPanelChecked(bool check) 
        {
            _chkPanel.Checked = check;
        }

        public void SetMode(bool isTaskbarMode)
        {
            if (isTaskbarMode)
            {
                _lblId.Visible = false;
                _lblName.Visible = true; 

                _inputName.Visible = false;
                _inputShort.Visible = true;

                _chkPanel.Visible = false;
                _chkTaskbar.Visible = true;
            }
            else
            {
                _lblId.Visible = true;
                _lblName.Visible = false;

                _inputName.Visible = true;
                _inputShort.Visible = false;

                _chkPanel.Visible = true;
                _chkTaskbar.Visible = false;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(UIColors.Border))
                e.Graphics.DrawLine(p, MonitorLayout.X_COL1, Height - 1, Width - UIUtils.S(20), Height - 1);
        }

        public void SyncToConfig()
        {
            string valName = _inputName.Inner.Text.Trim();
            string originalName = LanguageManager.GetOriginal(UIUtils.Intern("Items." + Config.Key));
            Config.UserLabel = string.Equals(valName, originalName, StringComparison.OrdinalIgnoreCase) ? "" : valName;

            string valShort = _inputShort.Inner.Text.Trim();
            string originalShort = LanguageManager.GetOriginal(UIUtils.Intern("Short." + Config.Key));
            Config.TaskbarLabel = string.Equals(valShort, originalShort, StringComparison.OrdinalIgnoreCase) ? "" : valShort;

            Config.VisibleInPanel = _chkPanel.Checked;
            Config.VisibleInTaskbar = _chkTaskbar.Checked;
        }
    }

    public class MonitorGroupHeader : Panel
    {
        public string GroupKey { get; private set; }
        public LiteUnderlineInput InputAlias { get; private set; }
        private LiteCheck _chkAll; // [需求2] 增加组开关
        public event EventHandler MoveUp;
        public event EventHandler MoveDown;
        public event EventHandler<bool> ToggleGroup; // [需求2] 组开关事件

        public MonitorGroupHeader(string groupKey, string alias)
        {
            this.GroupKey = groupKey;
            this.Dock = DockStyle.Top;
            this.Height = UIUtils.S(45);
            this.BackColor = UIColors.GroupHeader; 

            var lblId = new Label { 
                Text = groupKey, 
                Location = new Point(MonitorLayout.X_COL1, UIUtils.S(12)), 
                AutoSize = true, 
                Font = UIFonts.Bold(9F), 
                ForeColor = Color.Gray 
            };

            string defGName = LanguageManager.T("Groups." + groupKey);
            if (defGName.StartsWith("Groups.")) defGName = groupKey;
            
            InputAlias = new LiteUnderlineInput(string.IsNullOrEmpty(alias) ? defGName : alias, "", "", 100) 
            { Location = new Point(MonitorLayout.X_COL2, UIUtils.S(8)) };
            InputAlias.SetBg(UIColors.GroupHeader); 
            InputAlias.Inner.Font = UIFonts.Bold(9F);

            // [需求2] 组开关复选框
            _chkAll = new LiteCheck(true, "") 
            { 
                Location = new Point(MonitorLayout.X_COL3, UIUtils.S(12)),
                // 默认不显示文字，只作为全选/全不选开关
            };
            // 点击事件：触发 ToggleGroup
            _chkAll.Click += (s, e) => ToggleGroup?.Invoke(this, _chkAll.Checked);

            var btnUp = new LiteSortBtn("▲") { Location = new Point(MonitorLayout.X_COL4, UIUtils.S(10)) };
            var btnDown = new LiteSortBtn("▼") { Location = new Point(MonitorLayout.X_COL4 + UIUtils.S(36), UIUtils.S(10)) };
            
            btnUp.Click += (s, e) => MoveUp?.Invoke(this, EventArgs.Empty);
            btnDown.Click += (s, e) => MoveDown?.Invoke(this, EventArgs.Empty);

            this.Controls.AddRange(new Control[] { lblId, InputAlias, _chkAll, btnUp, btnDown });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using(var p = new Pen(UIColors.Border)) 
                e.Graphics.DrawLine(p, 0, Height-1, Width, Height-1);
        }
    }
}