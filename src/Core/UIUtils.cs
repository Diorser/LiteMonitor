using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// LiteMonitor UI Utilities (Refactored)
    /// Logic delegated to MetricUtils.
    /// This class now focuses on GDI+ Rendering helpers and Resource Management.
    /// </summary>
    public static class UIUtils
    {
        // ============================================================
        // 1. String Interning (Memory Optimization)
        // ============================================================
        private static readonly Dictionary<string, string> _stringPool = new(StringComparer.Ordinal);
        private static readonly object _poolLock = new object();

        public static string Intern(string str)
        {
            if (string.IsNullOrEmpty(str)) return string.Empty;
            lock (_poolLock)
            {
                if (_stringPool.TryGetValue(str, out var pooled)) return pooled;
                _stringPool[str] = str;
                return str;
            }
        }

        public static void ClearStringPool()
        {
            lock (_poolLock) _stringPool.Clear();
        }

        // ============================================================
        // 2. DPI Scaling
        // ============================================================
        public static float ScaleFactor { get; set; } = 1.0f;

        public static int S(int px) => (int)(px * ScaleFactor);
        public static float S(float px) => px * ScaleFactor;
        public static Size S(Size size) => new Size(S(size.Width), S(size.Height));
        public static Padding S(Padding p) => new Padding(S(p.Left), S(p.Top), S(p.Right), S(p.Bottom));

        // ============================================================
        // 3. GDI+ Resource Cache (Brushes & Fonts)
        // ============================================================
        private static readonly Dictionary<string, SolidBrush> _brushCache = new(16);
        private static readonly Dictionary<string, Pen> _penCache = new(16);
        private static readonly Dictionary<string, Font> _fontCache = new(16);
        private static readonly Dictionary<string, StringFormat> _formatCache = new(8);
        private static readonly object _brushLock = new object();
        private const int MAX_BRUSH_CACHE = 32;

        public static Pen GetPen(Color color, float width = 1.0f)
        {
            string key = $"{color.ToArgb()}_{width:F1}";
            lock (_brushLock)
            {
                if (!_penCache.TryGetValue(key, out var pen))
                {
                    if (_penCache.Count >= MAX_BRUSH_CACHE)
                    {
                        foreach (var p in _penCache.Values) p.Dispose();
                        _penCache.Clear();
                    }
                    pen = new Pen(color, width);
                    _penCache[key] = pen;
                }
                return pen;
            }
        }

        public static SolidBrush GetBrush(string color)
        {
            if (string.IsNullOrEmpty(color)) return (SolidBrush)Brushes.Transparent;

            lock (_brushLock)
            {
                if (!_brushCache.TryGetValue(color, out var br))
                {
                    // Cache eviction policy
                    if (_brushCache.Count >= MAX_BRUSH_CACHE)
                    {
                        var keysToRemove = _brushCache.Keys.Take(_brushCache.Count / 2).ToList();
                        foreach (var k in keysToRemove)
                        {
                            if (_brushCache.TryGetValue(k, out var oldBrush))
                            {
                                oldBrush.Dispose();
                                _brushCache.Remove(k);
                            }
                        }
                    }

                    br = new SolidBrush(ThemeManager.ParseColor(color));
                    _brushCache[color] = br;
                }
                return br;
            }
        }

        public static Font GetFont(string familyName, float size, bool bold)
        {
            string key = $"{familyName}_{size}_{bold}";
            lock (_brushLock)
            {
                if (!_fontCache.TryGetValue(key, out var font))
                {
                    try 
                    {
                        var style = bold ? FontStyle.Bold : FontStyle.Regular;
                        font = new Font(familyName, size, style);
                    }
                    catch
                    {
                        font = new Font(SystemFonts.DefaultFont.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular);
                    }
                    _fontCache[key] = font;
                }
                return font;
            }
        }

        public static void ClearBrushCache()
        {
            lock (_brushLock)
            {
                foreach (var b in _brushCache.Values) b.Dispose();
                _brushCache.Clear();

                foreach (var p in _penCache.Values) p.Dispose();
                _penCache.Clear();
                
                foreach (var f in _fontCache.Values) f.Dispose();
                _fontCache.Clear();
            }
        }

        // ============================================================
        // 4. Theme Helpers
        // ============================================================

        public static Color GetStateColor(int state, Theme t, bool isValueText = true)
        {
            if (state == MetricUtils.STATE_CRIT) return ThemeManager.ParseColor(isValueText ? t.Color.ValueCrit : t.Color.BarHigh);
            if (state == MetricUtils.STATE_WARN) return ThemeManager.ParseColor(isValueText ? t.Color.ValueWarn : t.Color.BarMid);
            return ThemeManager.ParseColor(t.Color.ValueSafe);
        }

        // ============================================================
        // 5. Drawing Helpers
        // ============================================================

        /// <summary>
        /// 按比例调整颜色的不透明度（保留原有 Alpha）
        /// </summary>
        public static Color WithAlpha(Color c, double alpha)
        {
            int a = (int)Math.Round(Math.Clamp(alpha, 0.0, 1.0) * 255 * c.A / 255.0);
            return Color.FromArgb(Math.Clamp(a, 0, 255), c.R, c.G, c.B);
        }

        private static StringFormat GetStringFormat(StringAlignment align, StringAlignment lineAlign)
        {
            string key = $"{align}_{lineAlign}";
            lock (_brushLock)
            {
                if (!_formatCache.TryGetValue(key, out var sf))
                {
                    sf = new StringFormat(StringFormat.GenericTypographic)
                    {
                        Alignment = align,
                        LineAlignment = lineAlign,
                        Trimming = StringTrimming.None,
                        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
                    };
                    _formatCache[key] = sf;
                }
                return sf;
            }
        }

        /// <summary>
        /// 使用 GDI+ 绘制文本（支持逐像素 Alpha，用于分层窗口）。
        /// 注意：TextRenderer 属于 GDI，不会写入 Alpha 通道，因此透明文字必须走 DrawString。
        /// </summary>
        public static void DrawText(Graphics g, string text, Font font, Rectangle bounds, Color color,
            StringAlignment align = StringAlignment.Near,
            StringAlignment lineAlign = StringAlignment.Center,
            double alpha = 1.0)
        {
            if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0) return;

            Color c = WithAlpha(color, alpha);
            if (c.A == 0) return;

            using var brush = new SolidBrush(c);
            g.DrawString(text, font, brush, bounds, GetStringFormat(align, lineAlign));
        }

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) return p;

            if (radius <= 0)
            {
                p.AddRectangle(r);
                return p;
            }

            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;

            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRoundRect(Graphics g, Rectangle r, int radius, Color c, double alpha = 1.0)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using var brush = new SolidBrush(WithAlpha(c, alpha));
            using var path = RoundRect(r, radius);
            g.FillPath(brush, path);
        }

        public static void DrawBar(Graphics g, MetricItem item, Theme t, double alpha = 1.0)
        {
            if (item.BarRect.Width <= 0 || item.BarRect.Height <= 0) return;

            // Background
            using (var bgPath = RoundRect(item.BarRect, item.BarRect.Height / 2))
            using (var bgBrush = new SolidBrush(WithAlpha(ThemeManager.ParseColor(t.Color.BarBackground), alpha)))
            {
                g.FillPath(bgBrush, bgPath);
            }

            // 2. Value Bar
            // [Refactor] CachedPercent already includes visual correction (min 5%, max 100%) via MetricUtils.GetProgressValue
            double percent = item.CachedPercent; 
            
            int w = (int)(item.BarRect.Width * percent);
            if (w < 1 && percent > 0) w = 1; // 绝对防御：只要有百分比，至少画1像素

            // Color
            Color barColor = GetStateColor(item.CachedColorState, t, false);

            if (w > 0)
            {
                Rectangle bar = new Rectangle(item.BarRect.X, item.BarRect.Y, w, item.BarRect.Height);
                if (bar.Width > 0 && bar.Height > 0)
                {
                    using (var fgPath = RoundRect(bar, bar.Height / 2))
                    using (var brush = new SolidBrush(WithAlpha(barColor, alpha)))
                    {
                        g.FillPath(brush, fgPath);
                    }
                }
            }
        }
    }
}
