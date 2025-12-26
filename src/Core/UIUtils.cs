using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
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
        private static readonly Dictionary<string, SolidBrush> _brushCache = new();

        /// <summary>
        /// 获取画刷的公共方法 (自动缓存)
        /// </summary>
        public static SolidBrush GetBrush(string color)
        {
            // 如果颜色字符串为空，返回透明或默认
            if (string.IsNullOrEmpty(color)) return (SolidBrush)Brushes.Transparent;

            if (!_brushCache.TryGetValue(color, out var br))
            {
                // 缓存未命中：创建并存入
                br = new SolidBrush(ThemeManager.ParseColor(color));
                _brushCache[color] = br;
            }
            return br;
        }

        /// <summary>
        /// 清理缓存的方法 (供外部切换主题时调用)
        /// </summary>
        public static void ClearBrushCache()
        {
            foreach (var b in _brushCache.Values) b.Dispose();
            _brushCache.Clear();
        }

        // ============================================================
        // 核心：通用数值格式化 (对外入口)
        // ============================================================
        public static string FormatValue(string key, float? raw)
        {
            string k = key.ToUpperInvariant();
            float v = raw ?? 0.0f;

            // 1. 内存/显存特殊显示逻辑 (必须放在第一位)
            if (k.Contains("MEM") || k.Contains("VRAM"))
            {
                // 1. 读取配置
                var cfg = Settings.Load();
                
                // 2. 判断模式：如果是 1 (已用容量)
                if (cfg.MemoryDisplayMode == 1) 
                {
                    double totalGB = 0;
                    // 获取对应的总容量 (从 Settings 静态变量)
                    if (k.Contains("MEM")) totalGB = Settings.DetectedRamTotalGB;
                    else if (k.Contains("VRAM")) totalGB = Settings.DetectedGpuVramTotalGB;

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

             // 2. 百分比类 (Load)
            if (k.Contains("LOAD")) 
                return $"{v:0.0}%";

            // 2. 温度类
            if (k.Contains("TEMP")) 
                return $"{v:0.0}°C";

            // 3. 频率类 (GHz / MHz)
            if (k.Contains("CLOCK"))
                // 逻辑优化：>=1000MHz 显示 GHz，否则显示 MHz
                //return v >= 1000 ? $"{v / 1000.0:F1}GHz" : $"{v:F0}MHz";
                return $"{v / 1000.0:F1}GHz";

            // 4. 功耗类 (W)
            if (k.Contains("POWER"))
                return $"{v:F0}W";

            // 5. 流量/速率类 (NET / DISK / DATA)
            // 复用 FormatDataSize 算法
            if (k.StartsWith("NET") || k.StartsWith("DISK"))
                return FormatDataSize(v, "/s"); // 速率带 /s

            
            if (k.StartsWith("DATA"))
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
                // 强制指定位数 (如 "0.0", "0.00")
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

            // 1. 去掉 "/s" (省空间)
            value = value.Replace("/s", "", StringComparison.OrdinalIgnoreCase).Trim();

            // 2. 拆分解析数值和单位 ，过滤非数字+单位的字符
            var m = Regex.Match(value, @"^([\d.]+)([A-Za-z%°℃]+)$");
            if (!m.Success) return value;

            double num = double.Parse(m.Groups[1].Value);
            string unit = m.Groups[2].Value;

            // 3. 智能缩略：如果数字过大 (>=100)，去掉小数位
            // 例如: "123.4MB" -> "123MB", "99.5MB" -> "99.5MB"
            return num >= 100
                ? ((int)Math.Round(num)) + unit
                : num.ToString("0.0") + unit;
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

        /// <summary>
        /// 核心：计算当前指标处于哪个报警级别 (0=Safe, 1=Warn, 2=Crit)
        /// </summary>
        public static int GetColorResult(string key, double value)
        {
            if (double.IsNaN(value)) return 0;

            string k = key.ToUpperInvariant();

            // 1. Adaptive (频率/功耗要转化成使用率数值)
            if (k.Contains("CLOCK") || k.Contains("POWER"))
            {
                value = GetAdaptivePercentage(key, value) * 100;
            }

            // 2. 使用 GetThresholds 获取阈值
            var (warn, crit) = GetThresholds(key); // GetThresholds 内部已处理 NET/DISK 分离
            
            // 3.NET/DISK 特殊处理：将 B/s 转换为 KB/s
            if (k.StartsWith("NET") || k.StartsWith("DISK") || k.Contains("DATA"))
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
            string k = key.ToUpperInvariant();
            var th = cfg.Thresholds;

            // Load, VRAM, Mem，CLOCK/POWER
            if (k.Contains("LOAD") || k.Contains("VRAM") || k.Contains("MEM")||k.Contains("CLOCK") || k.Contains("POWER"))
                return (th.Load.Warn, th.Load.Crit);
            
            // Temp
            if (k.Contains("TEMP"))
                return (th.Temp.Warn, th.Temp.Crit);

            // Disk R/W (共享阈值)
            if (k.StartsWith("DISK"))
                return (th.DiskIOMB.Warn, th.DiskIOMB.Crit);

            // NET Up/Down (分离阈值)
            if (k.StartsWith("NET"))
            {
                if (k.Contains("UP"))
                    return (th.NetUpMB.Warn, th.NetUpMB.Crit);
                else // NET.DOWN
                    return (th.NetDownMB.Warn, th.NetDownMB.Crit);
            }

            if (k.Contains("DATA"))
            {
                if (k.Contains("UP"))
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
            
            // ★★★ 修复：如果半径 <= 0，直接添加直角矩形并返回，防止 Crash ★★★
            if (radius <= 0)
            {
                p.AddRectangle(r);
                return p;
            }

            int d = radius * 2;
            
            // 防御性编程：如果圆角直径比矩形还大，限制它
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
            using var brush = new SolidBrush(c);
            using var path = RoundRect(r, radius);
            g.FillPath(brush, path);
        }

        // ============================================================
        // ⑤ 完整进度条 (恢复最低 5% 版本)
        // ★★★ 优化：复用 GetBrush 方法 ★★★
        // ============================================================
        public static void DrawBar(Graphics g, Rectangle bar, double value, string key, Theme t)
        {
            // 1. 绘制背景槽 - 使用缓存画刷
            using (var bgPath = RoundRect(bar, bar.Height / 2))
            {
                g.FillPath(GetBrush(t.Color.BarBackground), bgPath);
            }

            // =========================================================
            // ★★★ 优化核心：一次计算，两处使用 ★★★
            // =========================================================
            string k = key.ToUpperInvariant();
            double percent;

            // A. 统一计算进度百分比 (0.0 ~ 1.0)
            // ---------------------------------------------------------
            if (k.Contains("CLOCK") || k.Contains("POWER"))
            {
                // 复用 GetAdaptivePercentage (内部封装了读取 Settings 和 Max 的逻辑)
                // 避免了在 DrawBar 里重写一遍 Settings 读取代码
                percent = GetAdaptivePercentage(key, value);
            }
            else
            {
                // 普通数据 (Load/Temp/Mem) 默认为 0-100，直接除以 100 归一化
                percent = value / 100.0;
            }

            // B. 确定颜色 (就地判断，避免调用 GetColorResult 导致重复计算)
            // ---------------------------------------------------------
            // 逻辑：将 0~1 的 percent 还原为 0~100 的数值，与配置文件的阈值 (如 60, 85) 对比
            // 这样既省去了计算，又保证了颜色策略与 GetColorResult 保持完全一致
            var (warn, crit) = GetThresholds(key);
            double valForCheck = percent * 100.0;

            string colorCode;
            if (valForCheck >= crit) colorCode = t.Color.BarHigh;
            else if (valForCheck >= warn) colorCode = t.Color.BarMid;
            else colorCode = t.Color.BarLow;

            // C. 绘制前景条
            // ---------------------------------------------------------
            // 限制范围 5% ~ 100% (视觉优化)
            percent = Math.Max(0.05, Math.Min(1.0, percent));

            int w = (int)(bar.Width * percent);
            // 确保至少有 2px 宽度，避免圆角绘制异常
            if (w < 2) w = 2;

            if (w > 0)
            {
                var filled = new Rectangle(bar.X, bar.Y, w, bar.Height);
                
                // 简单防越界
                if (filled.Width > bar.Width) filled.Width = bar.Width;

                using (var fgPath = RoundRect(filled, filled.Height / 2))
                {
                    // 优化：使用缓存的前景画刷
                    g.FillPath(GetBrush(colorCode), fgPath);
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
    }
}