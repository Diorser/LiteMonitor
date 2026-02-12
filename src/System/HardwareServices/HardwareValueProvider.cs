using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using Debug = System.Diagnostics.Debug;

namespace LiteMonitor.src.SystemServices
{
    public class HardwareValueProvider : IDisposable
    {
        private readonly Computer _computer;
        private readonly Settings _cfg;
        private readonly SensorMap _sensorMap;
        private readonly NetworkManager _networkManager;
        private readonly DiskManager _diskManager;
        private readonly FpsCounter _fpsCounter;
        private readonly object _lock;
        private readonly Dictionary<string, float> _lastValidMap; 
        
        // 子服务与处理器
        private readonly PerformanceCounterManager _perfManager;
        private readonly ComponentProcessor _componentProcessor;

        // Tick 级智能缓存 (防止同帧重复计算)
        private readonly Dictionary<string, float> _tickCache = new();

        // 对象级缓存：(Sensor对象)
        private Dictionary<string, ISensor> _manualSensorCache = new();

        // 配置版本追踪，用于自动触发预热
        private string _lastPrefCpuFan = "";
        private string _lastPrefCpuPump = "";
        private string _lastPrefCaseFan = "";
        private string _lastPrefMoboTemp = "";
        private string _lastPrefDisk = "";
        private string _lastPrefNet = "";
        
        public HardwareValueProvider(Computer c, Settings s, SensorMap map, NetworkManager net, DiskManager disk, FpsCounter fpsCounter,PerformanceCounterManager perfManager, object syncLock, Dictionary<string, float> lastValid)
        {
            _computer = c;
            _cfg = s;
            _sensorMap = map;
            _networkManager = net;
            _diskManager = disk;
            _fpsCounter = fpsCounter;
            _perfManager = perfManager;
            _lock = syncLock;
            _lastValidMap = lastValid;

            // 初始化子服务
            _componentProcessor = new ComponentProcessor(c, s, map);
        }

        // ★★★ [新增] 清空缓存并重新预热（当硬件重载或配置变更时调用） ★★★
        public void PreCacheAllSensors(SensorMap map)
        {
            var newCache = map.GetInternalMap();

            // 记录当前预热的配置版本
            _lastPrefCpuFan = _cfg.PreferredCpuFan;
            _lastPrefCpuPump = _cfg.PreferredCpuPump;
            _lastPrefCaseFan = _cfg.PreferredCaseFan;
            _lastPrefMoboTemp = _cfg.PreferredMoboTemp;
            _lastPrefDisk = _cfg.PreferredDisk;
            _lastPrefNet = _cfg.PreferredNetwork;

            // 1. 预查找用户指定的首选传感器 (风扇、水泵、主板温度)
            string[] preferredKeys = { "CPU.Fan", "CPU.Pump", "CASE.Fan", "MOBO.Temp" };
            foreach (var key in preferredKeys)
            {
                string pref = (key == "CPU.Fan") ? _lastPrefCpuFan :
                             (key == "CPU.Pump") ? _lastPrefCpuPump :
                             (key == "CASE.Fan") ? _lastPrefCaseFan : _lastPrefMoboTemp;

                SensorType type = (key == "MOBO.Temp") ? SensorType.Temperature : SensorType.Fan;

                if (!string.IsNullOrEmpty(pref) && !pref.Contains("自动") && !pref.Contains("Auto"))
                {
                    var s = FindSensorReverse(pref, type);
                    if (s != null) newCache[key] = s;
                }
            }

            // 2. 预查找首选磁盘 (DISK.*)
            if (!string.IsNullOrEmpty(_lastPrefDisk))
            {
                var disk = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Storage && h.Name.Equals(_lastPrefDisk, StringComparison.OrdinalIgnoreCase));
                if (disk != null)
                {
                    foreach (var s in disk.Sensors)
                    {
                        if (s.SensorType == SensorType.Throughput)
                        {
                            if (SensorMap.Has(s.Name, "read")) newCache["DISK.Read"] = s;
                            else if (SensorMap.Has(s.Name, "write")) newCache["DISK.Write"] = s;
                        }
                    }
                    
                    // 独立处理温度筛选逻辑 (复用 DiskManager 算法)
                    var bestTemp = DiskManager.FindBestTempSensor(disk);
                    if (bestTemp != null) newCache["DISK.Temp"] = bestTemp;
                }
            }

