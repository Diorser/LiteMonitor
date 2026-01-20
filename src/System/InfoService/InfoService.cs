using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.SystemServices.InfoService
{
    /// <summary>
    /// 系统信息服务 (单例)
    /// 负责管理 HOST, IP, Time 等看板数据的获取与缓存
    /// 原 DashboardService
    /// </summary>
    public class InfoService
    {
        #region Singleton
        private static InfoService _instance;
        public static InfoService Instance => _instance ??= new InfoService();
        private InfoService() { Initialize(); }
        #endregion

        // === Constants ===
        private const string KEY_HOST = "HOST";
        private const string KEY_IP   = "IP";
        private const string KEY_TIME = "Time";
        private const string KEY_UPTIME = "Uptime";

        // Default Values (User Friendly)
        private const string DEFAULT_IP   = "0.0.0.0";


        // Update Intervals (For IP/Host only, NOT for Uptime/Time)
        private const int INTERVAL_SLOW = 60000; // 1 min (Stable state: IP found)
        private const int INTERVAL_FAST = 2000;  // 2 sec (Retry state: IP missing)

        // === State ===
        private readonly Dictionary<string, string> _data = new();
        private readonly object _lock = new();
        
        private long _lastUpdateTick = 0;
        private int _currentInterval = INTERVAL_FAST;

        /// <summary>
        /// 初始化默认值并启动首次更新
        /// </summary>
        private void Initialize()
        {
            lock (_lock)
            {
                _data[KEY_HOST] = Environment.MachineName; // Hostname always available
                _data[KEY_IP]   = DEFAULT_IP;
                _data[KEY_TIME] = DateTime.Now.ToString("ddd HH:mm:ss"); // ★★★ 立即赋值当前时间，不再使用 00:00:00 默认值 ★★★
            }
            
            // [Fix] Calculate Uptime immediately so it's ready for first render
            UpdateTimeInfo();

            // Trigger first async update
            UpdateData();
        }

        /// <summary>
        /// 线程安全地获取数据
        /// </summary>
        public string GetValue(string key)
        {
            lock (_lock)
            {
                return _data.TryGetValue(key, out var val) ? val : "";
            }
        }

        /// <summary>
        /// 外部注入通用数据 (例如插件抓取的数据)
        /// </summary>
        public void InjectValue(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            SetData(key, value);
        }

        /// <summary>
        /// 外部注入 IP (例如从配置缓存恢复)
        /// </summary>
        public void InjectIP(string ip)
        {
            if (string.IsNullOrEmpty(ip) || ip == DEFAULT_IP) return;

            SetData(KEY_IP, ip);
            // If valid IP injected, switch to slow update immediately
            _currentInterval = INTERVAL_SLOW;
        }

        /// <summary>
        /// 主循环调用 (建议每帧或定时器调用)
        /// </summary>
        public void Update()
        {
            // 1. High Frequency: Time (Every tick)
            UpdateTimeInfo();

            // 2. Low Frequency: Network/Host (Based on interval)
            long now = Environment.TickCount64;
            if (now - _lastUpdateTick > _currentInterval)
            {
                _lastUpdateTick = now;
                UpdateData();
            }
        }

        private void UpdateTimeInfo()
        {
            // Format: 周五 21:11:11
            SetData(KEY_TIME, DateTime.Now.ToString("ddd HH:mm:ss"));

            // Uptime
            TimeSpan ts = TimeSpan.FromMilliseconds(Environment.TickCount64);
            
            // Format: 
            // < 1 Day: 5h 12m 30s (5时 12分 30秒)
            // > 1 Day: 3d 5h 12m  (3天 5时 12分)
            string upStr;
            
            if (ts.TotalDays < 1)
            {
                upStr = LanguageManager.CurrentLang == "zh"
                    ? $"{ts.Hours}时 {ts.Minutes}分 {ts.Seconds}秒"
                    : $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
            }
            else
            {
                upStr = LanguageManager.CurrentLang == "zh"
                    ? $"{(int)ts.TotalDays}天 {ts.Hours}时 {ts.Minutes}分"
                    : $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
            }

            SetData(KEY_UPTIME, upStr);
        }

        private void UpdateData()
        {
            // Host info is local and fast
            SetData(KEY_HOST, Environment.MachineName);

            // IP info is potentially slow, run async
            Task.Run(UpdateIPInfo);
        }

        private void UpdateIPInfo()
        {
            try
            {
                // Delegate to HardwareMonitor's NetworkManager (handles caching & multi-interface logic)
                string ip = HardwareMonitor.Instance?.GetNetworkIP();

                // Validate IP
                if (!string.IsNullOrEmpty(ip) && ip != DEFAULT_IP)
                {
                    SetData(KEY_IP, ip);
                    _currentInterval = INTERVAL_SLOW; // Success -> Relax
                }
                else
                {
                    // Failed -> Retry fast
                    _currentInterval = INTERVAL_FAST;
                }
            }
            catch
            {
                _currentInterval = INTERVAL_FAST;
            }
        }

        private void SetData(string key, string value)
        {
            lock (_lock)
            {
                _data[key] = value;
            }
        }
    }
}