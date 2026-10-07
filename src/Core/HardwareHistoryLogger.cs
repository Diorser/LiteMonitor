using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace LiteMonitor.src.Core
{
    public class HardwareHistoryData
    {
        public List<HardwareHistoryPoint> Points { get; set; } = new List<HardwareHistoryPoint>();
    }

    public class HardwareHistoryPoint
    {
        public DateTime Time { get; set; }
        public Dictionary<string, HardwareHistoryValue> Values { get; set; } = new Dictionary<string, HardwareHistoryValue>();
    }

    public class HardwareHistoryValue
    {
        public float Avg { get; set; }
        public float Max { get; set; }
        public float Min { get; set; }
        public int Count { get; set; }
    }

    public static class HardwareHistoryLogger
    {
        public static readonly string[] TemperatureKeys = { "CPU.Temp", "GPU.Temp", "DISK.Temp", "MOBO.Temp" };
        public static readonly string[] LoadKeys = { "CPU.Load", "GPU.Load", "MEM.Load", "MEM.Virtual" };
        public static readonly string[] FrequencyKeys = { "CPU.Clock", "GPU.Clock" };
        public static readonly string[] PowerKeys = { "CPU.Power", "GPU.Power", "BAT.Power" };
        public static readonly string[] FpsKeys = { "FPS" };

        private static readonly string _filePath = Path.Combine(AppContext.BaseDirectory, "HardwareHistory.json");
        private static readonly string _backupPath = Path.Combine(AppContext.BaseDirectory, "HardwareHistory.json.bak");
        private static readonly string _tempPath = Path.Combine(AppContext.BaseDirectory, "HardwareHistory.json.tmp");
        private static readonly object _dataLock = new object();
        private static readonly object _ioLock = new object();
        private static readonly TimeSpan _sampleInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan _retention = TimeSpan.FromDays(7);
        private static readonly TimeSpan _saveInterval = TimeSpan.FromMinutes(5);

        private static readonly HashSet<string> _supportedKeys = new HashSet<string>(
            TemperatureKeys.Concat(LoadKeys).Concat(FrequencyKeys).Concat(PowerKeys).Concat(FpsKeys),
            StringComparer.OrdinalIgnoreCase);

        private static DateTime _lastSampleTime = DateTime.MinValue;
        private static DateTime _currentMinute = DateTime.MinValue;
        private static DateTime _lastSaveTime = DateTime.MinValue;
        private static readonly Dictionary<string, Accumulator> _currentBucket = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);
        private static bool _saveRunning;

        public static HardwareHistoryData Data { get; private set; } = new HardwareHistoryData();

        public static IEnumerable<string> SupportedKeys => _supportedKeys;

        public static void Load()
        {
            lock (_ioLock)
            {
                var loaded = TryLoadFile(_filePath) ?? TryLoadFile(_backupPath) ?? new HardwareHistoryData();
                loaded.Points ??= new List<HardwareHistoryPoint>();
                foreach (var point in loaded.Points)
                {
                    point.Values ??= new Dictionary<string, HardwareHistoryValue>();
                }
                lock (_dataLock)
                {
                    Data = loaded;
                    TrimUnsafe(DateTime.Now);
                }
            }
        }

        public static void RecordSnapshot(Settings cfg, Func<string, float?> readValue)
        {
            if (cfg?.MonitorItems == null || readValue == null) return;

            DateTime now = DateTime.Now;
            if (now - _lastSampleTime < _sampleInterval) return;
            _lastSampleTime = now;

            DateTime minute = FloorMinute(now);
            var enabledKeys = GetEnabledSupportedKeys(cfg).ToList();
            if (enabledKeys.Count == 0) return;

            lock (_dataLock)
            {
                if (_currentMinute == DateTime.MinValue)
                {
                    _currentMinute = minute;
                }
                else if (minute > _currentMinute)
                {
                    FlushCurrentBucketUnsafe();
                    _currentMinute = minute;
                }

                foreach (string key in enabledKeys)
                {
                    float? value;
                    try { value = readValue(key); }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[HardwareHistoryLogger] 读取硬件趋势采样失败: {key}, {ex.Message}");
                        continue;
                    }

                    float rawValue = value.GetValueOrDefault();
                    if (!value.HasValue || !IsValidValue(key, rawValue)) continue;

                    if (!_currentBucket.TryGetValue(key, out var acc))
                    {
                        acc = new Accumulator();
                        _currentBucket[key] = acc;
                    }
                    acc.Add(rawValue);
                }
            }

            if (now - _lastSaveTime >= _saveInterval)
            {
                _lastSaveTime = now;
                SaveAsync();
            }
        }

        public static List<HardwareHistoryPoint> GetPoints(DateTime from)
        {
            lock (_dataLock)
            {
                var result = Data.Points
                    .Where(p => p.Time >= from)
                    .Select(ClonePoint)
                    .ToList();

                var pending = BuildCurrentPointUnsafe();
                if (pending != null && pending.Time >= from)
                {
                    result.Add(pending);
                }

                return result.OrderBy(p => p.Time).ToList();
            }
        }

        public static void Save()
        {
            SaveCore(flushPending: true);
        }

        private static void SaveCore(bool flushPending)
        {
            HardwareHistoryData snapshot;
            lock (_dataLock)
            {
                if (flushPending)
                {
                    FlushCurrentBucketUnsafe();
                }
                TrimUnsafe(DateTime.Now);
                snapshot = CloneDataUnsafe();
                if (!flushPending)
                {
                    var pending = BuildCurrentPointUnsafe();
                    if (pending != null) snapshot.Points.Add(pending);
                }
            }

            lock (_ioLock)
            {
                try
                {
                    string json = JsonSerializer.Serialize(snapshot);
                    AtomicWrite(json);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HardwareHistoryLogger] 保存硬件趋势失败: {ex.Message}");
                    CleanupTempFile();
                }
            }
        }

        private static IEnumerable<string> GetEnabledSupportedKeys(Settings cfg)
        {
            foreach (string key in _supportedKeys)
            {
                bool enabled = cfg.MonitorItems.Any(item =>
                    item.Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                    (item.VisibleInPanel || item.VisibleInTaskbar));

                if (enabled) yield return key;
            }
        }

        private static bool IsValidValue(string key, float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return false;

            var type = MetricUtils.GetType(key);
            return type switch
            {
                MetricType.Temperature => v > 0 && v <= 125,
                MetricType.Percent => v >= 0 && v <= 100,
                MetricType.Memory => v >= 0 && v <= 100,
                MetricType.Frequency => v > 0 && v <= 10000,
                MetricType.Power => v > 0 && v <= 1000,
                MetricType.FPS => v >= 0 && v <= 1000,
                _ => false
            };
        }

        private static HardwareHistoryData? TryLoadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<HardwareHistoryData>(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HardwareHistoryLogger] 读取硬件趋势失败: {path}, {ex.Message}");
                return null;
            }
        }

        private static void FlushCurrentBucketUnsafe()
        {
            var point = BuildCurrentPointUnsafe();
            if (point == null) return;

            Data.Points.Add(point);
            _currentBucket.Clear();
            TrimUnsafe(DateTime.Now);
        }

        private static HardwareHistoryPoint? BuildCurrentPointUnsafe()
        {
            if (_currentMinute == DateTime.MinValue || _currentBucket.Count == 0) return null;

            var point = new HardwareHistoryPoint { Time = _currentMinute };
            foreach (var kv in _currentBucket)
            {
                if (kv.Value.Count <= 0) continue;
                point.Values[kv.Key] = kv.Value.ToValue();
            }

            return point.Values.Count > 0 ? point : null;
        }

        private static void TrimUnsafe(DateTime now)
        {
            DateTime cutoff = now - _retention;
            Data.Points.RemoveAll(p => p.Time < cutoff);
        }

        private static HardwareHistoryData CloneDataUnsafe()
        {
            return new HardwareHistoryData
            {
                Points = Data.Points.Select(ClonePoint).ToList()
            };
        }

        private static HardwareHistoryPoint ClonePoint(HardwareHistoryPoint p)
        {
            return new HardwareHistoryPoint
            {
                Time = p.Time,
                Values = (p.Values ?? new Dictionary<string, HardwareHistoryValue>()).ToDictionary(
                    kv => kv.Key,
                    kv => new HardwareHistoryValue
                    {
                        Avg = kv.Value.Avg,
                        Max = kv.Value.Max,
                        Min = kv.Value.Min,
                        Count = kv.Value.Count
                    },
                    StringComparer.OrdinalIgnoreCase)
            };
        }

        private static void SaveAsync()
        {
            lock (_ioLock)
            {
                if (_saveRunning) return;
                _saveRunning = true;
            }

            Task.Run(() =>
            {
                try { SaveCore(flushPending: false); }
                finally
                {
                    lock (_ioLock) _saveRunning = false;
                }
            });
        }

        private static void AtomicWrite(string json)
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_tempPath, json);

            if (File.Exists(_filePath))
            {
                try { File.Copy(_filePath, _backupPath, true); }
                catch (Exception ex) { Debug.WriteLine($"[HardwareHistoryLogger] 写入备份失败: {ex.Message}"); }
            }

            File.Move(_tempPath, _filePath, true);
        }

        private static void CleanupTempFile()
        {
            try
            {
                if (File.Exists(_tempPath)) File.Delete(_tempPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HardwareHistoryLogger] 清理临时文件失败: {ex.Message}");
            }
        }

        private static DateTime FloorMinute(DateTime time)
        {
            return new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0);
        }

        private sealed class Accumulator
        {
            private double _sum;
            private float _min = float.MaxValue;
            private float _max = float.MinValue;

            public int Count { get; private set; }

            public void Add(float value)
            {
                _sum += value;
                if (value < _min) _min = value;
                if (value > _max) _max = value;
                Count++;
            }

            public HardwareHistoryValue ToValue()
            {
                return new HardwareHistoryValue
                {
                    Avg = Count > 0 ? (float)(_sum / Count) : 0,
                    Max = Count > 0 ? _max : 0,
                    Min = Count > 0 ? _min : 0,
                    Count = Count
                };
            }
        }
    }
}
