using LiteMonitor.src.Core;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace LiteMonitor
{
    /// <summary>
    /// 单个组的布局信息（名称 + 块区域 + 子项）
    /// 必须包含这个类定义，UIRenderer 才能引用它
    /// </summary>
    public class GroupLayoutInfo
    {
        public string GroupName { get; set; }
        public Rectangle Bounds { get; set; }
        public List<MetricItem> Items { get; set; }

        public GroupLayoutInfo(string name, List<MetricItem> items)
        {
            GroupName = name;
            Items = items;
            Bounds = Rectangle.Empty;
        }
    }

    /// <summary>
    /// -------- UILayout：布局计算层 --------
    /// 负责所有坐标、尺寸、比例的计算
    /// </summary>
    public class UILayout
    {
        private readonly Theme _t;
        public UILayout(Theme t) { _t = t; }

        /// <summary>
        /// 计算所有布局：将数学计算完全封装在此，Renderer 只有绘制逻辑
        /// </summary>
        public int Build(List<GroupLayoutInfo> groups)
        {
            int x = _t.Layout.Padding;
            int y = _t.Layout.Padding;
            int w = _t.Layout.Width - _t.Layout.Padding * 2;
            int rowH = _t.Layout.RowHeight;

            // 1. 主标题占位
            string title = LanguageManager.T("Title");
            if (!string.IsNullOrEmpty(title) && title != "Title")
                y += rowH + _t.Layout.Padding;

            // 2. 遍历分组
            for (int idx = 0; idx < groups.Count; idx++)
            {
                var g = groups[idx];
                
                // --- 策略判断：是双列模式还是普通模式？ ---
                bool isTwoColumnGroup = 
                    g.GroupName.Equals("NET", StringComparison.OrdinalIgnoreCase) || 
                    g.GroupName.Equals("DISK", StringComparison.OrdinalIgnoreCase);

                int contentHeight = 0;

                if (isTwoColumnGroup)
                {
                    // === 双列布局计算 (NET / DISK) ===
                    
                    // 1. 调整高度：原本 rowH * 1.1 太挤了，建议改为 1.4~1.5 倍，容纳上下两行文字
                    // 如果觉得太高，可以改回 0.10，但建议至少留足空间
                    int twoLineH = (int)(rowH * 1.1); 
                    contentHeight = twoLineH + _t.Layout.ItemGap;

                    int itemY = y + _t.Layout.GroupPadding;
                    int colWidth = w / 2; // 两列平分宽度

                    // 确保最多只处理2个项目
                    int count = Math.Min(g.Items.Count, 2);
                    
                    for (int i = 0; i < count; i++)
                    {
                        var it = g.Items[i];
                        it.Style = MetricRenderStyle.TwoColumn;

                        // 2. 计算 X 轴：左列还是右列
                        // i=0 -> 左边 (x), i=1 -> 右边 (x + colWidth)
                        int itemX = (i == 0) ? x : x + colWidth;
                        
                        // 整个格子的区域
                        it.Bounds = new Rectangle(itemX, itemY, colWidth, twoLineH);

                        // 3. 内部布局：上下严格平分，避免重叠
                        int halfH = twoLineH / 2;

                        // LabelRect 占上半部分
                        // 渲染器会自动垂直居中或顶部对齐，这里给足空间即可
                        it.LabelRect = new Rectangle(itemX, itemY, colWidth, halfH);

                        // ValueRect 占下半部分
                        // 从 itemY + halfH 开始
                        it.ValueRect = new Rectangle(itemX, itemY + halfH, colWidth, twoLineH - halfH);
                    }
                }
                else
                {
                    // === 标准列表布局计算 (CPU / MEM / GPU) ===
                    contentHeight = g.Items.Count * rowH + (g.Items.Count - 1) * _t.Layout.ItemGap;
                    
                    int itemY = y + _t.Layout.GroupPadding;
                    foreach (var it in g.Items)
                    {
                        it.Style = MetricRenderStyle.StandardBar;
                        it.Bounds = new Rectangle(x, itemY, w, rowH);

                        // 内部：左右 padding 10px
                        var inner = new Rectangle(x + 10, itemY, w - 20, rowH);
                        
                        // 文本区域占上部 55%
                        int topH = (int)(inner.Height * 0.55);
                        var topRect = new Rectangle(inner.X, inner.Y, inner.Width, topH);
                        
                        it.LabelRect = topRect;
                        it.ValueRect = topRect;

                        // 进度条占底部，最小 6px
                        int barH = Math.Max(6, (int)(inner.Height * 0.25));
                        int barY = inner.Bottom - barH - 3;
                        it.BarRect = new Rectangle(inner.X, barY, inner.Width, barH);

                        itemY += rowH + _t.Layout.ItemGap;
                    }
                }

                // 3. 结算组高度
                int groupHeight = _t.Layout.GroupPadding * 2 + contentHeight;
                g.Bounds = new Rectangle(x, y, w, groupHeight);

                // 4. 移动 Y 轴到下一组
                if (idx < groups.Count - 1)
                    y += groupHeight + _t.Layout.GroupSpacing + _t.Layout.GroupBottom;
                else
                    y += groupHeight; 
            }

            return y + _t.Layout.Padding; // 返回总高度
        }
    }
}