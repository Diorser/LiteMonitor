using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json; // 如果报错，请引用 System.Text.Json NuGet包，或改用 Newtonsoft

namespace LiteMonitor.src.Core
{
    // === 数据模型 ===
    public class TrafficData
    {
        // Key格式: "2023-10-27"
        public Dictionary<string, DailyRecord> History { get; set; } = new Dictionary<string, DailyRecord>();
    }

    public class DailyRecord
    {
        public long Upload { get; set; }
        public long Download { get; set; }
    }

    // === 管理器 (静态单例) ===
    public static class TrafficLogger
    {
        private static readonly string _filePath = Path.Combine(AppContext.BaseDirectory, "TrafficHistory.json");
        private static readonly object _ioLock = new object(); // 文件锁
        // ====== 新增缓存字段 ======
        private static DateTime _cachedDate;
        private static string _cachedDateKey;

        public static TrafficData Data { get; private set; } = new TrafficData();

        // 启动时加载
        public static void Load()
        {
            lock (_ioLock)
            {
                try
                {
                    if (File.Exists(_filePath))
                    {
                        string json = File.ReadAllText(_filePath);
                        Data = JsonSerializer.Deserialize<TrafficData>(json) ?? new TrafficData();
                    }
                }
                catch (Exception ex)
                { 
                    // [Fix] 添加错误日志
                    System.Diagnostics.Debug.WriteLine($"[TrafficLogger] Load failed: {ex.Message}");
                    Data = new TrafficData(); 
                }
            }
        }

        // 关闭时保存
        public static void Save()
        {
            lock (_ioLock)
            {
                try
                {
                    var opt = new JsonSerializerOptions { WriteIndented = true };
                    string json = JsonSerializer.Serialize(Data, opt);
                    File.WriteAllText(_filePath, json);
                }
                catch (Exception ex) 
                { 
                    // [Fix] 添加错误日志
                    System.Diagnostics.Debug.WriteLine($"[TrafficLogger] Save failed: {ex.Message}");
                }
            }
        }

        // 核心：累加数据
        public static void AddTraffic(long upBytes, long downBytes)
        {
            // 优化：只有日期变更时才重新 Format 和 Intern
            if (DateTime.Today != _cachedDate)
            {
                _cachedDate = DateTime.Today;
                // 此时才调用 ToString 和 Intern
                _cachedDateKey = UIUtils.Intern(_cachedDate.ToString("yyyy-MM-dd"));
            }
            
            // 使用缓存的 Key
            string key = _cachedDateKey;

            // 线程安全注意：虽然 UI 读取和 Update 写入可能并发，但 Dictionary 非线程安全。
            // 考虑到冲突概率极低（UI 只读，Update 只写），暂不加重锁，或者简单加锁：
            lock (Data) 
            {
                if (!Data.History.ContainsKey(key))
                {
                    Data.History[key] = new DailyRecord();
                }

                Data.History[key].Upload += upBytes;
                Data.History[key].Download += downBytes;
            }
        }

        // 获取今日数据 (供 UI 显示)
        public static (long up, long down) GetTodayStats()
        {
            if (DateTime.Today != _cachedDate)
            {
                _cachedDate = DateTime.Today;
                _cachedDateKey = UIUtils.Intern(_cachedDate.ToString("yyyy-MM-dd"));
            }
            string key = _cachedDateKey;
            lock (Data)
            {
                if (Data.History.TryGetValue(key, out var rec))
                {
                    return (rec.Upload, rec.Download);
                }
            }
            return (0, 0);
        }

        // [新增] 删除指定日期的记录
        public static void RemoveRecord(string dateKey)
        {
            lock (_ioLock)
            {
                if (Data.History.Remove(dateKey))
                {
                    Save(); // 立即保存更改
                }
            }
        }

        // [新增] 清空所有历史
        public static void ClearHistory()
        {
            lock (_ioLock)
            {
                Data.History.Clear();
                Save();
            }
        }
    }
}