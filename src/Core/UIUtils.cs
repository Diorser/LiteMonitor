using System.Drawing.Drawing2D;
using System.Collections.Generic; // 补全引用
using System.Drawing; // 补全引用
using System.Linq; // 补全引用
using System; // 补全引用

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// LiteMonitor 的公共 UI 工具库（所有渲染器可用）
    /// </summary>
    public static class UIUtils
    {
        // ============================================================
        // ★★★ 新增：全局字符串驻留池 (内存优化 T1) ★★★
        // ============================================================
        private static readonly Dictionary<string, string> _stringPool = new(StringComparer.Ordinal);
        private static readonly object _poolLock = new object();

        /// <summary>
        /// 全局字符串驻留：如果池子里有一样的字符串，就返回池子里的引用，丢弃当前的。
        /// </summary>
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

        /// <summary>
        /// 清空字符串池 (建议在重置硬件服务时调用)
        /// </summary>
        public static void ClearStringPool()
        {
            lock (_poolLock) _stringPool.Clear();
        }

        // ============================================================
        // ★★★ 新增：DPI 适配工具 ★★★
        // ============================================================
        public static float ScaleFactor { get; set; } = 1.0f;

        // 核心辅助方法：将设计时的像素值转换为当前 DPI 下的像素值
        public static int S(int px) => (int)(px * ScaleFactor);
        public static float S(float px) => px * ScaleFactor;
        public static Size S(Size size) => new Size(S(size.Width), S(size.Height));
        public static Padding S(Padding p) => new Padding(S(p.Left), S(p.Top), S(p.Right), S(p.Bottom));

        // ============================================================
        // ★★★ 优化：画刷缓存机制下沉到此处 ★★★
        // ============================================================
        private static readonly Dictionary<string, SolidBrush> _brushCache = new(16);
        private static readonly object _brushLock = new object(); // 🔒 线程锁
        private static readonly Dictionary<string, Font> _fontCache = new(16); // 字体缓存
        private const int MAX_BRUSH_CACHE = 32;

        /// <summary>
        /// 获取画刷的公共方法 (自动缓存)
        /// </summary>
        public static SolidBrush GetBrush(string color)
        {
            if (string.IsNullOrEmpty(color))
                return (SolidBrush)Brushes.Transparent;

            lock (_brushLock) // 🔒 整个过程加锁
            {
                if (!_brushCache.TryGetValue(color, out var br))
                {
                    // ★★★ 防止缓存无限增长 ★★★
                    if (_brushCache.Count >= MAX_BRUSH_CACHE)
                    {
                        // 优化：先 ToList 获取 Keys 副本，再安全删除，避免 "集合已修改" 异常
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

        /// <summary>
        /// 清理缓存的方法 (供外部切换主题时调用)
        /// </summary>
        public static void ClearBrushCache()
        {
            lock (_brushLock) // 🔒 加锁
            {
                foreach (var b in _brushCache.Values) b.Dispose();
                _brushCache.Clear();
                
                foreach (var f in _fontCache.Values) f.Dispose();
                _fontCache.Clear();
            }
        }

        // ============================================================
        // 核心：通用数值格式化 (对外入口)
        // ============================================================
        public static string FormatValue(string key, float? raw)
        {
            // ★★★ 优化：消除 ToUpperInvariant，改用 IndexOf 忽略大小写 ★★★
            // string k = key.ToUpperInvariant();
            float v = raw ?? 0.0f;

            // 1. 内存/显存特殊显示逻辑 (必须放在第一位)
            if (key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("VRAM", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // 1. 读取配置 (注意：此处 Settings.Load() 现在是单例极速模式)
                var cfg = Settings.Load();

                // 2. 判断模式：如果是 1 (已用容量)
                if (cfg.MemoryDisplayMode == 1)
                {
                    double totalGB = 0;
                    // 获取对应的总容量 (从 Settings 静态变量)
                    if (key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0) totalGB = Settings.DetectedRamTotalGB;
                    else if (key.IndexOf("VRAM", StringComparison.OrdinalIgnoreCase) >= 0) totalGB = Settings.DetectedGpuVramTotalGB;

                    // 只有当探测到了有效容量时，才进行转换
                    if (totalGB > 0)
                    {
                        // 计算：(百分比 / 100) * 总GB = 已用GB
                        double usedGB = (v / 100.0) * totalGB;

                        // 转成 Bytes 喂给 FormatDataSize
                        double usedBytes = usedGB * 1024.0 * 1024.0 * 1024.0;

                        // 这里的 1 表示强制保留 1 位小数 (如 12.5GB)
                        return FormatDataSize(usedBytes, "", 1);
                    }
                }

                // 模式为 0 (百分比)，或者还没探测到总容量 -> 回落显示百分比
                return $"{v:0.0}%";
            }
            // ★★★ [新增] 在这里插入 FPS 格式化逻辑 ★★★
            if (key == "FPS") return $"{v:0} FPS";
            
            // 2. 百分比类 (Load)
            if (key.IndexOf("LOAD", StringComparison.OrdinalIgnoreCase) >= 0)
                return $"{v:0.0}%";

            // 2. 温度类
            if (key.IndexOf("TEMP", StringComparison.OrdinalIgnoreCase) >= 0)
                return $"{v:0.0}°C";

            // ★★★ [新增] 风扇支持 ★★★
            if (key.IndexOf("FAN", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("PUMP", StringComparison.OrdinalIgnoreCase) >= 0) return $"{v:0} RPM";

            // 3. 频率类 (GHz / MHz)
            if (key.IndexOf("CLOCK", StringComparison.OrdinalIgnoreCase) >= 0)
                // 逻辑优化：>=1000MHz 显示 GHz，否则显示 MHz
                //return v >= 1000 ? $"{v / 1000.0:F1}GHz" : $"{v:F0}MHz";
                return $"{v / 1000.0:F1}GHz";

            // 4. 功耗类 (W)
            if (key.IndexOf("POWER", StringComparison.OrdinalIgnoreCase) >= 0)
                return $"{v:F0}W";

            // 5. 流量/速率类 (NET / DISK / DATA)
            // 复用 FormatDataSize 算法
            if (key.StartsWith("NET", StringComparison.OrdinalIgnoreCase) || 
                key.StartsWith("DISK", StringComparison.OrdinalIgnoreCase))
                return FormatDataSize(v, "/s"); // 速率带 /s


            if (key.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                return FormatDataSize(v, "");   // 总量不带 /s

            return $"{v:0.0}";
        }

        // ============================================================
        // ★★★ 核心算法：统一的字节单位换算 ★★★
        // ============================================================
        // decimals: 
        //    -1 (默认): 智能模式 (KB/MB显示1位, GB+显示2位)
        //     0: 不显示小数 (如 12GB)
        //     1: 强制1位 (如 12.5GB)
        //     2: 强制2位 (如 12.55GB)
        public static string FormatDataSize(double bytes, string suffix = "", int decimals = -1)
        {
            string[] sizes = { "KB", "MB", "GB", "TB", "PB" };
            double len = bytes;
            int order = 0;

            // 初始就转换为 KB
            len /= 1024.0;

            // 自动升级单位 (>= 1024)
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024.0;
            }

            // ★★★ 核心修改：支持默认智能模式，也支持强制指定 ★★★
            string format;

            if (decimals < 0)
            {
                // 默认逻辑 (decimals = -1)
                // KB(0), MB(1) -> 保留 1 位 ("0.0")
                // GB(2) 及以上 -> 保留 2 位 ("0.00")
                format = order <= 1 ? "0.0" : "0.00";
            }
            else if (decimals == 0)
            {
                // 强制整数
                format = "0";
            }
            else
            {
                // 强制指个位数 (如 "0.0", "0.00")
                format = "0." + new string('0', decimals);
            }

            return $"{len.ToString(format)}{sizes[order]}{suffix}";
        }

        // ============================================================
        // 横屏模式专用：极简格式化 (UI 逻辑)
        // ============================================================
        public static string FormatHorizontalValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;

            // 1. 快速预处理
            // 这里的 Replace 虽然也产生新字符串，但比 Regex 轻量。
            // 如果追求极致，可以在 FormatValue 阶段就处理好，但这里先不动架构。
            string clean = value.Replace("/s", "").Trim();

            // 2. 手动寻找数字与单位的分界线 (替代 Regex)
            int splitIndex = -1;
            for (int i = 0; i < clean.Length; i++)
            {
                char c = clean[i];
                // 遇到第一个非数字且非小数点的字符，就是单位的开始
                if (!char.IsDigit(c) && c != '.' && c != '-') 
                {
                    splitIndex = i;
                    break;
                }
            }

            // 如果没找到单位，或没有数字，直接返回
            if (splitIndex <= 0) return clean;

            // 3. 分割字符串
            string numStr = clean.Substring(0, splitIndex);
            string unit = clean.Substring(splitIndex).Trim();

            // 4. 解析数值
            if (double.TryParse(numStr, out double num))
            {
                // ★★★ 风扇单位特殊处理 ★★★
                if (unit.Equals("RPM", StringComparison.OrdinalIgnoreCase))
                {
                    return ((int)Math.Round(num)).ToString() + "R";
                }

                // 智能缩略：>=100 去掉小数
                return num >= 100
                    ? ((int)Math.Round(num)) + unit
                    : numStr + unit; // 如果原本就是 12.5，直接用原字符串拼接，避免 ToString 再次由浮点误差导致变动
            }

            return clean;
        }

        // ============================================================
        // ③ 统一颜色选择
        // ============================================================
        public static Color GetColor(string key, double value, Theme t, bool isValueText = true)
        {
            if (double.IsNaN(value)) return ThemeManager.ParseColor(t.Color.TextPrimary);

            // 调用核心逻辑
            int result = GetColorResult(key, value);

            if (result == 2) return ThemeManager.ParseColor(isValueText ? t.Color.ValueCrit : t.Color.BarHigh);
            if (result == 1) return ThemeManager.ParseColor(isValueText ? t.Color.ValueWarn : t.Color.BarMid);
            return ThemeManager.ParseColor(t.Color.ValueSafe);
        }

        // [新增] 名字明确，专门用于“已知状态，求颜色”
        // 这里的 int state 就是 0(Safe), 1(Warn), 2(Crit)
        public static Color GetStateColor(int state, Theme t, bool isValueText = true)
        {
            if (state == 2) return ThemeManager.ParseColor(isValueText ? t.Color.ValueCrit : t.Color.BarHigh);
            if (state == 1) return ThemeManager.ParseColor(isValueText ? t.Color.ValueWarn : t.Color.BarMid);
            return ThemeManager.ParseColor(t.Color.ValueSafe);
        }

        /// <summary>
        /// 核心：计算当前指标处于哪个报警级别 (0=Safe, 1=Warn, 2=Crit)
        /// </summary>
        public static int GetColorResult(string key, double value)
        {
            if (double.IsNaN(value)) return 0;

            // ★★★ 优化：消除 ToUpperInvariant，改用 IndexOf 忽略大小写 ★★★
            // string k = key.ToUpperInvariant();

            // 1. Adaptive (频率/功耗要转化成使用率数值)
            // ★★★ [新增] 风扇支持 ★★★
            if (key.IndexOf("CLOCK", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("POWER", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("FAN", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("PUMP", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                value = GetAdaptivePercentage(key, value) * 100;
            }

            // 2. 使用 GetThresholds 获取阈值
            var (warn, crit) = GetThresholds(key); // GetThresholds 内部已处理 NET/DISK 分离

            // 3.NET/DISK 特殊处理：将 B/s 转换为 KB/s
            if (key.StartsWith("NET", StringComparison.OrdinalIgnoreCase) || 
                key.StartsWith("DISK", StringComparison.OrdinalIgnoreCase) || 
                key.IndexOf("DATA", StringComparison.OrdinalIgnoreCase) >= 0)
                value /= 1024.0 * 1024.0;

            if (value >= crit) return 2; // Crit
            if (value >= warn) return 1; // Warn

            return 0; // Safe
        }


        // ============================================================
        // ② 阈值解析（各类指标）
        // ============================================================
        public static (double warn, double crit) GetThresholds(string key)
        {
            var cfg = Settings.Load();
            // ★★★ 优化：消除 ToUpperInvariant，改用 IndexOf 忽略大小写 ★★★
            // string k = key.ToUpperInvariant();
            var th = cfg.Thresholds;

            // Load, VRAM, Mem，CLOCK/POWER，★ FAN
            if (key.IndexOf("LOAD", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("VRAM", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("CLOCK", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("POWER", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("FAN", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("PUMP", StringComparison.OrdinalIgnoreCase) >= 0)
                return (th.Load.Warn, th.Load.Crit);

            // Temp
            if (key.IndexOf("TEMP", StringComparison.OrdinalIgnoreCase) >= 0)
                return (th.Temp.Warn, th.Temp.Crit);

            // Disk R/W (共享阈值)
            if (key.StartsWith("DISK", StringComparison.OrdinalIgnoreCase))
                return (th.DiskIOMB.Warn, th.DiskIOMB.Crit);

            // NET Up/Down (分离阈值)
            if (key.StartsWith("NET", StringComparison.OrdinalIgnoreCase))
            {
                if (key.IndexOf("UP", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (th.NetUpMB.Warn, th.NetUpMB.Crit);
                else // NET.DOWN
                    return (th.NetDownMB.Warn, th.NetDownMB.Crit);
            }

            if (key.IndexOf("DATA", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (key.IndexOf("UP", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (th.DataUpMB.Warn, th.DataUpMB.Crit);
                else // DATA.DOWN
                    return (th.DataDownMB.Warn, th.DataDownMB.Crit);
            }

            return (th.Load.Warn, th.Load.Crit);
        }


        // ============================================================
        // ④ 通用图形
        // ============================================================
        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();

            // ★★★ [CRITICAL FIX] 防止宽度/高度 <= 0 导致的 Crash ★★★
            // GDI+ 的 AddArc 如果遇到宽或高为 0 会抛出 ArgumentException
            if (r.Width <= 0 || r.Height <= 0)
            {
                // 返回空路径（不绘制任何东西），安全的退出
                return p;
            }

            // ★★★ 修复：如果半径 <= 0，直接添加直角矩形并返回，防止 Crash ★★★
            if (radius <= 0)
            {
                p.AddRectangle(r);
                return p;
            }

            int d = radius * 2;

            // 防御性编程：如果圆角直径比矩形还大，限制它
            // 此时如果 d 变成了 0（因为 width 是 0），下面的 AddArc 依然会崩，
            // 所以最上面的 Width <= 0 判断非常重要。
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;

            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRoundRect(Graphics g, Rectangle r, int radius, Color c)
        {
            // ★★★ [CRITICAL FIX] 提前拦截无效矩形，避免无谓的资源创建和异常 ★★★
            if (r.Width <= 0 || r.Height <= 0) return;

            using var brush = new SolidBrush(c);
            using var path = RoundRect(r, radius);
            g.FillPath(brush, path);
        }
        // [新增] 统一获取进度条的百分比 (0.0 ~ 1.0)
        // 把那段 if/else 逻辑搬到这里
        public static double GetUnifiedPercent(string key, double value)
        {
            // 1. 自适应指标 (频率/功耗/风扇) -> 调用之前的 GetAdaptivePercentage
            if (key.IndexOf("CLOCK", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("POWER", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("FAN", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("PUMP", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GetAdaptivePercentage(key, value);
            }
            
            // 2. 普通指标 (Load/Temp/Mem) -> 默认 0-100 归一化
            return value / 100.0;
        }

        // [修改] DrawBar 彻底瘦身：只负责画，不负责算
        public static void DrawBar(Graphics g, MetricItem item, Theme t)
        {
            if (item.BarRect.Width <= 0 || item.BarRect.Height <= 0) return;

            // 1. 背景
            using (var bgPath = RoundRect(item.BarRect, item.BarRect.Height / 2))
            {
                g.FillPath(GetBrush(t.Color.BarBackground), bgPath);
            }

            // 2. ★★★ 直接使用缓存的百分比，零计算！ ★★★
            double percent = item.CachedPercent; 

            // 视觉微调：限制在 5% - 100% 之间
            percent = Math.Max(0.05, Math.Min(1.0, percent));

            int w = (int)(item.BarRect.Width * percent);
            if (w < 2) w = 2;

            // 3. 直接使用缓存的颜色状态，零计算！
            Color barColor = GetStateColor(item.CachedColorState, t, false);

            if (w > 0)
            {
                var filled = new Rectangle(item.BarRect.X, item.BarRect.Y, w, item.BarRect.Height);
                if (filled.Width > 0 && filled.Height > 0)
                {
                    using (var fgPath = RoundRect(filled, filled.Height / 2))
                    using (var brush = new SolidBrush(barColor))
                    {
                        g.FillPath(brush, fgPath);
                    }
                }
            }
        }

        // ============================================================
        // 辅助：获取自适应百分比 (从 Settings 读取)
        // ============================================================
        public static double GetAdaptivePercentage(string key, double val)
        {
            var cfg = Settings.Load();
            float max = 1.0f;

            if (key == "CPU.Clock") max = cfg.RecordedMaxCpuClock;
            else if (key == "CPU.Power") max = cfg.RecordedMaxCpuPower;
            else if (key == "GPU.Clock") max = cfg.RecordedMaxGpuClock;
            else if (key == "GPU.Power") max = cfg.RecordedMaxGpuPower;
            // ★★★ [新增] 风扇支持 ★★★
            else if (key == "CPU.Fan") max = cfg.RecordedMaxCpuFan;
            else if (key == "CPU.Pump") max = cfg.RecordedMaxCpuPump;
            else if (key == "CASE.Fan") max = cfg.RecordedMaxChassisFan;
            else if (key == "GPU.Fan") max = cfg.RecordedMaxGpuFan;

            if (max < 1) max = 1;
            double pct = val / max;
            return pct > 1.0 ? 1.0 : pct;
        }

        public static int ParseInt(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            string clean = new string(s.Where(c => char.IsDigit(c) || c == '-').ToArray());
            return int.TryParse(clean, out int v) ? v : 0;
        }

        public static double ParseDouble(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            // 允许小数点
            string clean = new string(s.Where(c => char.IsDigit(c) || c == '.' || c == '-').ToArray());
            return double.TryParse(clean, out double v) ? v : 0;
        }

        // 浮点数转显示字符串（统一格式）
        public static string ToStr(double v, string format = "F1") => v.ToString(format);

        // 3. 在类末尾（或其他合适位置）添加 GetFont 方法
        // ====== 新增整个方法 ======
        public static Font GetFont(string familyName, float size, bool bold)
        {
            string key = $"{familyName}_{size}_{bold}";
            lock (_brushLock) // 复用锁
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
                        // 兜底：防止字体不存在导致崩溃
                        font = new Font(SystemFonts.DefaultFont.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular);
                    }
                    _fontCache[key] = font;
                }
                return font;
            }
        }
    }
}