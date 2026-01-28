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
        private readonly FpsCounter _fpsCounter; // <--- 新增
        private readonly object _lock;
        private readonly Dictionary<string, float> _lastValidMap; 
        
        // ★★★ [新增] 性能计数器管理器 ★★★
        private readonly PerformanceCounterManager _perfManager;

        // [删除] 原有的系统计数器字段 (_cpuPerfCounter, _lastSystemCpuLoad 等)
        // [删除] 错误重试计数器字段

        // ★★★ [新增] Tick 级智能缓存 (防止同帧重复计算) ★★★
        private readonly Dictionary<string, float> _tickCache = new();

        // ★★★ [终极优化] 对象级缓存：(Sensor对象) ★★★
        // 缓存住找到的 ISensor 对象，彻底消除每秒的字符串解析和遍历开销
        private Dictionary<string, ISensor> _manualSensorCache = new();

        // ★★★ [优化] 核心指标传感器列表缓存，消除每秒的字符串查找和过滤开销 ★★★
        private List<ISensor>? _cpuLoadSensorsCache = null;
        private List<ISensor>? _cpuTempSensorsCache = null;
        private long _lastPowerCheckTick = 0;

        // ★★★ [新增] 配置版本追踪，用于自动触发预热 ★★★
        private string _lastPrefCpuFan = "";
        private string _lastPrefCpuPump = "";
        private string _lastPrefCaseFan = "";
        private string _lastPrefMoboTemp = "";
        private string _lastPrefDisk = "";
        private string _lastPrefNet = "";
        
        // ★★★ [新增] 系统充电状态缓存 ★★★
        private bool _isSystemCharging = false;

        public HardwareValueProvider(Computer c, Settings s, SensorMap map, NetworkManager net, DiskManager disk, FpsCounter fpsCounter,PerformanceCounterManager perfManager, object syncLock, Dictionary<string, float> lastValid)
        {
            _computer = c;
            _cfg = s;
            _sensorMap = map;
            _networkManager = net;
            _diskManager = disk;
            _fpsCounter = fpsCounter; // <--- 赋值
            _perfManager = perfManager; // ★★★ [新增] 赋值 ★★★
            _lock = syncLock;
            _lastValidMap = lastValid;
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
                        else if (s.SensorType == SensorType.Temperature)
                        {
                            newCache["DISK.Temp"] = s;
                        }
                    }
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
                _cpuLoadSensorsCache = null; 
                _cpuTempSensorsCache = null;
            }
        }

        public void ClearCache()
        {
            lock (_lock)
            {
                _manualSensorCache.Clear();
                _tickCache.Clear();
                _cpuLoadSensorsCache = null;
                _cpuTempSensorsCache = null;
            }
        }

        /// <summary>
        /// 每一轮更新开始时的准备工作（清空帧缓存、检查配置变更、节流获取电源状态）
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

                // 节流获取电源状态：每 3 秒检查一次即可
                long now = Environment.TickCount64;
                if (now - _lastPowerCheckTick > 3000)
                {
                    _lastPowerCheckTick = now;
                    try
                    {
                        var status = System.Windows.Forms.SystemInformation.PowerStatus;
                        MetricUtils.IsBatteryCharging = (status.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online);
                        
                        // ★★★ [Fix] 使用系统标志判断是否真的在充电 (解决 95% 以上可能还在充，或电池保护模式不充的问题)
                        // 注意：此 API 调用被外层的 if (now - _lastPowerCheckTick > 3000) 节流，每3秒仅执行一次，无性能损耗
                        _isSystemCharging = (status.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.Charging) == System.Windows.Forms.BatteryChargeStatus.Charging;
                    }
                    catch { }
                }
            }
        }


        // ===========================================================
        // ===================== 公共取值入口 =========================
        // ===========================================================
        public float? GetValue(string key)
        {
            lock (_lock)
            {
                // ★★★ [新增 3] 优先查缓存，如果本帧算过，直接返回 ★★★
                if (_tickCache.TryGetValue(key, out float cachedVal)) return cachedVal;

                // ★★★ [优化] 自动感知硬件树变动：如果 SensorMap 重建了，立即重新预热 Provider 缓存 ★★★
                if (_sensorMap.EnsureFresh(_computer, _cfg))
                {
                    PreCacheAllSensors(_sensorMap);
                }

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
                        // A. 尝试走计数器
                        if (useCounter)
                        {
                            result = _perfManager.GetCpuLoad();
                            // 修正计数器可能出现的 >100% 溢出
                            if (result.HasValue && result.Value > 100f) result = 100f;
                        }

                        // B. 熔断/回退逻辑 (LHM 手动聚合)
                        if (result == null)
                        {
                            // ★★★ [优化] 使用缓存的传感器列表，避免每秒的字符串查找 ★★★
                            if (_cpuLoadSensorsCache == null)
                            {
                                var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                                if (cpu != null)
                                {
                                    _cpuLoadSensorsCache = new List<ISensor>();
                                    foreach (var s in cpu.Sensors)
                                    {
                                        if (s.SensorType != SensorType.Load) continue;
                                        if (SensorMap.Has(s.Name, "Core") && SensorMap.Has(s.Name, "#") && 
                                            !SensorMap.Has(s.Name, "Total") && !SensorMap.Has(s.Name, "SOC") && 
                                            !SensorMap.Has(s.Name, "Max") && !SensorMap.Has(s.Name, "Average"))
                                        {
                                            _cpuLoadSensorsCache.Add(s);
                                        }
                                    }
                                }
                            }

                            if (_cpuLoadSensorsCache != null && _cpuLoadSensorsCache.Count > 0)
                            {
                                double totalLoad = 0;
                                int coreCount = 0;
                                foreach (var s in _cpuLoadSensorsCache)
                                {
                                    if (s.Value.HasValue) { totalLoad += s.Value.Value; coreCount++; }
                                }
                                if (coreCount > 0) result = (float)(totalLoad / coreCount);
                            }
                            
                            // 兜底：直接从静态缓存取
                            if (result == null && _manualSensorCache.TryGetValue("CPU.Load", out var fallbackS))
                            {
                                result = fallbackS.Value;
                            }
                            // 如果还是没值，默认为 0
                            if (result == null) result = 0f;
                        }
                        break;

                    // 2. CPU.Temp
                    case "CPU.Temp":
                        // ★★★ [优化] 使用缓存的传感器列表 ★★★
                        if (_cpuTempSensorsCache == null)
                        {
                            var cpuT = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                            if (cpuT != null)
                            {
                                _cpuTempSensorsCache = new List<ISensor>();
                                foreach (var s in cpuT.Sensors)
                                {
                                    if (s.SensorType == SensorType.Temperature && 
                                        !SensorMap.Has(s.Name, "Distance") && !SensorMap.Has(s.Name, "Average") && !SensorMap.Has(s.Name, "Max"))
                                    {
                                        _cpuTempSensorsCache.Add(s);
                                    }
                                }
                            }
                        }

                        if (_cpuTempSensorsCache != null && _cpuTempSensorsCache.Count > 0)
                        {
                            float maxTemp = -1000f;
                            bool found = false;
                            foreach (var s in _cpuTempSensorsCache)
                            {
                                if (s.Value.HasValue && s.Value.Value > 0)
                                {
                                    if (s.Value.Value > maxTemp) { maxTemp = s.Value.Value; found = true; }
                                }
                            }
                            if (found) result = maxTemp;
                        }
                        
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
                        // A. 尝试走计数器 (极速)
                        if (useCounter)
                        {
                            var memData = _perfManager.GetMemoryData();
                            if (memData.Load.HasValue)
                            {
                                result = memData.Load.Value;
                                // [Fix] 增加非零校验，防止计数器异常导致总量清零
                                if (Settings.DetectedRamTotalGB <= 0 && _perfManager.TotalMemoryGB > 0.1f)
                                {
                                    Settings.DetectedRamTotalGB = _perfManager.TotalMemoryGB;
                                }
                            }
                        }

                        // B. LHM 兜底逻辑
                        if (result == null)
                        {
                            if (Settings.DetectedRamTotalGB <= 0)
                            {
                                if (_manualSensorCache.TryGetValue("MEM.Used", out var u) && _manualSensorCache.TryGetValue("MEM.Available", out var a))
                                {
                                    if (u.Value.HasValue && a.Value.HasValue)
                                    {
                                        float rawTotal = u.Value.Value + a.Value.Value;
                                        Settings.DetectedRamTotalGB = rawTotal > 512.0f ? rawTotal / 1024.0f : rawTotal;
                                    }
                                }
                            }
                            // Break 后走下方通用传感器取值
                        }
                        else
                        {
                            // 如果走了计数器，直接返回，不走下方 break
                            break; 
                        }
                        break;

                    // 7. 显存
                    case "GPU.VRAM":
                        // 注意：这里递归调用了 GetValue，会用到缓存，非常高效
                        float? used = GetValue("GPU.VRAM.Used");
                        float? total = GetValue("GPU.VRAM.Total");
                        if (used.HasValue && total.HasValue && total > 0)
                        {
                            if (Settings.DetectedGpuVramTotalGB <= 0) Settings.DetectedGpuVramTotalGB = total.Value / 1024f;
                            // 单位转换
                            if (total > 10485760) { used /= 1048576f; total /= 1048576f; }
                            result = used / total * 100f;
                        }
                        else
                        {
                            if (_manualSensorCache.TryGetValue("GPU.VRAM.Load", out var s) && s.Value.HasValue) result = s.Value;
                        }
                        break;

                    // 8. 风扇/泵 (带 Max 记录) + [终极优化：全静态化缓存]
                    case "CPU.Fan":
                    case "CPU.Pump":
                    case "CASE.Fan":
                    case "GPU.Fan":
                        // 直接查对象缓存 (O(1) 访问)
                        if (_manualSensorCache.TryGetValue(key, out var sFan))
                        {
                            result = sFan.Value;
                        }
                        else 
                        {
                            // 如果静态缓存中没有（可能是 Auto），则尝试从通用映射中获取
                            if (_manualSensorCache.TryGetValue(key, out var autoS) && autoS.Value.HasValue)
                                result = autoS.Value.Value;
                        }
                        if (result.HasValue) _cfg.UpdateMaxRecord(key, result.Value);
                        break;

                    // [插入/修改逻辑] 主板温度 (全静态化缓存)
                    case "MOBO.Temp":
                        // 直接查缓存
                        if (_manualSensorCache.TryGetValue(key, out var sMobo))
                        {
                            result = sMobo.Value;
                        }
                        break;

                    // ★★★ [新增] FPS 支持 ★★★
                    case "FPS":
                        result = _fpsCounter.GetFps();
                        break;



                    // ★★★ [修改] 电池模拟测试逻辑 (基于主流 100W PD快充笔记本模型) ★★★
                    case "BAT.Percent":
                    case "BAT.Power":
                    case "BAT.Voltage":
                    case "BAT.Current":
                        bool _simulateBattery = false;
                        if (_simulateBattery) // 确保你有这个变量，或者直接写 true
                        {
                            var now = DateTime.Now;
                            int sec = now.Second; // 0-59

                            // === 状态定义 ===
                            // 前 30 秒：模拟 [高负载放电] (游戏/渲染中)
                            // 后 30 秒：模拟 [PD快充回血] (连接 100W 充电器)
                            bool isCharging = sec >= 30;
                            
                            // 必须同步更新全局状态，否则 UI 图标不跳变
                            MetricUtils.IsBatteryCharging = isCharging;

                            // 1. 计算基础电压 (4芯锂电池: 14.8V - 16.8V)
                            // 放电时电压下降，充电时电压升高
                            float voltage = isCharging 
                                ? 15.5f + ((sec - 30) * 0.05f)  // 充电：电压逐渐爬升 (15.5 -> 17.0V)
                                : 16.8f - (sec * 0.06f);        // 放电：电压逐渐掉落 (16.8 -> 15.0V)

                            // 2. 计算功耗 (W)
                            // 放电：正值，模拟 25W-45W 波动
                            // 充电：负值，模拟 65W-90W 快充波动
                            float power;
                            if (!isCharging)
                            {
                                // 模拟 PD 协商震荡：-65W 到 -85W 之间波动
                                power = -65.0f - (sec % 5) * 4.0f; 
                            }
                            else
                            {
                                // 模拟 CPU 负载波动：25W 到 40W
                                power = 25.0f + (sec % 3) * 5.0f; 
                            }

                            // 3. 计算电流 (A) = 功率 / 电压
                            float current = power / voltage;

                            // 4. 计算百分比 (%)
                            // 为了方便 UI 测试，让它在 30秒内 跑完 0-100%
                            float percent;
                            if (isCharging)
                                percent = (sec - 30) * (100.0f / 30.0f); // 0 -> 100
                            else
                                percent = 100.0f - (sec * (100.0f / 30.0f)); // 100 -> 0

                            // === 赋值 ===
                            if (key == "BAT.Percent") result = Math.Clamp(percent, 0f, 100f);
                            else if (key == "BAT.Power") result = power;
                            else if (key == "BAT.Voltage") result = voltage;
                            else if (key == "BAT.Current") result = current;

                            break; // 模拟模式下直接跳出，不查 SensorMap
                        }



                        // 真实硬件逻辑
                        // [修改] 直接从静态缓存读取，不再加锁，不再重复调用 System API
                        if (_manualSensorCache.TryGetValue(key, out var sBat) && sBat.Value.HasValue) 
                        {
                            float val = sBat.Value.Value;

                            // [修改] 2. 符号修正：充电时功耗/电流应为正号，放电时为负号
                            if (key == "BAT.Power" || key == "BAT.Current")
                            {
                                if (MetricUtils.IsBatteryCharging)
                                {
                                    // ★★★ [Fix] 逻辑修正：如果接通电源但系统报告"未在充电" (如已充满/电池保护)，强制归零
                                    // 物理含义：此时处于"电源直供 (Bypass)" 模式，电池闲置，物理功率应为 0。
                                    // 作用：解决硬件传感器在停止充电瞬间可能卡在历史数值(如 -12W)的问题。
                                    if (!_isSystemCharging)
                                    {
                                        val = 0f;
                                    }
                                    else
                                    {
                                        if (val < 0) val = -val;
                                    }
                                }
                                else
                                {
                                    if (val > 0) val = -val;
                                }
                            }
                            result = val;
                        }
                        break;




                    // 默认分支：处理模糊匹配 (StartsWith/Contains)
                    default:
                        // 3. 网络与磁盘
                        if (key.StartsWith("NET"))
                        {
                            // A. 只有在没有指定首选网卡时，尝试走计数器 (虽然目前 NET 还没全量走计数器，但逻辑要统一)
                            // 目前 NET 依然走静态缓存或 NetworkManager
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

                            // ★★★ 核心修正：优先级翻转 ★★★
                            // 1. 如果是【自动模式】且开启了计数器，必须【绝对优先】走计数器 (获取系统总速度)
                            if (useCounter && !isSpecificDisk)
                            {
                                if (key == "DISK.Read") result = _perfManager.GetDiskRead();
                                else if (key == "DISK.Write") result = _perfManager.GetDiskWrite();
                                else if (key == "DISK.Activity") 
                                {
                                    result = _perfManager.GetDiskActive();
                                    // [Fix] 计数器活跃度可能 > 100%，强制钳位
                                    if (result.HasValue) result = Math.Clamp(result.Value, 0f, 100f);
                                }
                            }

                            // 2. 如果是【首选磁盘模式】，或者计数器没拿到值，尝试走静态缓存 (极速路径)
                            if (result == null && _manualSensorCache.TryGetValue(key, out var sDisk))
                            {
                                result = sDisk.Value;
                            }

                            // 3. 最后兜底：走 LHM/DiskManager 的动态扫描
                            if (result == null)
                            {
                                result = _diskManager.GetBestValue(key, _computer, _cfg, _lastValidMap, _lock);
                            }
                        }
                        // 5. 频率与功耗
                        else if (key.Contains("Clock") || key.Contains("Power"))
                        {
                            // A. CPU 频率尝试走计数器
                        if (useCounter && key == "CPU.Clock")
                        {
                            result = _perfManager.GetCpuFreq();
                        }
                            
                            // B. 回退
                            if (result == null)
                            {
                                result = GetCompositeValue(key);
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
        }

        // =====================================================================
        // ★★★ [急速反向查找] 解析父级名 -> 定位根硬件 -> 查找分支 ★★★
        // 只在配置改变或启动时运行一次，随后进入缓存
        // =====================================================================
        private ISensor? FindSensorReverse(string savedString, SensorType type)
        {
            if (string.IsNullOrEmpty(savedString) || savedString.Contains("Auto") || savedString.Contains("自动")) 
                return null;

            int idx = savedString.LastIndexOf('[');
            if (idx < 0) return null; 

            // 预处理字符串
            string targetSensorName = savedString.Substring(0, idx).Trim();
            string targetHardwareName = savedString.Substring(idx + 1).TrimEnd(']');

            // 局部递归
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

            // 极速定位：只遍历 5-10 个根节点
            foreach (var hw in _computer.Hardware)
            {
                if (hw.Name == targetHardwareName)
                {
                    return SearchBranch(hw);
                }
            }

            return null;
        }

        private float? GetCompositeValue(string key)
        {
            if (key == "CPU.Clock")
            {
                if (_sensorMap.CpuCoreCache.Count == 0) return null;
                double sum = 0; int count = 0; float maxRaw = 0;
                
                // Zen 5 修正 (仅针对 LHM 原始数据)
                float correctionFactor = 1.0f;
                if (_sensorMap.CpuBusSpeedSensor != null && _sensorMap.CpuBusSpeedSensor.Value.HasValue)
                {
                    float bus = _sensorMap.CpuBusSpeedSensor.Value.Value;
                    if (bus > 1.0f && bus < 20.0f) { float factor = 100.0f / bus; if (factor > 2.0f && factor < 10.0f) correctionFactor = factor; }
                }

                foreach (var core in _sensorMap.CpuCoreCache)
                {
                    if (core.Clock == null || !core.Clock.Value.HasValue) continue;
                    float clk = core.Clock.Value.Value * correctionFactor;
                    if (clk > maxRaw) maxRaw = clk;
                    if (clk > 400f) { sum += clk; count++; }
                }
                if (maxRaw > 0) _cfg.UpdateMaxRecord(key, maxRaw);
                if (count > 0) return (float)(sum / count);
                return maxRaw;
            }

            if (key == "CPU.Power")
            {
                if (_manualSensorCache.TryGetValue("CPU.Power", out var s) && s.Value.HasValue) 
                { 
                    _cfg.UpdateMaxRecord(key, s.Value.Value); 
                    return s.Value.Value; 
                }
                return null;
            }

            if (key.StartsWith("GPU"))
            {
                var gpu = _sensorMap.CachedGpu;
                if (gpu == null) return null;
                if (key == "GPU.Clock")
                {
                    var s = gpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Clock && (SensorMap.Has(x.Name, "graphics") || SensorMap.Has(x.Name, "core") || SensorMap.Has(x.Name, "shader")));
                    if (s != null && s.Value.HasValue) { float val = s.Value.Value; if (val > 6000.0f) return null; _cfg.UpdateMaxRecord(key, val); return val; }
                }
                else if (key == "GPU.Power")
                {
                    var s = gpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Power && (SensorMap.Has(x.Name, "package") || SensorMap.Has(x.Name, "ppt") || SensorMap.Has(x.Name, "board") || SensorMap.Has(x.Name, "core") || SensorMap.Has(x.Name, "gpu")));
                    if (s != null && s.Value.HasValue) { float val = s.Value.Value; if (val > 2000.0f) return null; _cfg.UpdateMaxRecord(key, val); return val; }
                }
            }
            return null;
        }

        public void Dispose()
        {
        }
    }
}