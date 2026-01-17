using LiteMonitor.src.Core;

namespace LiteMonitor
{
    /// <summary>
    /// 横版渲染器（基于列结构绘制）
    /// 完全保留原版布局，不做任何功能添加。
    /// 修复内容：
    /// 1. Render 方法签名修复（支持 panelWidth）
    /// 2. value/颜色 使用 UIUtils 统一入口 -> 升级为 MetricItem 缓存入口
    /// 3. 删除文件内重复工具函数
    /// </summary>
    public static class HorizontalRenderer
    {
        public static void Render(Graphics g, Theme t, List<Column> cols, int panelWidth)
        {
            int panelHeight = (int)g.VisibleClipBounds.Height;

            using (var bg = new SolidBrush(ThemeManager.ParseColor(t.Color.Background)))
                g.FillRectangle(bg, new Rectangle(0, 0, panelWidth, panelHeight));

            foreach (var col in cols)
                DrawColumn(g, col, t);
        }

        private static void DrawColumn(Graphics g, Column col, Theme t)
        {
            if (col.Bounds == Rectangle.Empty) return;
            // ★★★ [新增]：如果只有 Top 没有 Bottom，则让 Top 占用整个 Bounds (实现垂直居中)
            if (col.Bottom == null && col.Top != null)
            {
                DrawItem(g, col.Top, col.Bounds, t);
                return;
            }

            int half = col.Bounds.Height / 2;
            var rectTop = new Rectangle(col.Bounds.X, col.Bounds.Y, col.Bounds.Width, half);
            var rectBottom = new Rectangle(col.Bounds.X, col.Bounds.Y + half, col.Bounds.Width, half);

            if (col.Top != null) DrawItem(g, col.Top, rectTop, t);
            if (col.Bottom != null) DrawItem(g, col.Bottom, rectBottom, t);
        }

        private static void DrawItem(Graphics g, MetricItem it, Rectangle rc, Theme t)
        {
            // 使用 MetricItem 统一格式化 (横屏模式=true)
            string value = it.GetFormattedText(true);
            Color valColor = it.GetTextColor(t);

            // ★★★ 策略 A: 纯文本模式 (隐藏标签) ★★★
            // 适用于 IP、Dashboard 文本，直接居左显示
            // 逻辑：如果 ShortLabel 被显式设为空格，则视为隐藏标签
            bool hideLabel = (it.ShortLabel == " ");

            if (hideLabel)
            {
                // 可以根据偏好选择 Left 或 Center，这里选用 Left 比较稳妥
                TextRenderer.DrawText(
                    g,
                    value,
                    t.FontValue,
                    rc,
                    valColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                );
                return;
            }

            // ★★★ 策略 B: 标准标签模式 ★★★
            // Label (左对齐)
            // 优化：直接使用缓存的 ShortLabel
            string label = !string.IsNullOrEmpty(it.ShortLabel) ? it.ShortLabel : it.Label;
            if (string.IsNullOrEmpty(label)) label = it.Key;

            TextRenderer.DrawText(
                g,
                label,
                t.FontItem,
                rc,
                ThemeManager.ParseColor(t.Color.TextPrimary),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
            );

            // Value (右对齐)
            TextRenderer.DrawText(
                g,
                value,
                t.FontValue,
                rc,
                valColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
            );
        }
    }
}