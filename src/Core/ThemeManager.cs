using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 布局配置：仅保留当前代码实际用到的字段
    /// - width:            窗体宽度（最终由 Settings.PanelWidth 覆盖）
    /// - rowHeight:        行高（各监控项基准高度）
    /// - padding:          画布外边距
    /// - cornerRadius:     窗体圆角（MainForm 应用）
    /// - groupRadius:      组块圆角（UIRenderer 应用）
    /// - groupPadding:     组块内边距
    /// - groupSpacing:     组块之间的垂直间距
    /// - groupBottom:      组块额外底部留白
    /// - itemGap:          监控项之间的垂直间距
    /// - groupTitleOffset: 组标题与块体的垂直微调
    /// </summary>
    public class LayoutConfig
    {
        public int Width { get; set; } = 240;//不会被实际使用，运行时由 Settings.PanelWidth 覆盖
        public float LayoutScale { get; set; } = 1.0f;//不会被实际使用，运行时由 Settings.PanelWidth 覆盖
        public int RowHeight { get; set; } = 40;
        public int Padding { get; set; } = 12;

        public int CornerRadius { get; set; } = 12;
        public int GroupRadius { get; set; } = 10;

        public int GroupPadding { get; set; } = 8;
        public int GroupSpacing { get; set; } = 30;
        public int GroupBottom { get; set; } = 0;

        public int ItemGap { get; set; } = 6;
        public int GroupTitleOffset { get; set; } = 6;

        

        public void Scale(float s)
        {
            if (s <= 0f || Math.Abs(s - 1f) < 0.01f)
                return;

            Width = (int)(Width * s);
            RowHeight = (int)(RowHeight * s);
            Padding = (int)(Padding * s);
            CornerRadius = (int)(CornerRadius * s);
            GroupRadius = (int)(GroupRadius * s);
            GroupPadding = (int)(GroupPadding * s);
            GroupSpacing = (int)(GroupSpacing * s);
            GroupBottom = (int)(GroupBottom * s);
            GroupTitleOffset = (int)(GroupTitleOffset * s);
            ItemGap = (int)(ItemGap * s);
        }


    }

    /// <summary>
    /// 字体配置：
    /// - family       : 文本主字体
    /// - valueFamily  : 数值字段单独字体（等宽可读性更好）
    /// - title/group/item/value: 四类字号
    /// - bold         : 是否加粗（四类统一按该值生效）
    /// - scale        : DPI/喜好缩放系数（0.5~3.0）
    /// </summary>
    public class FontConfig
    {
        public string Family { get; set; } = "Microsoft YaHei UI";
        public string ValueFamily { get; set; } = "Consolas";

        public double Title { get; set; } = 11.5;
        public double Group { get; set; } = 10.5;
        public double Item { get; set; } = 10.0;
        public double Value { get; set; } = 10.5;

        public bool Bold { get; set; } = true;
        public int ItemSize { get; internal set; }
    }

    /// <summary>
    /// 阈值定义（warn/crit），渲染中用于切换颜色。
    /// </summary>
    public class ThresholdSet
    {
        public double Warn { get; set; } = 70;
        public double Crit { get; set; } = 90;
    }

    /// <summary>
    /// 各类指标的阈值配置（按当前 UIRenderer 的使用保留）。
    /// </summary>
    public class ThresholdConfig
    {
        public ThresholdSet Load { get; set; } = new() { Warn = 65, Crit = 85 };
        public ThresholdSet Temp { get; set; } = new() { Warn = 50, Crit = 70 };
        public ThresholdSet Vram { get; set; } = new() { Warn = 65, Crit = 85 };
        public ThresholdSet Mem { get; set; } = new() { Warn = 65, Crit = 85 };
        public ThresholdSet NetKBps { get; set; } = new() { Warn = 2048 * 1024, Crit = 8192 * 1024 };
    }

    /// <summary>
    /// 颜色配置：只保留实际使用的颜色键
    /// - Background / GroupBackground
    /// - TextTitle / TextGroup / TextPrimary
    /// - ValueSafe / ValueWarn / ValueCrit
    /// - BarBackground / BarLow / BarMid / BarHigh
    /// </summary>
    public class ColorConfig
    {
        public string Background { get; set; } = "#202225";

        public string TextTitle { get; set; } = "#FFFFFF";
        public string TextGroup { get; set; } = "#B0B0B0";
        public string TextPrimary { get; set; } = "#EAEAEA";

        public string ValueSafe { get; set; } = "#66FF99";
        public string ValueWarn { get; set; } = "#FFD666";
        public string ValueCrit { get; set; } = "#FF6666";

        public string BarBackground { get; set; } = "#1C1C1C";
        public string BarLow { get; set; } = "#00C853";
        public string BarMid { get; set; } = "#FFAB00";
        public string BarHigh { get; set; } = "#D50000";

        public string GroupBackground { get; set; } = "#2B2D31";
    }

    /// <summary>
    /// Theme 主对象：聚合 Layout / Font / Threshold / Color。
    /// 运行期还会构建 4 类 Font 对象供渲染使用。
    /// </summary>
    public class Theme
    {
        public string Name { get; set; } = "Default";
        public int Version { get; set; } = 3;

        public LayoutConfig Layout { get; set; } = new();
        public FontConfig Font { get; set; } = new();
        public ThresholdConfig Thresholds { get; set; } = new();
        public ColorConfig Color { get; set; } = new();

        // 运行期字体（Json 忽略）
        [JsonIgnore] public Font FontTitle = SystemFonts.CaptionFont;
        [JsonIgnore] public Font FontGroup = SystemFonts.CaptionFont;
        [JsonIgnore] public Font FontItem = SystemFonts.CaptionFont;
        [JsonIgnore] public Font FontValue = SystemFonts.CaptionFont;
        [JsonIgnore] public Font FontTaskbar = SystemFonts.CaptionFont;

        // ===== 任务栏字体(写死硬编码，用来被调用) =====
        
        /// <summary>
        /// 构建 4 类字体。bold 对四类统一生效；scale 做软限制（0.5~3.0）。
        /// </summary>
        public void BuildFonts()
        {
            try
            {
                var style = Font.Bold ? FontStyle.Bold : FontStyle.Regular;
                // 使用 Point 单位作为加载时的默认构建（加载后通常会由 Scale 调整为正确像素）
                FontTitle = new Font(Font.Family, (float)Font.Title, style, GraphicsUnit.Point);
                FontGroup = new Font(Font.Family, (float)Font.Group, style, GraphicsUnit.Point);
                FontItem = new Font(Font.Family, (float)Font.Item, style, GraphicsUnit.Point);
                
                var valueFamily = string.IsNullOrWhiteSpace(Font.ValueFamily) ? Font.Family : Font.ValueFamily;
                FontValue = new Font(valueFamily, (float)Font.Value, style, GraphicsUnit.Point);
                // 任务栏字体默认与 Value 一致
                FontTaskbar = (FontValue != null) ? (FontValue.Clone() as Font) : FontValue;
            }
            catch
            {
                FontTitle = FontGroup = FontItem = FontValue = FontTaskbar = SystemFonts.CaptionFont;
            }
        }

        public void Scale(float dpiScale, float userScale)
        {
            // Layout 按最终缩放比例（dpi * user）调整
            Layout.LayoutScale = dpiScale * userScale;
            Layout.Scale(Layout.LayoutScale);

            var style = Font.Bold ? FontStyle.Bold : FontStyle.Regular;
            var valueFamily = string.IsNullOrWhiteSpace(Font.ValueFamily) ? Font.Family : Font.ValueFamily;
            float u = userScale <= 0 ? 1.0f : userScale;

            // ★★★ 关键：先销毁旧的缩放字体，防止句柄泄露 ★★★
            DisposeFonts();

            // 计算像素尺寸：
            // DPI (dots per inch) = dpiScale * 96
            // 点(point) 与像素的转换： pixels = points * (dpi / 72)
            float dpi = dpiScale * 96f;
            float pointToPixel = dpi / 72f; // = dpiScale * 96 / 72 = dpiScale * 4/3

            // finalPixelSize = basePointSize * pointToPixel * userScale
            try
            {
                FontTitle = new Font(Font.Family, (float)Font.Title * pointToPixel * u, style, GraphicsUnit.Pixel);
                FontGroup = new Font(Font.Family, (float)Font.Group * pointToPixel * u, style, GraphicsUnit.Pixel);
                FontItem = new Font(Font.Family, (float)Font.Item * pointToPixel * u, style, GraphicsUnit.Pixel);
                FontValue = new Font(valueFamily, (float)Font.Value * pointToPixel * u, style, GraphicsUnit.Pixel);

                // 任务栏字体按 Value 构建（可单独微调）
                FontTaskbar = (FontValue != null) ? (FontValue.Clone() as Font) : null ?? FontValue;
            }
            catch
            {
                // 回退到系统字体，避免程序崩溃
                FontTitle = FontGroup = FontItem = FontValue = FontTaskbar = SystemFonts.CaptionFont;
            }
        }
        public void DisposeFonts()
        {
            // 只有非系统字体才需要 Dispose
            if (FontTitle != null && !FontTitle.IsSystemFont) { try { FontTitle.Dispose(); } catch { } }
            if (FontGroup != null && !FontGroup.IsSystemFont) { try { FontGroup.Dispose(); } catch { } }
            if (FontItem != null && !FontItem.IsSystemFont) { try { FontItem.Dispose(); } catch { } }
            if (FontValue != null && !FontValue.IsSystemFont) { try { FontValue.Dispose(); } catch { } }
            if (FontTaskbar != null && !FontTaskbar.IsSystemFont) { try { FontTaskbar.Dispose(); } catch { } }
        }

    }


    /// <summary>
    /// 主题管理器：负责读取 JSON、反序列化、构建字体、暴露 Current。
    /// 注意：不在此处做清缓存；清缓存应在 UIController.ApplyTheme() 统一处理。
    /// </summary>
    public static class ThemeManager
    {
        public static Theme Current { get; private set; } = new Theme();
        
        // ★★★ 修改：移除 _stringPool，只保留 _colorCache ★★★
        // private static readonly Dictionary<string, string> _stringPool ... (删除)
        private static readonly Dictionary<string, Color> _colorCache = new(32);
        private static readonly object _lock = new object(); 

        public static string ThemeDir
        {
            get
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "resources/themes");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>
        /// 列出可用主题文件名（不含扩展名）
        /// </summary>
        public static IEnumerable<string> GetAvailableThemes()
        {
            try
            {
                return Directory.EnumerateFiles(ThemeDir, "*.json")
                                .Select(Path.GetFileNameWithoutExtension)
                                .OrderBy(n => n)
                                .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// 加载指定主题：读取 JSON → 反序列化 → 构建字体 → 设置 Current。
        /// </summary>
        public static Theme Load(string name)
        {
            try
            {
                var path = Path.Combine(ThemeDir, $"{name}.json");
                if (!File.Exists(path))
                    throw new FileNotFoundException("Theme json not found", path);

                var json = File.ReadAllText(path);
                var theme = JsonSerializer.Deserialize<Theme>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    IgnoreReadOnlyProperties = true,
                    AllowTrailingCommas = true
                });

                if (theme == null)
                    throw new Exception("Theme parse failed.");
                
                if (theme.Font != null)
                {
                    theme.Font.Family = UIUtils.Intern(theme.Font.Family);
                    if (!string.IsNullOrEmpty(theme.Font.ValueFamily))
                    {
                        theme.Font.ValueFamily = UIUtils.Intern(theme.Font.ValueFamily);
                    }
                }
                // 构建运行期字体（初始为 point 单位，后续 UIController 会调用 Scale 以按 DPI 重建为像素单位）
                theme.BuildFonts();

                Current = theme;
                Console.WriteLine($"[ThemeManager] Loaded theme: {theme.Name} (v{theme.Version})");

                return theme;
            }
            catch (Exception ex)
            {
                // 兜底主题，保证程序可继续运行
                Console.WriteLine($"[ThemeManager] Load error: {ex.Message}");
                var fallback = new Theme();
                fallback.BuildFonts();
                Current = fallback;
                return fallback;
            }
        }
        
        // ★★★ 修改：删除私有 Intern 方法 ★★★
        // private static string Intern(string str) ... (删除)

        public static Color ParseColor(string colorStr)
        {
            if (string.IsNullOrWhiteSpace(colorStr)) 
                return Color.Transparent;

            // ★★★ 修改：使用 UIUtils 的全局池 ★★★
            string key = UIUtils.Intern(colorStr);
            
            lock (_lock)
            {
                if (_colorCache.TryGetValue(key, out var cached))
                    return cached;
            }

            Color color;
            if (colorStr.StartsWith('#'))
            {
                ReadOnlySpan<char> hex = colorStr.AsSpan(1);
                try {
                    if (hex.Length == 6)
                    {
                        int r = Convert.ToInt32(hex.Slice(0, 2).ToString(), 16);
                        int g = Convert.ToInt32(hex.Slice(2, 2).ToString(), 16);
                        int b = Convert.ToInt32(hex.Slice(4, 2).ToString(), 16);
                        color = Color.FromArgb(r, g, b);
                    }
                    else if (hex.Length == 8)
                    {
                        int a = Convert.ToInt32(hex.Slice(0, 2).ToString(), 16);
                        int r = Convert.ToInt32(hex.Slice(2, 2).ToString(), 16);
                        int g = Convert.ToInt32(hex.Slice(4, 2).ToString(), 16);
                        int b = Convert.ToInt32(hex.Slice(6, 2).ToString(), 16);
                        color = Color.FromArgb(a, r, g, b);
                    }
                    else
                    {
                        color = Color.Transparent;
                    }
                } catch { color = Color.Transparent; }
            }
            else
            {
                color = Color.FromName(colorStr);
            }

            // 🔒 写缓存加锁
            lock (_lock)
            {
                _colorCache[key] = color;
            }
            return color;
        }

        public static void ClearCaches()
        {
            lock (_lock) // 🔒 加锁
            {
                _colorCache.Clear();
                // _stringPool.Clear(); (删除，这里不需要清理全局字符串，因为主题切换不影响硬件Key)
            }
            // ★★★ 新增：清理 UIUtils 的画刷缓存 (配合主题切换) ★★★
            UIUtils.ClearBrushCache(); 
        }
    }
}