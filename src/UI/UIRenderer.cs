using LiteMonitor.src.Core;
using System.Drawing.Drawing2D;

namespace LiteMonitor
{
    public static class UIRenderer
    {
        // ★★★ 优化：移除本地画刷缓存，改为调用 UIUtils ★★★
        // private static readonly Dictionary<string, SolidBrush> _brushCache = new();
        // private static SolidBrush GetBrush(string color, Theme t) { ... }

        // [替换] ClearCache 方法
        public static void ClearCache() 
        {
            // 委托给 UIUtils 清理
            UIUtils.ClearBrushCache();
        }

        public static void Render(Graphics g, List<GroupLayoutInfo> groups, Theme t)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 1. 绘制背景
            // ★★★ [核心修复] 扩大绘制区域，解决左侧和上侧漏黑边的问题 ★★★
            // 原理：从 (-5, -5) 开始画，确保绝对覆盖掉 (0,0) 处的物理像素死角。
            // 多余的部分会被系统自动裁剪，不会有副作用。
            g.FillRectangle(UIUtils.GetBrush(t.Color.Background), 
                new Rectangle(-5, -5, (int)g.VisibleClipBounds.Width + 10, (int)g.VisibleClipBounds.Height + 10));

            // 2. 绘制主标题
            DrawMainTitle(g, t);

            // 3. 绘制各分组
            foreach (var gr in groups)
            {
                DrawGroupBackground(g, gr, t);
                
                // 遍历子项绘制 (不再区分 NET/DISK，统一由 Item.Style 决定)
                foreach (var it in gr.Items)
                {
                    if (it.Style == MetricRenderStyle.TwoColumn)
                        DrawTwoColumnItem(g, it, t);
                    else
                        DrawStandardItem(g, it, t);
                }
            }
        }

        private static void DrawMainTitle(Graphics g, Theme t)
        {
            string title = LanguageManager.T("Title");
            if (string.IsNullOrEmpty(title) || title == "Title") return;

            // 直接使用字体高度，不需要测量
            int titleH = t.FontTitle.Height;
            
            // ★★★ [优化] 标题下方的微调间距也要随 DPI 缩放 ★★★
            int titlePadding = (int)(4 * t.Layout.LayoutScale);
            
            var titleRect = new Rectangle(t.Layout.Padding, t.Layout.Padding, t.Layout.Width - t.Layout.Padding * 2, titleH + titlePadding);

            TextRenderer.DrawText(g, title, t.FontTitle, titleRect,
                ThemeManager.ParseColor(t.Color.TextTitle),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private static void DrawGroupBackground(Graphics g, GroupLayoutInfo gr, Theme t)
        {
            int gp = t.Layout.GroupPadding;
            
            // 绘制圆角背景
            UIUtils.FillRoundRect(g, gr.Bounds, t.Layout.GroupRadius, ThemeManager.ParseColor(t.Color.GroupBackground));

            // 绘制组标题 (CPU, GPU...)
            string label = LanguageManager.T($"Groups.{gr.GroupName}");
            if (string.IsNullOrEmpty(label)) label = gr.GroupName;

            int titleH = t.FontGroup.Height;
            int titleY = gr.Bounds.Y - t.Layout.GroupTitleOffset - titleH;

            var rectTitle = new Rectangle(gr.Bounds.X + gp, System.Math.Max(0, titleY), gr.Bounds.Width - gp * 2, titleH);

            TextRenderer.DrawText(g, label, t.FontGroup, rectTitle,
                ThemeManager.ParseColor(t.Color.TextGroup),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        /// <summary>
        /// 绘制标准项 (标签 + 数值 + 进度条)
        /// </summary>
        private static void DrawStandardItem(Graphics g, MetricItem it, Theme t)
        {
            if (it.Bounds == Rectangle.Empty) return;

            // Label (左对齐)
            string label = LanguageManager.T($"Items.{it.Key}"); 
            if (label == $"Items.{it.Key}") label = it.Label; // Fallback

            TextRenderer.DrawText(g, label, t.FontItem, it.LabelRect,
                ThemeManager.ParseColor(t.Color.TextPrimary),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // Value (右对齐)  传入 false 表示竖屏/普通模式
            string valText = it.GetFormattedText(false);
            
            
            Color valColor = UIUtils.GetColor(it.Key, it.DisplayValue, t);

            TextRenderer.DrawText(g, valText, t.FontValue, it.ValueRect,
                valColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // Bar - 注意：这里调用的是 UIUtils.DrawBar，它现在已经使用了优化的画刷逻辑
            UIUtils.DrawBar(g, it.BarRect, it.DisplayValue, it.Key, t);
        }

        /// <summary>
        /// 绘制双列项 (居中标签 + 居中数值)
        /// </summary>
        private static void DrawTwoColumnItem(Graphics g, MetricItem it, Theme t)
        {
            if (it.Bounds == Rectangle.Empty) return;

            // Label (居中顶部)
            string label = LanguageManager.T($"Items.{it.Key}");
            
            TextRenderer.DrawText(g, label, t.FontItem, it.LabelRect,
                ThemeManager.ParseColor(t.Color.TextPrimary),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);

            // Value (居中底部)

            string valText = it.GetFormattedText(t.Layout.Width < 240 * t.Layout.LayoutScale);
            // 窄屏处理
            if (t.Layout.Width < 240*t.Layout.LayoutScale) valText = UIUtils.FormatHorizontalValue(valText);
            Color valColor = UIUtils.GetColor(it.Key, it.Value ?? 0, t);

            TextRenderer.DrawText(g, valText, t.FontValue, it.ValueRect,
                valColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.NoPadding);
        }
    }
}