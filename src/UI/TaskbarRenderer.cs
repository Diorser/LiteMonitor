using LiteMonitor.Common;
using LiteMonitor.src.Core;
using Microsoft.Win32;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace LiteMonitor
{
    /// <summary>
    /// 任务栏渲染器（TaskbarRenderer）
    /// --------------------------------------------------------
    /// ① 独立字体（硬编码）
    /// ② 独立颜色（硬编码）
    /// ③ 颜色分深色主题与浅色主题两套
    /// ④ Warn/Crit 阈值数值从 Theme 阈值读取（你要求）
    /// ⑤ 最终颜色由 TaskbarRenderer 自己决定（硬编码方案）
    ///
    /// 注意：
    /// 不使用 Theme.Color.* 的颜色，只使用 Theme 阈值。
    /// </summary>
    public static class TaskbarRenderer
    {
        // =============================
        // 系统主题切换：深色/浅色两套颜色
        // =============================

        // 浅色系统主题 → Label 用深色
        private static readonly Color LABEL_LIGHT = Color.FromArgb(20, 20, 20);

        // 深色系统主题 → Label 用白色
        private static readonly Color LABEL_DARK = Color.White;

        // ========= 数值颜色（三级） ==========
        // 浅色系统主题
        private static readonly Color SAFE_LIGHT = Color.FromArgb(0x00, 0x80, 0x40);  // 深绿
        private static readonly Color WARN_LIGHT = Color.FromArgb(0xB5, 0x75, 0x00);  // 暗黄
        private static readonly Color CRIT_LIGHT = Color.FromArgb(0xC0, 0x30, 0x30);  // 暗红

        // 深色系统主题
        private static readonly Color SAFE_DARK = Color.FromArgb(0x66, 0xFF, 0x99);   // 明亮绿
        private static readonly Color WARN_DARK = Color.FromArgb(0xFF, 0xD6, 0x66);   // 明亮黄
        private static readonly Color CRIT_DARK = Color.FromArgb(0xFF, 0x66, 0x66);   // 明亮红


        // 判断系统深/浅色模式
        private static bool IsSystemLightTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

                int v = (int)(key?.GetValue("SystemUsesLightTheme", 1) ?? 1);
                return v != 0;
            }
            catch { return true; }
        }


        // =============================
        // 主渲染入口
        // =============================
        public static void Render(Graphics g, List<Column> cols, int taskbarHeight)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            float dpiScale = g.DpiX / 96f;

            foreach (var col in cols)
                DrawColumn(g, col, taskbarHeight, dpiScale);
        }


        private static void DrawColumn(Graphics g, Column col, int totalHeight, float dpi)
        {
            if (col.Bounds == Rectangle.Empty) return;

            int rowH = totalHeight / 2;
            //int rowH = GetTextHeight(g);   // 使用字体真实高度（更紧凑）

            var rtTop = new Rectangle(col.Bounds.X, col.Bounds.Y+2, col.Bounds.Width, rowH);
            var rtBottom = new Rectangle(col.Bounds.X, col.Bounds.Y-2 + rowH, col.Bounds.Width, rowH);

            if (col.Top != null)
                DrawItem(g, col.Top, rtTop, dpi);

            if (col.Bottom != null)
                DrawItem(g, col.Bottom, rtBottom, dpi);
        }


        /// <summary>
        /// 绘制单个项目：Label + Value
        /// </summary>
        private static void DrawItem(Graphics g, MetricItem item, Rectangle rc, float dpi)
        {
            // ===== label 文本 =====
            string label = LanguageManager.T($"Short.{item.Key}");

            // ===== value 文本 =====
            string value = UIUtils.FormatValue(item.Key, item.DisplayValue);
            value = UIUtils.FormatHorizontalValue(value);//去除网络和磁盘的“/s”，小数点智能显示

            using var fLabel = ThemeManager.Current.FontTaskbar;
            using var fValue = ThemeManager.Current.FontTaskbar;

            // ===== 根据系统深浅主题选择 label 颜色 =====
            bool isLight = IsSystemLightTheme();
            Color labelColor = isLight ? LABEL_LIGHT : LABEL_DARK;

            // ===== Value 颜色由阈值区间判定 =====
            Color valueColor = PickColorByThreshold(item.Key, item.DisplayValue, isLight);

            // ===== 绘制 label（左对齐）=====
            TextRenderer.DrawText(
                g, label, fLabel, rc,
                labelColor,
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoClipping
            );

            // ===== 绘制 value（右对齐）=====
            TextRenderer.DrawText(
                g, value, fValue, rc,
                valueColor,
                TextFormatFlags.Right |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoClipping
            );
        }


        /// <summary>
        /// 根据 Theme 的阈值区间 + 系统深浅色主题，
        /// 决定最终 value 的颜色。
        /// （颜色硬编码，阈值来自主题）
        /// </summary>
        private static Color PickColorByThreshold(string key, double value, bool light)
        {
            if (double.IsNaN(value))
                return light ? LABEL_LIGHT : LABEL_DARK;

            // 从 UIUtils 读取主题阈值（你要求用这个）
            var (warn, crit) = UIUtils.GetThresholds(key, ThemeManager.Current);

            //网络/磁盘数值转化成KB
            if (key.StartsWith("NET") || key.StartsWith("DISK"))
            {
                value = value / 1024.0;
            }
                // 判定区间
                if (value >= crit)
                return light ? CRIT_LIGHT : CRIT_DARK;

            if (value >= warn)
                return light ? WARN_LIGHT : WARN_DARK;

            return light ? SAFE_LIGHT : SAFE_DARK;
        }


    }
}
