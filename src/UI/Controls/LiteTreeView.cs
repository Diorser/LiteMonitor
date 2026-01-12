using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.UI.Controls
{
    public class LiteTreeView : TreeView
    {
        private static readonly Brush _selectBgBrush = new SolidBrush(Color.FromArgb(204, 232, 255)); // 选中背景颜色
        private static readonly Brush _hoverBrush = new SolidBrush(Color.FromArgb(250, 250, 250)); // 悬停背景颜色
        private static readonly Pen _linePen = new Pen(Color.FromArgb(240, 240, 240)); // 树线颜色
        private static readonly Brush _chevronBrush = new SolidBrush(Color.Gray); //  Chevron 图标颜色

        private Font _baseFont;
        private Font _boldFont;

        // --- 布局参数 ---
        public int ColValueWidth { get; set; } = 70;  
        public int ColMaxWidth { get; set; } = 70;
        // ★★★ 修正：右边距调小，让内容靠右 ★★★
        public int RightMargin { get; set; } = 6;    
        public int IconWidth { get; set; } = 20;      

        public LiteTreeView()
        {
            this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            this.DrawMode = TreeViewDrawMode.OwnerDrawText; 
            this.ShowLines = false;
            this.ShowPlusMinus = false; 
            this.FullRowSelect = true;
            this.BorderStyle = BorderStyle.None;
            this.BackColor = Color.White;
            this.ItemHeight = UIUtils.S(28); 

            _baseFont = new Font("Microsoft YaHei UI", 9f);
            _boldFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            this.Font = _baseFont;
        }

        // --- 局部刷新区域计算 ---
        public void InvalidateSensorValue(TreeNode node)
        {
            if (node == null || node.Bounds.Height <= 0) return;
            
            // 计算右侧动态区域宽度：Value + Max + Icon + Margin
            int refreshWidth = UIUtils.S(ColValueWidth + ColMaxWidth + IconWidth + RightMargin + 15); 
            int safeWidth = this.ClientSize.Width;

            // 只重绘右侧这一块
            Rectangle dirtyRect = new Rectangle(safeWidth - refreshWidth, node.Bounds.Y, refreshWidth, node.Bounds.Height);
            this.Invalidate(dirtyRect);
        }

        protected override void OnDrawNode(DrawTreeNodeEventArgs e)
        {
            if (e.Bounds.Height <= 0 || e.Bounds.Width <= 0) return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int w = this.ClientSize.Width; 
            Rectangle fullRow = new Rectangle(0, e.Bounds.Y, w, this.ItemHeight);

            // 1. 背景
            if ((e.State & TreeNodeStates.Selected) != 0) g.FillRectangle(_selectBgBrush, fullRow);
            else if ((e.State & TreeNodeStates.Hot) != 0) g.FillRectangle(_hoverBrush, fullRow);
            else g.FillRectangle(Brushes.White, fullRow);

            // 2. 分割线
            g.DrawLine(_linePen, 0, fullRow.Bottom - 1, w, fullRow.Bottom - 1);

            // --- ★★★ 坐标计算 (从最右侧开始往左推) ★★★ ---
            // 基准线 = 窗口宽度 - 右边距
            int xBase = w - UIUtils.S(RightMargin); 
            
            // 折叠图标区域
            Rectangle chevronRect = new Rectangle(xBase - UIUtils.S(IconWidth), fullRow.Y, UIUtils.S(IconWidth), fullRow.Height);
            
            // Max 列区域 (在图标左侧)
            int xMax = chevronRect.X - UIUtils.S(5) - UIUtils.S(ColMaxWidth);
            Rectangle maxRect = new Rectangle(xMax, fullRow.Y, UIUtils.S(ColMaxWidth), fullRow.Height);

            // Value 列区域 (在 Max 左侧)
            int xValue = xMax - UIUtils.S(10) - UIUtils.S(ColValueWidth);
            Rectangle valRect = new Rectangle(xValue, fullRow.Y, UIUtils.S(ColValueWidth), fullRow.Height);


            // 3. 绘制折叠图标 (如果有子节点)
            if (e.Node.Nodes.Count > 0)
            {
                DrawChevron(g, chevronRect, e.Node.IsExpanded);
            }

            // 4. 绘制数值 (仅传感器)
            if (e.Node.Tag is ISensor sensor)
            {
                // Max (灰色)
                string maxStr = FormatValue(sensor.Max, sensor.SensorType);
                TextRenderer.DrawText(g, maxStr, _baseFont, maxRect, Color.Gray, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);

                // Value (彩色)
                string valStr = FormatValue(sensor.Value, sensor.SensorType);
                Color valColor = GetColorByType(sensor.SensorType);
                TextRenderer.DrawText(g, valStr, _baseFont, valRect, valColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }

            // 5. 绘制左侧文本
            // --- ★★★ 颜色与字体逻辑修正 ★★★ ---
            Color txtColor;
            Font font;

            if (e.Node.Tag is IHardware) 
            {
                // 情况1：硬件 (主板、CPU、显卡、SuperIO芯片) -> 纯黑 + 粗体
                font = _boldFont;
                txtColor = Color.Black;
            }
            else if (e.Node.Tag is ISensor)
            {
                // 情况2：传感器 (具体的温度、电压) -> 深灰 + 常规
                font = _baseFont;
                txtColor = Color.FromArgb(20, 20, 20); 
            }
            else 
            {
                // 情况3：类型分类 (Temperatures, Fans, Controls) -> 深灰 + 粗体 (比硬件淡一点)
                // 只要 Tag 不是硬件也不是传感器，就认为是分类组
                font = _boldFont;
                txtColor = Color.FromArgb(30, 30, 30); 
            }

            // 计算文本绘制区域 (左侧缩进 -> Value列左侧)
            int indent = (e.Node.Level * UIUtils.S(20)) + UIUtils.S(10);
            int textWidth = xValue - indent - UIUtils.S(10); 
            
            Rectangle textRect = new Rectangle(indent, fullRow.Y, textWidth, fullRow.Height);
            TextRenderer.DrawText(g, e.Node.Text, font, textRect, txtColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        private void DrawChevron(Graphics g, Rectangle rect, bool expanded)
        {
            int cx = rect.X + rect.Width / 2;
            int cy = rect.Y + rect.Height / 2;
            int size = UIUtils.S(4);

            using (Pen p = new Pen(_chevronBrush, 1.5f))
            {
                if (expanded) // V
                {
                    g.DrawLine(p, cx - size, cy - 2, cx, cy + 3);
                    g.DrawLine(p, cx, cy + 3, cx + size, cy - 2);
                }
                else // > (折叠状态)
                {
                    g.DrawLine(p, cx - 2, cy - size, cx + 2, cy);
                    g.DrawLine(p, cx + 2, cy, cx - 2, cy + size);
                }
            }
        }

        private Color GetColorByType(SensorType type)
        {
            switch (type) {
                case SensorType.Temperature: return Color.FromArgb(200, 60, 0); 
                case SensorType.Load: return Color.FromArgb(0, 100, 0); 
                case SensorType.Power: return Color.Purple;
                case SensorType.Clock: return Color.DarkBlue;
                default: return Color.Black;
            }
        }

        private string FormatValue(float? val, SensorType type)
        {
            if (!val.HasValue) return "-";
            float v = val.Value;
            switch (type)
            {
                case SensorType.Voltage: return $"{v:F3} V";
                case SensorType.Clock: return v >= 1000 ? $"{v/1000:F1} GHz" : $"{v:F0} MHz";
                case SensorType.Temperature: return $"{v:F0} °C";
                case SensorType.Load: return $"{v:F1} %";
                case SensorType.Fan: return $"{v:F0} RPM";
                case SensorType.Power: return $"{v:F1} W";
                case SensorType.Data: return $"{v:F1} GB";
                case SensorType.SmallData: return $"{v:F0} MB";
                case SensorType.Throughput: return UIUtils.FormatDataSize(v, "/s");
                default: return $"{v:F1}";
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var node = this.GetNodeAt(e.X, e.Y);
            if (node != null && node.Nodes.Count > 0)
            {
                // 点击右侧折叠区 (宽度放大一点方便点击)
                if (e.X > this.ClientSize.Width - UIUtils.S(RightMargin + IconWidth + 20)) 
                {
                    if (node.IsExpanded) node.Collapse(); else node.Expand();
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _baseFont?.Dispose(); _boldFont?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}