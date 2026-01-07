using LiteMonitor.src.Core;
using System.Drawing;
using System.Drawing.Text;
using System.Collections.Generic;
using System.Windows.Forms;

namespace LiteMonitor
{
    /// <summary>
    /// 任务栏渲染器（仅负责绘制，不再负责布局）
    /// </summary>
    public static class TaskbarRenderer
    {
        // 字体缓存
        private static Font? _cachedFont = null;
        
        // ★★★ 新增：用于存储 Layout 传来的修正字体 ★★★
        private static Font? _correctionFont = null; 

        // 浅色主题
        private static readonly Color LABEL_LIGHT = Color.FromArgb(20, 20, 20);
        private static readonly Color SAFE_LIGHT = Color.FromArgb(0x00, 0x80, 0x40);
        private static readonly Color WARN_LIGHT = Color.FromArgb(0xB5, 0x75, 0x00);
        private static readonly Color CRIT_LIGHT = Color.FromArgb(0xC0, 0x30, 0x30);

        // 深色主题
        private static readonly Color LABEL_DARK = Color.White;
        private static readonly Color SAFE_DARK = Color.FromArgb(0x66, 0xFF, 0x99);
        private static readonly Color WARN_DARK = Color.FromArgb(0xFF, 0xD6, 0x66);
        private static readonly Color CRIT_DARK = Color.FromArgb(0xFF, 0x66, 0x66);

        // 自定义颜色缓存
        private static bool _useCustom = false;
        private static Color _cLabel, _cSafe, _cWarn, _cCrit;

        // ★★★ 新增：接收修正字体（如果为null则清除） ★★★
        public static void SetCorrectionFont(Font? f)
        {
            // 如果之前有修正字体，且不是同一个对象，先释放旧的
            if (_correctionFont != null && _correctionFont != f)
            {
                _correctionFont.Dispose();
            }
            _correctionFont = f;
        }

        public static void ReloadStyle(Settings cfg)
        {
            _cachedFont?.Dispose(); // 释放旧的
            var style = cfg.TaskbarFontBold ? FontStyle.Bold : FontStyle.Regular;
            _cachedFont = new Font(cfg.TaskbarFontFamily, cfg.TaskbarFontSize, style);

            // 读取自定义颜色配置
            _useCustom = cfg.TaskbarCustomStyle;
            if (_useCustom)
            {
                try {
                    _cLabel = ColorTranslator.FromHtml(cfg.TaskbarColorLabel);
                    _cSafe = ColorTranslator.FromHtml(cfg.TaskbarColorSafe);
                    _cWarn = ColorTranslator.FromHtml(cfg.TaskbarColorWarn);
                    _cCrit = ColorTranslator.FromHtml(cfg.TaskbarColorCrit);
                } catch {
                    _useCustom = false; 
                }
            }
        }

        public static void Render(Graphics g, List<Column> cols, bool light)
        {
            if (_cachedFont == null) ReloadStyle(Settings.Load());
            
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            foreach (var col in cols)
            {
                if (col.BoundsTop != Rectangle.Empty && col.Top != null)
                    DrawItem(g, col.Top, col.BoundsTop, light);

                if (col.BoundsBottom != Rectangle.Empty && col.Bottom != null)
                    DrawItem(g, col.Bottom, col.BoundsBottom, light);
            }
        }

        private static void DrawItem(Graphics g, MetricItem item, Rectangle rc, bool light)
        {
            string label = LanguageManager.T($"Short.{item.Key}");
            string value = item.GetFormattedText(true);

            // ★★★ 优先使用修正字体，如果没有则使用默认字体 ★★★
            Font font = _correctionFont ?? _cachedFont!;
            
            Color labelColor, valueColor;

            if (_useCustom)
            {
                labelColor = _cLabel;
                valueColor = PickCustomColor(item.Key, item.DisplayValue);
            }
            else
            {
                labelColor = light ? LABEL_LIGHT : LABEL_DARK;
                valueColor = PickColor(item.Key, item.DisplayValue, light);
            }

            TextRenderer.DrawText(
                g, label, font, rc, labelColor,
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoClipping
            );

            TextRenderer.DrawText(
                g, value, font, rc, valueColor,
                TextFormatFlags.Right |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoClipping
            );
        }

        private static Color PickCustomColor(string key, double v)
        {
            if (double.IsNaN(v)) return _cLabel;
            int result = UIUtils.GetColorResult(key, v);
            if (result == 2) return _cCrit;
            if (result == 1) return _cWarn;
            return _cSafe;
        }

        private static Color PickColor(string key, double v, bool light)
        {
            if (double.IsNaN(v)) return light ? LABEL_LIGHT : LABEL_DARK;
            int result = UIUtils.GetColorResult(key, v); 
            if (result == 2) return light ? CRIT_LIGHT : CRIT_DARK;
            if (result == 1) return light ? WARN_LIGHT : WARN_DARK;
            return light ? SAFE_LIGHT : SAFE_DARK;
        }
    }
}