﻿using LiteMonitor.src.Core;
namespace LiteMonitor
{
    /// <summary>
    /// 单个组的布局信息（名称 + 块区域 + 子项）
    /// 必须包含这个类定义，UIRenderer 才能引用它
    /// </summary>
    public class GroupLayoutInfo
    {
        public string GroupName { get; set; }
        // ★★★ 新增：缓存组标签 (防止渲染循环重复申请字符串) ★★★
        public string Label { get; set; } = ""; 
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
            // ★★★ [优化] 获取缩放系数，修正硬编码像素在 高DPI 下过小的问题 ★★★
            float s = _t.Layout.LayoutScale; 
            if (s <= 0) s = 1.0f;

            // 将写死的像素值进行缩放
            int innerPad = (int)(10 * s);      // 原本是 10
            int innerPadTotal = innerPad * 2;  // 原本是 20
            int barMinH = (int)(4 * s);        // 原本是 6
            int barBotGap = (int)(3 * s);      // 原本是 3

            // ★★★ [视觉补偿] x 减去 1px ★★★
            // 修复“左边距比右边宽”的视觉问题（平衡 GDI+ 文本左侧留白）
            int x = _t.Layout.Padding - 1; 

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
                    g.GroupName.Equals("DISK", StringComparison.OrdinalIgnoreCase) || 
                    g.GroupName.Equals("DATA", StringComparison.OrdinalIgnoreCase);

                int contentHeight;
                if (isTwoColumnGroup)
                {
                    // === 双列布局计算 (NET / DISK) ===

                    // 1. 调整高度：容纳上下两行文字
                    int twoLineH = rowH;
                    contentHeight = twoLineH + _t.Layout.ItemGap;

                    int itemY = y + _t.Layout.GroupPadding + (_t.Layout.ItemGap / 2);
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
                        it.LabelRect = new Rectangle(itemX, itemY, colWidth, halfH);

                        // ValueRect 占下半部分
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

                        // 内部：左右 padding 使用缩放后的 innerPad
                        var inner = new Rectangle(x + innerPad, itemY, w - innerPadTotal, rowH);
                        
                        // 文本区域占上部 60%
                        int topH = (int)(inner.Height * 0.60);
                        var topRect = new Rectangle(inner.X, inner.Y, inner.Width, topH);
                        
                        it.LabelRect = topRect;
                        it.ValueRect = topRect;

                        // 进度条占底部，最小高度使用缩放后的 barMinH
                        int barH = Math.Max(barMinH, (int)(inner.Height * 0.20));
                        int barY = inner.Bottom - barH - barBotGap; // 底部间距也缩放
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