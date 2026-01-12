using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI
{
    public class HardwareInfoForm : Form
    {
        private LiteTreeView _tree;
        private System.Windows.Forms.Timer _refreshTimer;
        private Panel _headerPanel; 

        public HardwareInfoForm()
        {
            this.Text = "Hardware Inspector";
            this.Size = new Size(UIUtils.S(650), UIUtils.S(750)); // 稍微加宽一点
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.White;
            this.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            // 搜索栏
            var pnlToolbar = new Panel { Dock = DockStyle.Top, Height = UIUtils.S(40), Padding = new Padding(10), BackColor = Color.WhiteSmoke };
            var searchInput = new TextBox { 
                Dock = DockStyle.Fill, 
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Microsoft YaHei UI", 9f), 
                PlaceholderText = "Search..." 
            };
            searchInput.TextChanged += (s, e) => RebuildTree(searchInput.Text.Trim());
            pnlToolbar.Controls.Add(searchInput);

            // 表头
            _headerPanel = new Panel { Dock = DockStyle.Top, Height = UIUtils.S(24), BackColor = Color.FromArgb(250, 250, 250) };
            _headerPanel.Paint += HeaderPanel_Paint;
            _headerPanel.Resize += (s, e) => _headerPanel.Invalidate();

            _tree = new LiteTreeView { Dock = DockStyle.Fill };
            
            var cms = new ContextMenuStrip();
            cms.Items.Add("Copy Value", null, (s, e) => CopyInfo("Value"));
            cms.Items.Add("Copy ID", null, (s, e) => CopyInfo("ID"));
            cms.Items.Add(new ToolStripSeparator());
            cms.Items.Add("Expand All", null, (s, e) => _tree.ExpandAll());
            // ★★★ 修改这里：去掉 foreach 循环，只保留 CollapseAll ★★★
            cms.Items.Add("Collapse All", null, (s, e) => {
                _tree.CollapseAll();
                // 删除原来的 foreach(TreeNode n in _tree.Nodes) n.Expand(); 这一行
            });
            _tree.ContextMenuStrip = cms;

            this.Controls.Add(_tree);
            this.Controls.Add(_headerPanel);
            this.Controls.Add(pnlToolbar);

            RebuildTree("");

            // 局部刷新定时器
            _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _refreshTimer.Tick += (s, e) => UpdateVisibleNodesSmart();
            _refreshTimer.Start();
        }

        private void UpdateVisibleNodesSmart()
        {
            if (!this.Visible || _tree.IsDisposed) return;
            TreeNode node = _tree.TopNode;
            while (node != null)
            {
                if (node.Bounds.Top > _tree.ClientSize.Height) break;
                if (node.Tag is ISensor) _tree.InvalidateSensorValue(node);
                node = node.NextVisibleNode;
            }
        }

        private void HeaderPanel_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            
            // 使用 ClientSize 确保不包含边框宽度
            int w = _headerPanel.ClientSize.Width; 

            // 1. 绘制底部分割线
            using (var pen = new Pen(Color.FromArgb(230, 230, 230)))
                g.DrawLine(pen, 0, _headerPanel.Height - 1, w, _headerPanel.Height - 1);

            var font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold); 
            
            // --- 坐标计算 (从右向左推，基准必须与 LiteTreeView 完全一致) ---
            // 布局逻辑: [窗口右边] - [右边距] - [图标占位] - [间距] - [Max列] - [间距] - [Value列]
            
            int rightMargin = UIUtils.S(_tree.RightMargin);
            int iconWidth = UIUtils.S(_tree.IconWidth);
            int colMaxW = UIUtils.S(_tree.ColMaxWidth);
            int colValW = UIUtils.S(_tree.ColValueWidth);
            int gap = UIUtils.S(10); // 列之间的间距

            // 计算各列的 X 坐标 (Left)
            int xIconLeft = w - rightMargin - iconWidth;
            int xMaxLeft = xIconLeft - gap - colMaxW-20;
            int xValueLeft = xMaxLeft - gap - colValW;

            // --- 绘制文本 ---
            // 关键修复：添加 SingleLine | EndEllipsis 防止文字乱码换行

            // 2. 绘制 "Sensor" (左侧)
            // 使用 Rectangle 而不是 Point，并垂直居中，防止位置跑偏
            Rectangle titleRect = new Rectangle(10, 0, xValueLeft - 10, _headerPanel.Height);
            TextRenderer.DrawText(g, " Sensor", font, titleRect, Color.FromArgb(80, 80, 80), 
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            // 3. 绘制 "Max"
            Rectangle maxRect = new Rectangle(xMaxLeft, 0, colMaxW, _headerPanel.Height);
            TextRenderer.DrawText(g, "Max", font, maxRect, Color.FromArgb(80, 80, 80), 
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine);

            // 4. 绘制 "Value"
            Rectangle valRect = new Rectangle(xValueLeft, 0, colValW, _headerPanel.Height);
            TextRenderer.DrawText(g, "Value", font, valRect, Color.FromArgb(80, 80, 80), 
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine);
            
            font.Dispose();
        }

        private void RebuildTree(string filter)
        {
            _tree.BeginUpdate();
            _tree.Nodes.Clear();

            var computer = HardwareMonitor.Instance?.ComputerInstance;
            if (computer == null || computer.Hardware.Count == 0) 
            {
                _tree.Nodes.Add(new TreeNode("Initializing..."));
                _tree.EndUpdate();
                return;
            }

            foreach (var hw in computer.Hardware)
            {
                AddHardwareNode(_tree.Nodes, hw, filter, !string.IsNullOrEmpty(filter));
            }
            _tree.EndUpdate();
        }

        private void AddHardwareNode(TreeNodeCollection parentNodes, IHardware hw, string filter, bool isSearch)
        {
            string typeStr = GetHardwareTypeString(hw.HardwareType);
            string icon = GetHardwareIcon(hw.HardwareType);
            string label = $"{icon} {typeStr} {hw.Name}";

            var hwNode = new TreeNode(label) { Tag = hw };
            bool hasContent = false;

            var groups = hw.Sensors.GroupBy(s => s.SensorType).OrderBy(g => g.Key);
            foreach (var group in groups)
            {
                string typeIcon = GetSensorTypeIcon(group.Key);
                string typeName = $"{typeIcon} {group.Key}"; 
                var typeNode = new TreeNode(typeName); 

                bool groupHasMatch = false;
                foreach (var s in group)
                {
                    if (isSearch && !s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) && !hw.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    typeNode.Nodes.Add(new TreeNode(s.Name) { Tag = s });
                    groupHasMatch = true;
                }

                if (groupHasMatch)
                {
                    hwNode.Nodes.Add(typeNode);
                    if (isSearch) typeNode.Expand(); 
                    hasContent = true;
                }
            }

            foreach (var subHw in hw.SubHardware)
            {
                AddHardwareNode(hwNode.Nodes, subHw, filter, isSearch);
            }
            if (hwNode.Nodes.Count > 0) hasContent = true;

            if (!isSearch || hasContent || hw.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                parentNodes.Add(hwNode);
                
                // ★★★ 默认行为调整 ★★★
                if (isSearch)
                {
                    hwNode.Expand(); // 搜索时全展开
                }
                else
                {
                    // 普通模式：只显示硬件层，且全部折叠 (用户要求 "默认全部折叠到只显示 最上层的")
                    // 这里不调用 Expand()，默认就是 Collapse 的
                    // 如果你想让硬件层可见但子项不展开，这样就已经做到了（因为添加到了 parentNodes）
                    // 唯一需要做的是，如果 HardwareNode 是根节点，它默认就是显示的。
                    // 不需要 Expand()。
                }
            }
        }

        private void CopyInfo(string type)
        {
            var node = _tree.SelectedNode;
            if (node?.Tag is ISensor s)
            {
                if (type == "Value") Clipboard.SetText(s.Value?.ToString() ?? "");
                else if (type == "ID") Clipboard.SetText(s.Identifier.ToString());
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            base.OnFormClosed(e);
            this.Dispose();
        }

        private string GetHardwareIcon(HardwareType type)
        {
            switch (type) {
                case HardwareType.Cpu: return "💻"; 
                case HardwareType.GpuNvidia: return "🎮";
                case HardwareType.GpuAmd: return "🎮";
                case HardwareType.GpuIntel: return "🎮";
                case HardwareType.Memory: return "🧠"; 
                case HardwareType.Motherboard: return "🔌"; 
                case HardwareType.Storage: return "💾"; 
                case HardwareType.Network: return "🌐"; 
                default: return "📦";
            }
        }
        private string GetHardwareTypeString(HardwareType type)
        {
            switch (type) {
                case HardwareType.Cpu: return "[处理器]";
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel: return "[显卡]";
                case HardwareType.Memory: return "[内存]";
                case HardwareType.Motherboard: return "[主板]";
                case HardwareType.Storage: return "[硬盘]";
                case HardwareType.Network: return "[网卡]";
                default: return "";
            }
        }
        private string GetSensorTypeIcon(SensorType type)
        {
            switch (type) {
                case SensorType.Temperature: return "🌡️";
                case SensorType.Load: return "📊";
                case SensorType.Fan: return "🌪️";
                case SensorType.Power: return "⚡";
                case SensorType.Clock: return "⏱️";
                case SensorType.Control: return "🎛️";
                case SensorType.Voltage: return "🔋";
                case SensorType.Data: return "🔢";
                default: return "•";
            }
        }
    }
}