            // 3. 预查找首选网络 (NET.*)
            if (!string.IsNullOrEmpty(_lastPrefNet))
            {
                var net = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Network && h.Name.Equals(_lastPrefNet, StringComparison.OrdinalIgnoreCase));
                if (net != null)
                {
                    foreach (var s in net.Sensors)
                    {
                        if (s.SensorType == SensorType.Throughput)
                        {
                            if (SensorMap.Has(s.Name, "upload") || SensorMap.Has(s.Name, "sent") || SensorMap.Has(s.Name, "up")) newCache["NET.Up"] = s;
                            else if (SensorMap.Has(s.Name, "download") || SensorMap.Has(s.Name, "received") || SensorMap.Has(s.Name, "down")) newCache["NET.Down"] = s;
                        }
                    }
                }
            }

            // 4. 原子更新缓存
            lock (_lock)
            {
                _manualSensorCache = newCache;
                _tickCache.Clear();
                _componentProcessor.ClearCache();
            }
        }

        /// <summary>
        /// 极速反向查找：解析保存的字符串（格式：SensorName [HardwareName]）并定位到 ISensor 对象
        /// </summary>
        private ISensor? FindSensorReverse(string savedString, SensorType type)
        {
            if (string.IsNullOrEmpty(savedString) || savedString.Contains("Auto") || savedString.Contains("自动")) 
                return null;

            int idx = savedString.LastIndexOf('[');
            if (idx < 0) return null; 

            string targetSensorName = savedString.Substring(0, idx).Trim();
            string targetHardwareName = savedString.Substring(idx + 1).TrimEnd(']');

            ISensor? SearchBranch(IHardware h)
            {
                foreach (var s in h.Sensors)
                {
                    if (s.SensorType == type && s.Name == targetSensorName)
                        return s; 
                }
                foreach (var sub in h.SubHardware)
                {
                    var s = SearchBranch(sub);
                    if (s != null) return s;
                }
                return null;
            }

            foreach (var hw in _computer.Hardware)
            {
                if (hw.Name == targetHardwareName)
                {
                    return SearchBranch(hw);
                }
            }
            return null;
        }

        public void ClearCache()
        {
            lock (_lock)
            {
                _manualSensorCache.Clear();
                _tickCache.Clear();
                _componentProcessor.ClearCache();
            }
        }

        /// <summary>
        /// 每一轮更新开始时的准备工作（清空帧缓存、检查配置变更、更新电源状态）
        /// </summary>
        public void OnUpdateTickStarted()
        {
            lock (_lock)
            {
                _tickCache.Clear();

                // 自动检测配置变更：如果用户更改了首选风扇/磁盘，立即自动预热
                if (_lastPrefCpuFan != _cfg.PreferredCpuFan ||
                    _lastPrefCpuPump != _cfg.PreferredCpuPump ||
                    _lastPrefCaseFan != _cfg.PreferredCaseFan ||
                    _lastPrefMoboTemp != _cfg.PreferredMoboTemp ||
                    _lastPrefDisk != _cfg.PreferredDisk ||
                    _lastPrefNet != _cfg.PreferredNetwork)
                {
                    PreCacheAllSensors(_sensorMap);
                }
            }
        }


        // ===========================================================
        // ===================== 公共取值入口 =========================
        // ===========================================================
        public float? GetValue(string key)
        {
            // ★★★ [优化] 使用 TryEnter 避免 UI 线程因后台重载而卡死 ★★★
            // 如果 10ms 内拿不到锁（说明正在重载硬件），直接返回 null
            bool lockTaken = false;
            try
            {
                Monitor.TryEnter(_lock, 10, ref lockTaken);
                if (!lockTaken) 
                {
                    // ★★★ [Fix] 如果无法获取锁（硬件正在重载），优先返回上一帧的有效值，防止 UI 闪烁 ★★★
                    // 因为 _lastValidMap 仅在 GetValue 内部（即 UI 线程）写入，
                    // 而后台线程（UpdateAll/Reload）只读不写或完全不访问它，
                    // 所以此时读取是安全的（虽然没有锁，但没有并发写入）。
                    if (_lastValidMap.TryGetValue(key, out float lastVal)) return lastVal;
                    return null;
                }

                // ★★★ [新增 3] 优先查缓存，如果本帧算过，直接返回 ★★★
                if (_tickCache.TryGetValue(key, out float cachedVal)) return cachedVal;



                // 定义临时结果变量
                float? result = null;
                
                // ★★★ [核心逻辑] 全局开关判断：只有当开关开启，且管理器已初始化成功时，才尝试走计数器 ★★★
                // 这里的 UseWindowsPerformanceCounters 对应 Step 1 中 Settings 新增的属性
                bool useCounter = _cfg.UseWinPerCounters && _perfManager.IsInitialized;

                // ★★★ [终极优化] 使用 switch 替代 if-else 链，实现 O(1) 哈希跳转 ★★★
                switch (key)
                {
                    // 1. CPU.Load
                    case "CPU.Load":
                        if (useCounter)
                        {
                            result = _perfManager.GetCpuLoad();
                            if (result.HasValue && result.Value > 100f) result = 100f;
                        }

                        if (result == null)
                        {
                            // [Fix] 优先使用 LHM 的 "Total" 聚合传感器（与任务管理器一致）
                            // 然后才回退到各核心平均值计算
                            if (_manualSensorCache.TryGetValue("CPU.Load", out var totalSensor) && totalSensor.Value.HasValue)
                            {
                                result = totalSensor.Value.Value;
                            }

                            // 如果 Total 传感器不可用，才回退到核心平均值
                            if (result == null)
                            {
                                result = _componentProcessor.GetCpuLoad();
                            }

                            if (result == null) result = 0f;
                        }
                        break;

                    // 2. CPU.Temp
                    case "CPU.Temp":
                        result = _componentProcessor.GetCpuTemp();
                        if (result == null && _manualSensorCache.TryGetValue("CPU.Temp", out var fallbackT))
                        {
                            result = fallbackT.Value;
                        }
                        if (result == null) result = 0f;
                        break;

                    // 4. 每日流量
                    case "DATA.DayUp":
                        result = TrafficLogger.GetTodayStats().up;
                        break;
                    case "DATA.DayDown":
                        result = TrafficLogger.GetTodayStats().down;
                        break;

                    // 6. 内存
                    case "MEM.Load":
                        if (useCounter)
                        {
                            var memData = _perfManager.GetMemoryData();
                            if (memData.Load.HasValue)
                            {
                                result = memData.Load.Value;
                                if (Settings.DetectedRamTotalGB <= 0 && _perfManager.TotalMemoryGB > 0.1f)
                                {
                                    Settings.DetectedRamTotalGB = _perfManager.TotalMemoryGB;
                                }
                            }
                        }

                        if (result == null)
                        {
                            // 1. 尝试获取基础数据 (Used, Available)
                            // 这是最可靠的数据源，既能算出负载，也能初始化总容量
                            float? usedVal = null;
                            float? availVal = null;
                            if (_manualSensorCache.TryGetValue("MEM.Used", out var u) && u.Value.HasValue) usedVal = u.Value.Value;
                            if (_manualSensorCache.TryGetValue("MEM.Available", out var a) && a.Value.HasValue) availVal = a.Value.Value;

                            // 2. 优先通过计算获取结果 (Used / (Used + Available))
                            // 这种方式能同时确保 Load 百分比和 Total 容量都被正确处理，避免"有负载没容量"的问题
                            if (usedVal.HasValue && availVal.HasValue)
                            {
                                float memTotal = usedVal.Value + availVal.Value;
                                if (memTotal > 0)
                                {
                                    // 初始化总容量 (供容量显示模式使用)
                                    if (Settings.DetectedRamTotalGB <= 0)
                                    {
                                        Settings.DetectedRamTotalGB = memTotal > 512.0f ? memTotal / 1024.0f : memTotal;
                                    }

                                    // 计算负载百分比
                                    result = (usedVal.Value / memTotal) * 100.0f;
                                }
                            }

                            // 3. 兜底：如果无法计算 (缺 Used/Available)，才尝试读取原生 Load 传感器
                            if (result == null && _manualSensorCache.TryGetValue("MEM.Load", out var sLoad) && sLoad.Value.HasValue)
                            {
                                result = sLoad.Value.Value;
                            }
                        }
                        else
                        {
                            break; 
                        }
                        break;

                    // 7. 显存
                    case "GPU.VRAM":
                        float? used = GetValue("GPU.VRAM.Used");
                        float? total = GetValue("GPU.VRAM.Total");
                        if (used.HasValue && total.HasValue && total > 0)
                        {
                            if (Settings.DetectedGpuVramTotalGB <= 0) Settings.DetectedGpuVramTotalGB = total.Value / 1024f;
                            if (total > 10485760) { used /= 1048576f; total /= 1048576f; }
                            result = used / total * 100f;
                        }
                        else
                        {
                            if (_manualSensorCache.TryGetValue("GPU.VRAM.Load", out var s) && s.Value.HasValue) result = s.Value;
                        }
                        break;

                    // 8. 风扇/泵 (带 Max 记录)
                    case "CPU.Fan":
                    case "CPU.Pump":
                    case "CASE.Fan":
                    case "GPU.Fan":
                        if (_manualSensorCache.TryGetValue(key, out var sFan))
                        {
                            result = sFan.Value;
                        }
                        else 
                        {
                            if (_manualSensorCache.TryGetValue(key, out var autoS) && autoS.Value.HasValue)
                                result = autoS.Value.Value;
                        }
                        if (result.HasValue && result.Value < 10000f) 
                            _cfg.UpdateMaxRecord(key, result.Value);
                        break;

                    // 主板温度
                    case "MOBO.Temp":
                        if (_manualSensorCache.TryGetValue(key, out var sMobo))
                        {
                            result = sMobo.Value;
                        }
                        break;

                    case "FPS":
                        result = _fpsCounter.GetFps();
                        break;

                    // 电池
                    case "BAT.Percent":
                    case "BAT.Power":
                    case "BAT.Voltage":
                    case "BAT.Current":
                        result = BatteryService.GetBatteryValue(key, _manualSensorCache);
                        break;

                    // 默认分支：处理模糊匹配 (StartsWith/Contains)
                    default:
                        if (key.StartsWith("NET"))
                        {
                            if (_manualSensorCache.TryGetValue(key, out var sNet))
                            {
                                result = sNet.Value;
                            }
                            
                            if (result == null)
                            {
                                result = _networkManager.GetBestValue(key, _computer, _cfg, _lastValidMap, _lock);
                            }
                        }
                        else if (key.StartsWith("DISK"))
                        {
                            bool isSpecificDisk = !string.IsNullOrEmpty(_cfg.PreferredDisk);
                            if (useCounter && !isSpecificDisk)
                            {
                                if (key == "DISK.Read") result = _perfManager.GetDiskRead();
                                else if (key == "DISK.Write") result = _perfManager.GetDiskWrite();
                                else if (key == "DISK.Activity") 
                                {
                                    result = _perfManager.GetDiskActive();
                                    if (result.HasValue) result = Math.Clamp(result.Value, 0f, 100f);
                                }
                            }

                            if (result == null && _manualSensorCache.TryGetValue(key, out var sDisk))
                            {
                                result = sDisk.Value;
                            }

                            if (result == null)
                            {
                                result = _diskManager.GetBestValue(key, _computer, _cfg, _lastValidMap, _lock);
                            }
                        }
                        else if (key.Contains("Clock") || key.Contains("Power"))
                        {
                            if (useCounter && key == "CPU.Clock")
                            {
                                result = _perfManager.GetCpuFreq();
                            }
                            
                            if (result == null)
                            {
                                result = _componentProcessor.GetCompositeValue(key, _manualSensorCache);
                            }
                        }
                        break;
                }

                // 10. 通用传感器查找 (兜底)
                // ★★★ [优化] 移除锁和 _sensorMap 查找，直接查静态缓存 ★★★
                if (result == null && _manualSensorCache.TryGetValue(key, out var sGen))
                {
                    var val = sGen.Value;
                    if (val.HasValue && !float.IsNaN(val.Value)) 
                    { 
                        _lastValidMap[key] = val.Value; 
                        result = val.Value; 
                    }
                    else if (_lastValidMap.TryGetValue(key, out var last))
                    {
                        result = last;
                    }
                }

                // ★★★ [新增 4] 写入缓存并返回 ★★★
                if (result.HasValue)
                {
                    _tickCache[key] = result.Value;
                    return result.Value;
                }

                return null;
            }
            finally
            {
                if (lockTaken) Monitor.Exit(_lock);
            }
        }

        public void Dispose()
        {
        }
    }
}