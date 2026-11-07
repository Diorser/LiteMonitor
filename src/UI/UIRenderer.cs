using LiteMonitor.src.Core;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LiteMonitor
{
    public static class UIRenderer
    {
        // [替换] ClearCache 方法
        public static void ClearCache() 
        {
            // 委托给 UIUtils 清理
            UIUtils.ClearBrushCache();
        }

        /// <param name="bgAlpha">背景（面板/分组/进度条/分割线）透明度</param>
        /// <param name="textAlpha">文字透明度，与背景独立</param>
        public static void Render(Graphics g, List<GroupLayoutInfo> groups, Theme t,
            double bgAlpha = 1.0, double textAlpha = 1.0)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 1. 绘制背景
            // ★★★ [核心修复] 扩大绘制区域，解决左侧和上侧漏黑边的问题 ★★★
            // 原理：从 (-5, -5) 开始画，确保绝对覆盖掉 (0,0) 处的物理像素死角。
            // 多余的部分会被系统自动裁剪，不会有副作用。
            using (var bgBrush = new SolidBrush(UIUtils.WithAlpha(ThemeManager.ParseColor(t.Color.Background), bgAlpha)))
            {
                g.FillRectangle(bgBrush,
                    new Rectangle(-5, -5, (int)g.VisibleClipBounds.Width + 10, (int)g.VisibleClipBounds.Height + 10));
            }

            // 2. 绘制主标题
            DrawMainTitle(g, t, textAlpha);

            // 3. 绘制各分组
            foreach (var gr in groups)
            {
                DrawGroupBackground(g, gr, t, bgAlpha, textAlpha);
                
                // 遍历子项绘制 (不再区分 NET/DISK，统一由 Item.Style 决定)
                for (int i = 0; i < gr.Items.Count; i++)
                {
                    var it = gr.Items[i];
                    bool isLast = (i == gr.Items.Count - 1);

                    if (it.Style == MetricRenderStyle.TwoColumn)
                        DrawTwoColumnItem(g, it, t, textAlpha);
                    else if (it.Style == MetricRenderStyle.TextOnly) // [新增]
                        DrawTextItem(g, it, t, isLast, bgAlpha, textAlpha);
                    else
                        DrawStandardItem(g, it, t, bgAlpha, textAlpha);
                }
            }
        }

        private static void DrawMainTitle(Graphics g, Theme t, double textAlpha)
        {
            string title = LanguageManager.T("Title");
            if (string.IsNullOrEmpty(title) || title == "Title") return;

            // 直接使用字体高度，不需要测量
            int titleH = t.FontTitle.Height;
            
            // ★★★ [优化] 标题下方的微调间距也要随 DPI 缩放 ★★★
            int titlePadding = (int)(4 * t.Layout.LayoutScale);
            
            var titleRect = new Rectangle(t.Layout.Padding, t.Layout.Padding, t.Layout.Width - t.Layout.Padding * 2, titleH + titlePadding);

            UIUtils.DrawText(g, title, t.FontTitle, titleRect,
                ThemeManager.ParseColor(t.Color.TextTitle),
                StringAlignment.Near, StringAlignment.Center, textAlpha);
        }

        private static void DrawGroupBackground(Graphics g, GroupLayoutInfo gr, Theme t, double bgAlpha, double textAlpha)
        {
            int gp = t.Layout.GroupPadding;
            
            // 绘制圆角背景
            UIUtils.FillRoundRect(g, gr.Bounds, t.Layout.GroupRadius,
                ThemeManager.ParseColor(t.Color.GroupBackground), bgAlpha);

            // 绘制组标题 (CPU, GPU...)
            // ★★★ 优化：直接使用缓存的 gr.Label，不再每帧调用 LanguageManager.T ★★★
            string label = string.IsNullOrEmpty(gr.Label) ? gr.GroupName : gr.Label;

            int titleH = t.FontGroup.Height;
            int titleY = gr.Bounds.Y - t.Layout.GroupTitleOffset - titleH;

            // [Fix] 修复 Windows 10 下 Emoji 显示不全的问题
            // Emoji 字符高度通常大于普通字体高度，且 VerticalCenter 可能导致上下被裁剪
            // 解决方案：扩大绘制区域的高度，并保持中心对齐
            int extraH = (int)(8 * t.Layout.LayoutScale);
            
            // Y 向上偏移一半 extraH，高度增加 extraH，这样中心点不变
            var rectTitle = new Rectangle(gr.Bounds.X + gp, 
                System.Math.Max(0, titleY - extraH / 2), 
                gr.Bounds.Width - gp * 2, 
                titleH + extraH);

            UIUtils.DrawText(g, label, t.FontGroup, rectTitle,
                ThemeManager.ParseColor(t.Color.TextGroup),
                StringAlignment.Near, StringAlignment.Center, textAlpha);
        }

        /// <summary>
        /// 绘制标准项 (标签 + 数值 + 进度条)
        /// </summary>
        private static void DrawStandardItem(Graphics g, MetricItem it, Theme t, double bgAlpha, double textAlpha)
        {
            if (it.Bounds == Rectangle.Empty) return;

            // Label (左对齐)
            // ★★★ 优化：直接使用缓存的 it.Label ★★★
            string label = string.IsNullOrEmpty(it.Label) ? it.Key : it.Label;

            UIUtils.DrawText(g, label, t.FontItem, it.LabelRect,
                ThemeManager.ParseColor(t.Color.TextPrimary),
                StringAlignment.Near, StringAlignment.Center, textAlpha);

            // Value (右对齐)  传入 false 表示竖屏/普通模式
            string valText = it.GetFormattedText(false);
            
            Color valColor = it.GetTextColor(t);

            UIUtils.DrawText(g, valText, t.FontValue, it.ValueRect,
                valColor,
                StringAlignment.Far, StringAlignment.Center, textAlpha);

            // Bar - 注意：这里调用的是 UIUtils.DrawBar，它现在已经使用了优化的画刷逻辑
            UIUtils.DrawBar(g, it, t, bgAlpha);
        }

        /// <summary>
        /// 绘制双列项 (居中标签 + 居中数值)
        /// </summary>
        private static void DrawTwoColumnItem(Graphics g, MetricItem it, Theme t, double textAlpha)
        {
            if (it.Bounds == Rectangle.Empty) return;

            // Label (居中顶部)
            string label = string.IsNullOrEmpty(it.Label) ? it.Key : it.Label;
            
            UIUtils.DrawText(g, label, t.FontItem, it.LabelRect,
                ThemeManager.ParseColor(t.Color.TextPrimary),
                StringAlignment.Center, StringAlignment.Near, textAlpha);

            // Value (居中底部)
            // ★★★ [优化]：如果用户使用了自定义单位 (HasCustomUnit)，则跳过窄屏自动精简逻辑 ★★★
            // 原逻辑：if (narrow) valText = FormatHorizontalValue(...)
            bool narrow = t.Layout.Width < 240 * t.Layout.LayoutScale;
            string valText = it.GetFormattedText(narrow);
            
            Color valColor = it.GetTextColor(t);

            UIUtils.DrawText(g, valText, t.FontValue, it.ValueRect,
                valColor,
                StringAlignment.Center, StringAlignment.Far, textAlpha);
        }

        // [新增] 绘制纯文本项方法
        private static void DrawTextItem(Graphics g, MetricItem it, Theme t, bool isLast, double bgAlpha, double textAlpha)
        {
            if (it.Bounds == Rectangle.Empty) return;

            // 1. 绘制左侧标签 (IP)
            string label = string.IsNullOrEmpty(it.Label) ? it.Key : it.Label;
            UIUtils.DrawText(g, label, t.FontItem, it.LabelRect,
                ThemeManager.ParseColor(t.Color.TextPrimary),
                StringAlignment.Near, StringAlignment.Center, textAlpha);

            // 2. 绘制右侧数值 (192.168.x.x)
            // ★★★ 修复：直接使用布局计算好的 ValueRect (已修正为全宽) ★★★
            // 优先调用 GetFormattedText 以支持单位显示
            string text = it.GetFormattedText(false);
            
            // ★★★ 修改：字体使用 FontItem (与标签一致)，颜色保持 ValueSafe ★★★
            // [Fix] Use GetTextColor(t) to respect Plugin Color Override
            Color valColor = it.GetTextColor(t);
            UIUtils.DrawText(g, text, t.FontItem, it.ValueRect,
                valColor,
                StringAlignment.Far, StringAlignment.Center, textAlpha);

            // ★★★ 修复：补充分割线 (最后一行不画) ★★★
            if (!isLast)
            {
                // 使用半透明的 BarBackground 作为分割线
                Color baseDiv = ThemeManager.ParseColor(t.Color.BarBackground);
                Color divColor = UIUtils.WithAlpha(Color.FromArgb(100, baseDiv.R, baseDiv.G, baseDiv.B), bgAlpha);
                
                // [优化] 使用缓存的 Pen
                var pen = UIUtils.GetPen(divColor, 1);
                
                // 线条稍微缩进一点，更美观
                int lineY = it.Bounds.Bottom - 1;
                g.DrawLine(pen, it.Bounds.Left + 5, lineY, it.Bounds.Right - 5, lineY);
            }
        }

    }
}
