using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json; // 引入 JSON 库用于深拷贝
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class MonitorPage : SettingsPageBase
    {
        private Panel _container;
        private bool _isLoaded = false;
        private bool _isTaskbarTab = false;

        private Panel _tabPanel;    
        private Panel _headerPanel; 

        private Button _btnTabMain;
        private Button _btnTabBar;
        private LiteCheck _chkLinkHorizontal;
        private LiteCheck _chkOnlyVisible;
        
        private Label _lblCol1; 
        private Label _lblCol2; 
        // ★★★ [新增] 单位列 Label ★★★
        private Label _lblColUnit;
        private Label _lblCol3; 
        private Label _lblCol4; 

        // 本地工作副本
        private List<MonitorItemConfig> _workingList;

        public MonitorPage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);

            _container = new BufferedPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = UIUtils.S(new Padding(20, 5, 20, 0))
            };
            this.Controls.Add(_container);

            InitHeader();
            this.Controls.SetChildIndex(_container, 0); 
        }

        private void InitHeader()
        {
            _tabPanel = new Panel 
            { 
                Dock = DockStyle.Top, Height = UIUtils.S(42), BackColor = UIColors.MainBg,
                Padding = UIUtils.S(new Padding(20, 0, 20, 0))
            };
            _tabPanel.Paint += (s, e) => {
                using (var p = new Pen(UIColors.Border))
                    e.Graphics.DrawLine(p, 0, _tabPanel.Height - 1, _tabPanel.Width, _tabPanel.Height - 1);
            };

            _btnTabMain = CreateTabButton("🖥️ " + LanguageManager.T("Menu.MainForm"), true);
            _btnTabBar = CreateTabButton("➖ " + LanguageManager.T("Menu.Taskbar") + " / " + LanguageManager.T("Menu.Horizontal"), false);

            _btnTabMain.Click += (s, e) => SwitchTab(false);
            _btnTabBar.Click += (s, e) => SwitchTab(true);

            _btnTabMain.Location = new Point(UIUtils.S(20), UIUtils.S(8));
            _btnTabBar.Location = new Point(_btnTabMain.Right + UIUtils.S(10), UIUtils.S(8));

            _chkLinkHorizontal = new LiteCheck(false, LanguageManager.T("Menu.HorizontalFollowsTaskbar")) 
            {
                AutoSize = true, Visible = false, ForeColor = UIColors.TextSub, Font = UIFonts.Bold(9F)
            };
            
            _chkOnlyVisible = new LiteCheck(false, LanguageManager.T("Menu.OnlyShowEnabled")) 
            {
                AutoSize = true, Visible = false, ForeColor = UIColors.TextSub, Font = UIFonts.Bold(9F)
            };
            
            _chkOnlyVisible.CheckedChanged += (s, e) => 
            {
                // 当复选框改变时，界面上显示的还是改变之前的状态
                // 例如：从 [未勾选] -> [勾选]，UI 显示的是 [全部列表]
                // 所以我们需要用 (!Checked) 也就是 [未勾选/全部模式] 的逻辑来保存当前的 UI 顺序
                SaveToWorkingList(isFilteredOverride: !_chkOnlyVisible.Checked);
                
                ReloadList();
            };
            
            _tabPanel.Resize += (s, e) => {
                _chkLinkHorizontal.Location = new Point(_tabPanel.Width - _chkLinkHorizontal.Width - UIUtils.S(20), UIUtils.S(10));
                _chkOnlyVisible.Location = new Point(_chkLinkHorizontal.Left - _chkOnlyVisible.Width - UIUtils.S(20), UIUtils.S(10));
            };

            _tabPanel.Controls.AddRange(new Control[] { _btnTabMain, _btnTabBar, _chkLinkHorizontal, _chkOnlyVisible });

            _headerPanel = new Panel
            {
                Dock = DockStyle.Top, Height = UIUtils.S(34), BackColor = UIColors.MainBg, 
                Padding = UIUtils.S(new Padding(20, 0, 20, 0))
            };

            _lblCol1 = CreateHeaderLabel(); _lblCol2 = CreateHeaderLabel();
            _lblColUnit = CreateHeaderLabel(); // [新增]
            _lblCol3 = CreateHeaderLabel(); _lblCol4 = CreateHeaderLabel();
            _headerPanel.Controls.AddRange(new Control[] { _lblCol1, _lblCol2, _lblColUnit, _lblCol3, _lblCol4 });

            this.Controls.Add(_headerPanel);
            this.Controls.Add(_tabPanel);
        }

        private Label CreateHeaderLabel() => new Label { AutoSize = true, ForeColor = UIColors.TextSub, Font = UIFonts.Bold(8F), Visible = true };
        
        private Button CreateTabButton(string text, bool active)
        {
            var btn = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = UIFonts.Bold(9F), Padding = new Padding(5, 0, 5, 0) };
            btn.FlatAppearance.BorderSize = 0;
            UpdateBtnStyle(btn, active);
            return btn;
        }

        private void UpdateBtnStyle(Button btn, bool active)
        {
            btn.BackColor = active ? UIColors.Primary : Color.Transparent;
            btn.ForeColor = active ? Color.White : UIColors.TextSub;
        }

        private void SwitchTab(bool toTaskbarMode)
        {
            if (_isTaskbarTab == toTaskbarMode && _isLoaded) return;
            
            // 切换 Tab 时保存当前状态
            if (_isLoaded) SaveToWorkingList(); 

            _isTaskbarTab = toTaskbarMode;

            UpdateBtnStyle(_btnTabMain, !_isTaskbarTab);
            UpdateBtnStyle(_btnTabBar, _isTaskbarTab);
            
            _chkLinkHorizontal.Visible = _isTaskbarTab;
            _chkOnlyVisible.Visible = _isTaskbarTab;
            
            if (_isTaskbarTab && Config != null)
                _chkLinkHorizontal.Checked = Config.HorizontalFollowsTaskbar;

            _tabPanel.Width++; _tabPanel.Width--; 
            
            ReloadList();
        }

        public override void OnShow()
        {
            base.OnShow();
            if (Config == null) return;

            // ★★★ Fix: Always reload working list from Config to get latest changes (e.g. newly added plugins) ★★★
            // The previous logic (if _workingList == null) prevented new items from showing up
            // because _workingList was cached and didn't know about the new plugin items added in PluginPage.
            try
            {
                var json = JsonSerializer.Serialize(Config.MonitorItems);
                _workingList = JsonSerializer.Deserialize<List<MonitorItemConfig>>(json) ?? new List<MonitorItemConfig>();
            }
            catch
            {
                _workingList = new List<MonitorItemConfig>();
            }

            // Always refresh the list UI
            // ★★★ 性能优化与崩溃修复 ★★★
            // 如果 _container 包含大量控件，频繁 Dispose 会很慢甚至崩溃
            // 我们可以尝试仅 Diff 更新，或者确保 Dispose 安全
            // 目前先保持全量刷新，但确保在主线程安全操作
            if (this.IsHandleCreated)
            {
                 this.Invoke((MethodInvoker)delegate { ReloadList(); });
            }
            else
            {
                 ReloadList(); 
            }
            
            _isLoaded = true;
        }

        private void ReloadList()
        {
            _container.SuspendLayout();
            
            // 刷新语言缓存
            UpdateLanguageCacheFromWorkingList();

            // 清理旧控件 
            while (_container.Controls.Count > 0)
            {
                var control = _container.Controls[0];
                if (control is GroupBlock block)
                {
                    block.Header.MoveUp -= GroupHeader_MoveUp; block.Header.MoveDown -= GroupHeader_MoveDown;
                    foreach (var row in block.RowsPanel.Controls.OfType<MonitorItemRow>()) { row.MoveUp -= Row_MoveUp; row.MoveDown -= Row_MoveDown; }
                }
                else if (control is MonitorItemRow row)
                {
                    row.MoveUp -= Row_MoveUp; row.MoveDown -= Row_MoveDown;
                }
                control.Dispose();
            }

            UpdateHeaderLayout();

            var spacer = new Panel { Dock = DockStyle.Top, Height = UIUtils.S(30), BackColor = Color.Transparent };
            _container.Controls.Add(spacer);

            if (_isTaskbarTab)
            {
                var items = _workingList.OrderBy(x => x.TaskbarSortIndex).ToList();
                if (_chkOnlyVisible.Checked)
                    items = items.Where(x => x.VisibleInTaskbar).ToList();

                for (int i = items.Count - 1; i >= 0; i--)
                {
                    var row = new MonitorItemRow(items[i], true);
                    row.MoveUp += Row_MoveUp; row.MoveDown += Row_MoveDown;
                    _container.Controls.Add(row);
                }
            }
            else
            {
                var items = _workingList.OrderBy(x => x.SortIndex).ToList();
                var groups = items.GroupBy(x => x.UIGroup);
                
                foreach (var g in groups.Reverse())
                {
                    var block = CreateGroupBlock(g.Key, g.ToList()); // 需要修改 CreateGroupBlock
                    _container.Controls.Add(block);
                }
            }

            _container.ResumeLayout();
            _isLoaded = true;
        }
        // [新增] 临时将工作列表中的文本应用到语言管理器，以实现所见即所得
        private void UpdateLanguageCacheFromWorkingList()
        {
            if (_workingList == null) return;
            
            LanguageManager.ClearOverrides();
            if (Config.GroupAliases != null)
            {
                foreach (var kv in Config.GroupAliases)
                    LanguageManager.SetOverride(UIUtils.Intern("Groups." + kv.Key), kv.Value);
            }
            
            foreach (var item in _workingList)
            {
                if (!string.IsNullOrEmpty(item.UserLabel))
                    LanguageManager.SetOverride(UIUtils.Intern("Items." + item.Key), item.UserLabel);
                if (!string.IsNullOrEmpty(item.TaskbarLabel))
                    LanguageManager.SetOverride(UIUtils.Intern("Short." + item.Key), item.TaskbarLabel);
            }
        }

        private void SyncUIToWorkingList()
        {
            foreach (Control c in _container.Controls)
            {
                if (c is MonitorItemRow row) row.SyncToConfig();
                else if (c is GroupBlock block)
                {
                    foreach (Control rc in block.RowsPanel.Controls)
                        if (rc is MonitorItemRow r) r.SyncToConfig();
                }
            }
        }

        private void UpdateHeaderLayout()
        {
            int y = UIUtils.S(10); 
            int offset = UIUtils.S(20); 

            _lblCol1.Text = LanguageManager.T("Menu.MonitorItem");
            _lblCol1.Location = new Point(MonitorLayout.X_COL1 + offset, y);

            if (_isTaskbarTab) _lblCol2.Text = LanguageManager.T("Menu.short"); 
            else _lblCol2.Text = LanguageManager.T("Menu.name");  
            _lblCol2.Location = new Point(MonitorLayout.X_COL2 + offset, y);

            // ★★★ [新增] Unit Header ★★★
            _lblColUnit.Text = LanguageManager.T("Menu.Unit"); // 可以放入 LanguageManager
            _lblColUnit.Location = new Point(MonitorLayout.X_COL_UNIT + offset, y);

            _lblCol3.Text = LanguageManager.T("Menu.showHide"); 
            _lblCol3.Location = new Point(MonitorLayout.X_COL3 + offset, y);

            _lblCol4.Text = LanguageManager.T("Menu.sort");
            _lblCol4.Location = new Point(MonitorLayout.X_COL4 + offset, y);
        }

        private GroupBlock CreateGroupBlock(string groupKey, List<MonitorItemConfig> items)
        {
            string alias = Config.GroupAliases.ContainsKey(groupKey) ? Config.GroupAliases[groupKey] : "";
            var header = new MonitorGroupHeader(groupKey, alias);
            var rowsPanel = new Panel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.White };
            var block = new GroupBlock(header, rowsPanel);

            header.MoveUp += GroupHeader_MoveUp;
            header.MoveDown += GroupHeader_MoveDown;
            header.ToggleGroup += (s, checkState) => 
            {
                foreach(MonitorItemRow row in rowsPanel.Controls) row.SetPanelChecked(checkState);
            };
            header.Tag = block;

            for (int i = items.Count - 1; i >= 0; i--)
            {
                var row = new MonitorItemRow(items[i], false);
                row.MoveUp += Row_MoveUp; row.MoveDown += Row_MoveDown;
                rowsPanel.Controls.Add(row);
            }
            return block;
        }

        private void GroupHeader_MoveUp(object sender, EventArgs e) { if (sender is MonitorGroupHeader h && h.Tag is GroupBlock b) MoveControl(b, -1); }
        private void GroupHeader_MoveDown(object sender, EventArgs e) { if (sender is MonitorGroupHeader h && h.Tag is GroupBlock b) MoveControl(b, 1); }
        private void Row_MoveUp(object sender, EventArgs e) { if (sender is Control r) MoveControl(r, -1); }
        private void Row_MoveDown(object sender, EventArgs e) { if (sender is Control r) MoveControl(r, 1); }
        
        private void MoveControl(Control c, int dir)
        {
            var p = c.Parent; if (p == null) return;
            int idx = p.Controls.GetChildIndex(c); int newIdx = idx - dir; 
            if (newIdx >= 0 && newIdx < p.Controls.Count) p.Controls.SetChildIndex(c, newIdx);
        }

        // 保存 UI 状态到 _workingList
        private void SaveToWorkingList(bool? isFilteredOverride = null)
        {
            if (_workingList == null) return;

            // 1. 同步属性 (勾选、文本)
            SyncUIToWorkingList();

            if (_isTaskbarTab)
            {
                // 决定当前 UI 应该被视为 "过滤列表" 还是 "全量列表"
                bool isFiltered = isFilteredOverride ?? _chkOnlyVisible.Checked;

                if (isFiltered)
                {
                    // === 算法：骨架+插队 (处理部分视图排序) ===
                    var uiRows = _container.Controls.Cast<Control>().Reverse().OfType<MonitorItemRow>().ToList();
                    var newVisibleList = uiRows.Select(r => r.Config).ToList();
                    
                    var fullList = _workingList.OrderBy(x => x.TaskbarSortIndex).ToList();
                    var oldVisibleList = fullList.Where(x => newVisibleList.Contains(x)).ToList();

                    // 识别锚点 (未移动的项)
                    var lcs = GetLCS(oldVisibleList, newVisibleList);
                    var anchors = new HashSet<MonitorItemConfig>(lcs);

                    // 构建 "骨架"：移除所有被移动的显示项，保留隐藏项和锚点
                    var backbone = new List<MonitorItemConfig>();
                    foreach (var item in fullList)
                    {
                        if (!newVisibleList.Contains(item) || anchors.Contains(item))
                        {
                            backbone.Add(item);
                        }
                    }

                    // 将 "移动项" 插回骨架
                    int insertCursor = 0;
                    foreach (var item in newVisibleList)
                    {
                        if (anchors.Contains(item))
                        {
                            // 遇到锚点：更新游标到它后面
                            int idx = backbone.IndexOf(item);
                            if (idx >= 0) insertCursor = idx + 1;
                        }
                        else
                        {
                            // 遇到移动项：强行插入当前位置
                            if (insertCursor > backbone.Count) insertCursor = backbone.Count;
                            backbone.Insert(insertCursor, item);
                            insertCursor++;
                        }
                    }

                    // 更新索引
                    for (int i = 0; i < backbone.Count; i++) backbone[i].TaskbarSortIndex = i;
                }
                else
                {
                    // === 算法：全量覆盖 (处理全部视图排序) ===
                    // 直接按照 UI 顺序重置所有索引
                    var uiRows = _container.Controls.Cast<Control>().Reverse().OfType<MonitorItemRow>().ToList();
                    for(int i=0; i<uiRows.Count; i++) uiRows[i].Config.TaskbarSortIndex = i;
                }
            }
            else
            {
                // 主面板排序逻辑
                int idx = 0;
                var blocks = _container.Controls.Cast<Control>().Reverse().OfType<GroupBlock>();
                foreach (var block in blocks)
                {
                    string alias = block.Header.InputAlias.Inner.Text.Trim();
                    string defName = LanguageManager.T("Groups." + block.Header.GroupKey);
                    if (!string.IsNullOrEmpty(alias) && alias != defName) 
                        Config.GroupAliases[block.Header.GroupKey] = alias;
                    else 
                        Config.GroupAliases.Remove(block.Header.GroupKey);

                    foreach (MonitorItemRow row in block.RowsPanel.Controls.Cast<Control>().Reverse())
                        row.Config.SortIndex = idx++;
                }
            }
        }

        public override void Save()
        {
            if (!_isLoaded || Config == null || _workingList == null) return;
            
            Config.HorizontalFollowsTaskbar = _chkLinkHorizontal.Checked;
            
            // 执行最后的保存计算
            SaveToWorkingList();

            // 提交副本到全局配置
            Config.MonitorItems = new List<MonitorItemConfig>(_workingList);
            Config.SyncToLanguage();
        }

        private List<MonitorItemConfig> GetLCS(List<MonitorItemConfig> list1, List<MonitorItemConfig> list2)
        {
            int n = list1.Count; int m = list2.Count;
            int[,] dp = new int[n + 1, m + 1];
            for (int i = 1; i <= n; i++) {
                for (int j = 1; j <= m; j++) {
                    if (list1[i - 1] == list2[j - 1]) dp[i, j] = dp[i - 1, j - 1] + 1;
                    else dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                }
            }
            var lcs = new List<MonitorItemConfig>();
            int x = n, y = m;
            while (x > 0 && y > 0) {
                if (list1[x - 1] == list2[y - 1]) { lcs.Add(list1[x - 1]); x--; y--; }
                else if (dp[x - 1, y] > dp[x, y - 1]) x--; else y--;
            }
            lcs.Reverse();
            return lcs;
        }

        private class GroupBlock : Panel
        {
            public MonitorGroupHeader Header { get; }
            public Panel RowsPanel { get; }
            public GroupBlock(MonitorGroupHeader header, Panel rowsPanel)
            {
                Header = header; RowsPanel = rowsPanel;
                Dock = DockStyle.Top; AutoSize = true;
                Padding = UIUtils.S(new Padding(0, 0, 0, 20));
                var card = new LiteCard { Dock = DockStyle.Top };
                card.Controls.Add(rowsPanel); card.Controls.Add(header);
                Controls.Add(card);
            }
        }
    }
}