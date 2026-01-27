using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;
using LiteMonitor.src.Core.Actions;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class MonitorPage : SettingsPageBase
    {
        private Panel _container;
        private bool _isLoaded = false;
        private bool _isTaskbarTab = false;
        private string _lastDataSignature = ""; // ★★★ 性能优化：数据指纹

        private Panel _tabPanel;    
        private Panel _headerPanel; 

        private Button _btnTabMain;
        private Button _btnTabBar;
        private LiteCheck _chkLinkHorizontal;
        private LiteCheck _chkOnlyVisible;
        
        private Label _lblCol1; 
        private Label _lblCol2; 
        private Label _lblColUnit;
        private Label _lblCol3; 
        private Label _lblCol4; 

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
            
            // Immediate binding to avoid conflict with MainPanelPage
            _chkLinkHorizontal.CheckedChanged += (s, e) => {
                if (Config != null) Config.HorizontalFollowsTaskbar = _chkLinkHorizontal.Checked;
            };
            
            _chkOnlyVisible = new LiteCheck(false, LanguageManager.T("Menu.OnlyShowEnabled")) 
            {
                AutoSize = true, Visible = false, ForeColor = UIColors.TextSub, Font = UIFonts.Bold(9F)
            };
            
            _chkOnlyVisible.CheckedChanged += (s, e) => 
            {
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
            _lblColUnit = CreateHeaderLabel(); 
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
            
            if (_isLoaded) SaveToWorkingList(); 

            _isTaskbarTab = toTaskbarMode;

            UpdateBtnStyle(_btnTabMain, !_isTaskbarTab);
            UpdateBtnStyle(_btnTabBar, _isTaskbarTab);
            
            _chkLinkHorizontal.Visible = _isTaskbarTab;
            _chkOnlyVisible.Visible = _isTaskbarTab;
            
            if (_isTaskbarTab && Config != null)
                _chkLinkHorizontal.Checked = Config.HorizontalFollowsTaskbar;

            _tabPanel.Width++; _tabPanel.Width--; 
            
            // 切换 Tab 时必须刷新，因为显示模式变了
            ReloadList();
        }

        // ★★★ 性能优化核心：按需加载 ★★★
        public override void OnShow()
        {
            base.OnShow();
            if (Config == null) return;

            // Sync Checkbox if in taskbar tab (in case it was changed in MainPanelPage)
            if (_isTaskbarTab)
                _chkLinkHorizontal.Checked = Config.HorizontalFollowsTaskbar;

            // 1. 计算数据指纹 (Data Signature)
            // 只要 Config.MonitorItems 的内容（Keys的组合）没变，我们就不需要重新加载
            string currentSig = GenerateSignature();

            // 2. 脏检查：如果已加载且指纹未变，直接返回！(0ms 耗时)
            if (_isLoaded && currentSig == _lastDataSignature) 
            {
                return; 
            }

            // 3. 数据变了（例如在插件页添加了新项），执行全量加载
            try
            {
                var json = JsonSerializer.Serialize(Config.MonitorItems);
                _workingList = JsonSerializer.Deserialize<List<MonitorItemConfig>>(json) ?? new List<MonitorItemConfig>();

                var liveMap = Config.MonitorItems.ToDictionary(x => x.Key, x => x);
                foreach (var item in _workingList)
                {
                    if (liveMap.TryGetValue(item.Key, out var liveItem))
                    {
                        item.DynamicLabel = liveItem.DynamicLabel;
                        item.DynamicTaskbarLabel = liveItem.DynamicTaskbarLabel;
                    }
                }
            }
            catch
            {
                _workingList = new List<MonitorItemConfig>();
            }

            _lastDataSignature = currentSig;
            ReloadList();
            
            _isLoaded = true;
        }

        private string GenerateSignature()
        {
            if (Config?.MonitorItems == null) return "null";
            
            // [Fix] Include DynamicLabel in signature to detect label updates (Plugin Sync)
            var sb = new System.Text.StringBuilder();
            sb.Append(Config.MonitorItems.Count);
            
            foreach (var item in Config.MonitorItems)
            {
                sb.Append('|');
                sb.Append(item.Key);
                // 必须包含动态属性，因为 JsonIgnore 导致它们不参与序列化比较
                // 且 OnShow 中的手动同步逻辑会被 Signature 检查跳过
                sb.Append(':');
                sb.Append(item.DynamicLabel ?? ""); 
                sb.Append(':');
                sb.Append(item.DynamicTaskbarLabel ?? "");
            }
            return sb.ToString();
        }

        private void ReloadList()
        {
            if (_container == null || _container.IsDisposed) return;
            
            _container.SuspendLayout();
            
            try 
            {
                UpdateLanguageCacheFromWorkingList();

                // 清理旧控件
                for (int i = _container.Controls.Count - 1; i >= 0; i--)
                {
                    var control = _container.Controls[i];
                    if (control is GroupBlock block)
                    {
                        block.Header.MoveUp -= GroupHeader_MoveUp; block.Header.MoveDown -= GroupHeader_MoveDown;
                        foreach (var row in block.RowsPanel.Controls.OfType<MonitorItemRow>()) { row.MoveUp -= Row_MoveUp; row.MoveDown -= Row_MoveDown; }
                    }
                    else if (control is MonitorItemRow row)
                    {
                        row.MoveUp -= Row_MoveUp; row.MoveDown -= Row_MoveDown;
                    }
                    _container.Controls.RemoveAt(i);
                    control.Dispose();
                }

                UpdateHeaderLayout();

                var controlsToAdd = new List<Control>();
                var spacer = new Panel { Dock = DockStyle.Top, Height = UIUtils.S(30), BackColor = Color.Transparent };
                controlsToAdd.Add(spacer);

                if (_isTaskbarTab)
                {
                    // [Fix] Taskbar sort fallback to Global SortIndex if TaskbarSortIndex is 0 (or equal)
                    var items = _workingList
                        .OrderBy(x => x.TaskbarSortIndex)
                        .ThenBy(x => x.SortIndex)
                        .ToList();

                    if (_chkOnlyVisible.Checked)
                        items = items.Where(x => x.VisibleInTaskbar).ToList();

                    for (int i = items.Count - 1; i >= 0; i--)
                    {
                        var row = new MonitorItemRow(items[i], true);
                        row.MoveUp += Row_MoveUp; row.MoveDown += Row_MoveDown;
                        controlsToAdd.Add(row);
                    }
                }
                else
                {
                    var items = _workingList.OrderBy(x => x.SortIndex).ToList();
                    var groups = items.GroupBy(x => x.UIGroup);
                    
                    foreach (var g in groups.Reverse())
                    {
                        var block = CreateGroupBlock(g.Key, g.ToList());
                        controlsToAdd.Add(block);
                    }
                }
                
                _container.Controls.AddRange(controlsToAdd.ToArray());

            // 底部提示文案
            var bottomLabel = new Label 
            { 
                Text = LanguageManager.T("Menu.MonitorItemTip"), 
                AutoSize = true, 
                ForeColor = UIColors.TextSub, 
                Font = new Font("Microsoft YaHei UI", 8.5F),
                Dock = DockStyle.Bottom,
                Padding = new Padding(0, 25, 20, 10)
            };
            _container.Controls.Add(bottomLabel);
            }
            finally
            {
                _container.ResumeLayout();
            }

            _isLoaded = true;
        }

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
                // [Fix] Use MetricLabelResolver to get the best available label (User > InfoService > Dynamic)
                // This ensures that plugin items show their dynamic labels even in the settings list
                string resolvedLabel = MetricLabelResolver.ResolveLabel(item);
                if (!string.IsNullOrEmpty(resolvedLabel))
                    LanguageManager.SetOverride(UIUtils.Intern("Items." + item.Key), resolvedLabel);

                string resolvedShort = MetricLabelResolver.ResolveShortLabel(item);
                if (!string.IsNullOrEmpty(resolvedShort))
                    LanguageManager.SetOverride(UIUtils.Intern("Short." + item.Key), resolvedShort);
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

            _lblColUnit.Text = LanguageManager.T("Menu.Unit");
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

            var rows = new List<Control>();
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var row = new MonitorItemRow(items[i], false);
                row.MoveUp += Row_MoveUp; row.MoveDown += Row_MoveDown;
                rows.Add(row);
            }
            rowsPanel.Controls.AddRange(rows.ToArray());
            
            return block;
        }

        private void GroupHeader_MoveUp(object sender, EventArgs e) { if (sender is MonitorGroupHeader h && h.Tag is GroupBlock b) MoveControl(b, -1); }
        private void GroupHeader_MoveDown(object sender, EventArgs e) { if (sender is MonitorGroupHeader h && h.Tag is GroupBlock b) MoveControl(b, 1); }
        private void Row_MoveUp(object sender, EventArgs e) { if (sender is Control r) MoveControl(r, -1); }
        private void Row_MoveDown(object sender, EventArgs e) { if (sender is Control r) MoveControl(r, 1); }
        
        private void MoveControl(Control c, int dir)
        {
            var p = c.Parent as ScrollableControl; if (p == null) return;
            
            // [Fix] 防止 WinForms 自动滚动跳动
            p.SuspendLayout();
            var scroll = p.AutoScrollPosition;
            
            int idx = p.Controls.GetChildIndex(c); 
            int newIdx = idx - dir; 
            if (newIdx >= 0 && newIdx < p.Controls.Count) p.Controls.SetChildIndex(c, newIdx);

            p.AutoScrollPosition = new Point(Math.Abs(scroll.X), Math.Abs(scroll.Y));
            p.ResumeLayout(false);
            p.PerformLayout();
        }

        private void SaveToWorkingList(bool? isFilteredOverride = null)
        {
            if (_workingList == null) return;

            SyncUIToWorkingList();

            if (_isTaskbarTab)
            {
                bool isFiltered = isFilteredOverride ?? _chkOnlyVisible.Checked;

                if (isFiltered)
                {
                    var uiRows = _container.Controls.Cast<Control>().Reverse().OfType<MonitorItemRow>().ToList();
                    var newVisibleList = uiRows.Select(r => r.Config).ToList();
                    
                    var fullList = _workingList.OrderBy(x => x.TaskbarSortIndex).ToList();
                    var oldVisibleList = fullList.Where(x => newVisibleList.Contains(x)).ToList();

                    var lcs = GetLCS(oldVisibleList, newVisibleList);
                    var anchors = new HashSet<MonitorItemConfig>(lcs);

                    var backbone = new List<MonitorItemConfig>();
                    foreach (var item in fullList)
                    {
                        if (!newVisibleList.Contains(item) || anchors.Contains(item))
                        {
                            backbone.Add(item);
                        }
                    }

                    int insertCursor = 0;
                    foreach (var item in newVisibleList)
                    {
                        if (anchors.Contains(item))
                        {
                            int idx = backbone.IndexOf(item);
                            if (idx >= 0) insertCursor = idx + 1;
                        }
                        else
                        {
                            if (insertCursor > backbone.Count) insertCursor = backbone.Count;
                            backbone.Insert(insertCursor, item);
                            insertCursor++;
                        }
                    }

                    for (int i = 0; i < backbone.Count; i++) backbone[i].TaskbarSortIndex = i;
                }
                else
                {
                    var uiRows = _container.Controls.Cast<Control>().Reverse().OfType<MonitorItemRow>().ToList();
                    for(int i=0; i<uiRows.Count; i++) uiRows[i].Config.TaskbarSortIndex = i;
                }
            }
            else
            {
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
            
            SaveToWorkingList();
            
            // Delegate logic to SettingsOperations
            SettingsChanger.UpdateMonitorList(Config, _workingList, _chkLinkHorizontal.Checked);
        }

        private List<MonitorItemConfig> GetLCS(List<MonitorItemConfig> list1, List<MonitorItemConfig> list2)
        {
            if (list1.Count == list2.Count && list1.SequenceEqual(list2))
                return new List<MonitorItemConfig>(list1);

            int n = list1.Count; int m = list2.Count;
            if (n == 0 || m == 0) return new List<MonitorItemConfig>();

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