using System.Diagnostics;
using System.Text.Json;

namespace LiteMonitor.src.Core
{
    // === 数据模型 ===
    public class AppTrafficData
    {
        // "2026-11-18" -> "chrome.exe" -> 流量
        public Dictionary<string, Dictionary<string, DailyRecord>> History { get; set; } = new();
        // "chrome.exe" -> 完整路径 (用于图标 / 打开文件位置)
        public Dictionary<string, string> Paths { get; set; } = new();
    }

    /// <summary>
    /// 按应用的流量统计 (数据来自 AppTrafficTracer)。
    /// 每日按程序名累计，保存到 AppTrafficHistory.json；另维护每秒实时速度。
    /// </summary>
    public static class AppTrafficLogger
    {

        private static readonly string _filePath = Path.Combine(AppContext.BaseDirectory, "AppTrafficHistory.json");
        private static readonly string _backupPath = _filePath + ".bak";
        private static readonly string _tempPath = _filePath + ".tmp";
        private static readonly object _lock = new();
        private static readonly object _ioLock = new();

        private static AppTrafficData _data = new();
        private static Dictionary<string, DailyRecord>? _today;   // 当日字典缓存，避免每个事件查日期
        private static string _todayKey = "";
        private static DateTime _todayDate;
        private static bool _dirty;

        // 实时速度：当前秒累加 → Tick() 时转为 bytes/s
        private static Dictionary<string, DailyRecord> _tick = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, DailyRecord> _rate = new(StringComparer.OrdinalIgnoreCase);
        private static long _lastTick = Environment.TickCount64;

        public static void Load()
        {
            lock (_ioLock)
            {
                var loaded = TryLoad(_filePath) ?? TryLoad(_backupPath) ?? new AppTrafficData();
                loaded.History ??= new();
                loaded.Paths = new Dictionary<string, string>(loaded.Paths ?? new(), StringComparer.OrdinalIgnoreCase);
                foreach (var key in loaded.History.Keys.ToList())
                    loaded.History[key] = new Dictionary<string, DailyRecord>(loaded.History[key] ?? new(), StringComparer.OrdinalIgnoreCase);
                lock (_lock) { _data = loaded; _today = null; }
            }
        }

        private static AppTrafficData? TryLoad(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<AppTrafficData>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppTrafficLogger] 读取失败: {path}, {ex.Message}");
                return null;
            }
        }

        /// <summary>由 ETW 线程调用，热路径：一次加锁、两次字典查找。</summary>
        public static void Add(string app, long up, long down)
        {
            lock (_lock)
            {
                var today = TodayUnsafe();
                if (!today.TryGetValue(app, out var r)) today[app] = r = new DailyRecord();
                r.Upload += up; r.Download += down;

                if (!_tick.TryGetValue(app, out var t)) _tick[app] = t = new DailyRecord();
                t.Upload += up; t.Download += down;
                _dirty = true;
            }
        }

        public static void SetPath(string app, string path)
        {
            lock (_lock)
            {
                if (_data.Paths.TryGetValue(app, out var old) && old == path) return;
                _data.Paths[app] = path;
                _dirty = true;
            }
        }

        public static string? GetPath(string app)
        {
            lock (_lock) return _data.Paths.TryGetValue(app, out var p) ? p : null;
        }

        /// <summary>每秒调用一次 (HardwareMonitor 维护循环)，计算各应用实时速度。</summary>
        public static void Tick()
        {
            lock (_lock)
            {
                long now = Environment.TickCount64;
                double secs = Math.Max(0.2, (now - _lastTick) / 1000.0);
                _lastTick = now;
                if (_tick.Count == 0 && _rate.Count == 0) return;

                var rate = new Dictionary<string, DailyRecord>(_tick.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var (app, t) in _tick)
                    rate[app] = new DailyRecord { Upload = (long)(t.Upload / secs), Download = (long)(t.Download / secs) };
                _rate = rate;
                _tick = new Dictionary<string, DailyRecord>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public static (long up, long down) GetSpeed(string app)
        {
            lock (_lock) return _rate.TryGetValue(app, out var r) ? (r.Upload, r.Download) : (0, 0);
        }

        public static (long up, long down) GetTotalSpeed()
        {
            lock (_lock)
            {
                long u = 0, d = 0;
                foreach (var r in _rate.Values) { u += r.Upload; d += r.Download; }
                return (u, d);
            }
        }

        /// <summary>汇总 [from, to] (含) 日期区间内各应用流量，按总量降序。</summary>
        public static List<(string App, long Up, long Down)> Query(DateTime from, DateTime to)
        {
            string f = from.ToString("yyyy-MM-dd"), t = to.ToString("yyyy-MM-dd");
            var sum = new Dictionary<string, (long Up, long Down)>(StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                foreach (var (day, apps) in _data.History)
                {
                    if (string.CompareOrdinal(day, f) < 0 || string.CompareOrdinal(day, t) > 0) continue;
                    foreach (var (app, r) in apps)
                    {
                        sum.TryGetValue(app, out var s);
                        sum[app] = (s.Up + r.Upload, s.Down + r.Download);
                    }
                }
            }
            return sum.Select(kv => (kv.Key, kv.Value.Up, kv.Value.Down))
                      .OrderByDescending(x => x.Up + x.Down).ToList();
        }

        public static DateTime? FirstDay()
        {
            lock (_lock)
            {
                string? min = _data.History.Keys.Min();
                return min != null && DateTime.TryParse(min, out var d) ? d : null;
            }
        }

        private static Dictionary<string, DailyRecord> TodayUnsafe()
        {
            var date = DateTime.Today;
            if (_today == null || date != _todayDate)
            {
                _todayDate = date;
                _todayKey = date.ToString("yyyy-MM-dd");
                if (!_data.History.TryGetValue(_todayKey, out _today))
                    _data.History[_todayKey] = _today = new Dictionary<string, DailyRecord>(StringComparer.OrdinalIgnoreCase);
            }
            return _today;
        }

        public static void Save()
        {
            string json;
            lock (_lock)
            {
                if (!_dirty) return;
                json = JsonSerializer.Serialize(_data);
                _dirty = false;
            }

            lock (_ioLock)
            {
                try
                {
                    File.WriteAllText(_tempPath, json);
                    if (File.Exists(_filePath))
                        try { File.Copy(_filePath, _backupPath, true); } catch { }
                    File.Move(_tempPath, _filePath, true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AppTrafficLogger] 保存失败: {ex.Message}");
                    lock (_lock) _dirty = true;
                }
            }
        }
    }
}
