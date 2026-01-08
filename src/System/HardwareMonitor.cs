using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using System.Linq; // 确保引用

namespace LiteMonitor.src.SystemServices
{
    public sealed class HardwareMonitor : IDisposable
    {
        public static HardwareMonitor? Instance { get; private set; }
        public event Action? OnValuesUpdated;

        private readonly Settings _cfg;
        private readonly Computer _computer;
        private readonly object _lock = new object();

        // 拆分出的子服务
        private readonly SensorMap _sensorMap;
        private readonly NetworkManager _networkManager;
        private readonly DiskManager _diskManager;
        private readonly DriverInstaller _driverInstaller;
        private readonly HardwareValueProvider _valueProvider;

        private readonly Dictionary<string, float> _lastValidMap = new();

        private DateTime _lastTrafficTime = DateTime.Now;
        private DateTime _lastTrafficSave = DateTime.Now;
        private DateTime _startTime = DateTime.Now;
        private DateTime _lastSlowScan = DateTime.Now;
        private DateTime _lastDiskBgScan = DateTime.Now;

        public HardwareMonitor(Settings cfg)
        {
            _cfg = cfg;
            Instance = this;

            _computer = new Computer()
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsNetworkEnabled = true,
                IsStorageEnabled = true,
                // ★★★ 修改：开启主板，关闭控制器(遵照指示) ★★★
                IsMotherboardEnabled = true,
                IsControllerEnabled = false
            };

            // 初始化子服务
            _sensorMap = new SensorMap();
            _networkManager = new NetworkManager();
            _diskManager = new DiskManager();
            _driverInstaller = new DriverInstaller(cfg, _computer, ReloadComputerSafe);
            _valueProvider = new HardwareValueProvider(_computer, cfg, _sensorMap, _networkManager, _diskManager, _lock, _lastValidMap);

            Task.Run(async () =>
            {
                try
                {
                    _computer.Open();
                    _sensorMap.Rebuild(_computer, cfg); // ★★★ 传入 cfg
                    await _driverInstaller.SmartCheckDriver();
                }
                catch { }
            });
        }

        public float? Get(string key) => _valueProvider.GetValue(key);

        public void UpdateAll()
        {
            try
            {
                DateTime now = DateTime.Now;
                double timeDelta = (now - _lastTrafficTime).TotalSeconds;
                _lastTrafficTime = now;
                if (timeDelta > 5.0) timeDelta = 0;

                bool needCpu = _cfg.IsAnyEnabled("CPU");
                bool needGpu = _cfg.IsAnyEnabled("GPU");
                bool needMem = _cfg.IsAnyEnabled("MEM");
                bool needNet = _cfg.IsAnyEnabled("NET") || _cfg.IsAnyEnabled("DATA");
                bool needDisk = _cfg.IsAnyEnabled("DISK");
                // ★★★ [新增] 判断主板更新需求 ★★★
                bool needMobo = _cfg.IsAnyEnabled("MOBO") || _cfg.IsAnyEnabled("CPU.Fan");

                bool isSlowScanTick = (now - _lastSlowScan).TotalSeconds > 3;
                bool needDiskBgScan = (now - _lastDiskBgScan).TotalSeconds > 10;

                lock (_lock)
                {
                    foreach (var hw in _computer.Hardware)
                    {
                        if (hw.HardwareType == HardwareType.Cpu && needCpu) { hw.Update(); continue; }
                        if ((hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuIntel) && needGpu) { hw.Update(); continue; }
                        if (hw.HardwareType == HardwareType.Memory && needMem) { hw.Update(); continue; }

                        if (hw.HardwareType == HardwareType.Network && needNet)
                        {
                            _networkManager.ProcessUpdate(hw, _cfg, timeDelta, isSlowScanTick);
                            continue;
                        }
                        if (hw.HardwareType == HardwareType.Storage && needDisk)
                        {
                            _diskManager.ProcessUpdate(hw, _cfg, isSlowScanTick, needDiskBgScan);
                            continue;
                        }
                        
                        // ★★★ [新增] 递归更新主板 (Motherboard / SuperIO) ★★★
                        if ((hw.HardwareType == HardwareType.Motherboard || hw.HardwareType == HardwareType.SuperIO) && needMobo)
                        {
                             UpdateWithSubHardware(hw);
                             continue;
                        }
                    }
                }

                if (isSlowScanTick) _lastSlowScan = now;
                if (needDiskBgScan) _lastDiskBgScan = now;

                _valueProvider.UpdateSystemCpuCounter();

                if ((now - _lastTrafficSave).TotalSeconds > 60)
                {
                    TrafficLogger.Save();
                    _lastTrafficSave = now;
                }

                OnValuesUpdated?.Invoke();
            }
            catch { }
        }

        // ★★★ [新增] 递归更新子硬件，确保 SuperIO 刷新 ★★★
        private void UpdateWithSubHardware(IHardware hw)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) 
            {
                UpdateWithSubHardware(sub);
            }
        }

        private void ReloadComputerSafe()
        {
            try
            {
                lock (_lock)
                {
                    _networkManager.ClearCache();
                    _diskManager.ClearCache();
                    _sensorMap.Clear();
                    _computer.Close();
                    _computer.Open();
                }
                _sensorMap.Rebuild(_computer, _cfg); // ★★★ 传入 cfg
            }
            catch { }
        }

        public void Dispose()
        {
            _computer.Close();
            _valueProvider.Dispose();
            _networkManager.ClearCache();
            _diskManager.ClearCache(); // 漏掉的，补上
        }
        
        // 静态辅助方法 (UI用)
        public static List<string> ListAllNetworks() => Instance?._computer.Hardware.Where(h => h.HardwareType == HardwareType.Network).Select(h => h.Name).Distinct().ToList() ?? new List<string>();
        public static List<string> ListAllDisks() => Instance?._computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage).Select(h => h.Name).Distinct().ToList() ?? new List<string>();
        
        // ★★★ [新增] 列出所有风扇 ★★★
        public static List<string> ListAllFans()
        {
            if (Instance == null) return new List<string>();
            var list = new List<string>();

            foreach (var hw in Instance._computer.Hardware)
            {
                // ★★★ 核心优化：只允许主板、SuperIO 和 水冷控制器 ★★★
                // 排除掉 GPU、硬盘、内存等不可能接 CPU 风扇的硬件
                bool isAssignable = hw.HardwareType == HardwareType.Motherboard || 
                                    hw.HardwareType == HardwareType.SuperIO || 
                                    hw.HardwareType == HardwareType.Cooler;

                if (isAssignable)
                {
                    // 递归查找该硬件下的所有风扇
                    foreach (var s in GetAllSensors(hw, SensorType.Fan))
                    {
                        // 格式化为唯一标识符: "[硬件名] 传感器名"
                        // 例如: "[Nuvoton NCT6796D] Fan #1"
                        //list.Add($"[{hw.Name}] {s.Name}");
                        list.Add($"{s.Name}");
                    }
                }
            }
            
            // 排序并去重，让列表更整洁
            list.Sort(); 
            return list.Distinct().ToList();
        }

        // ★★★ [新增] 列出主板温度 ★★★
        public static List<string> ListAllMoboTemps()
        {
            if (Instance == null) return new List<string>();
            var list = new List<string>();
            foreach (var hw in Instance._computer.Hardware)
            {
                if (hw.HardwareType == HardwareType.Motherboard || hw.HardwareType == HardwareType.SuperIO)
                {
                    list.AddRange(GetAllSensors(hw, SensorType.Temperature).Select(s => s.Name));
                }
            }
            return list.Distinct().ToList();
        }

        private static IEnumerable<ISensor> GetAllSensors(IHardware hw, SensorType type)
        {
            foreach (var s in hw.Sensors) if (s.SensorType == type) yield return s;
            foreach (var sub in hw.SubHardware) 
                foreach (var s in GetAllSensors(sub, type)) yield return s;
        }
    }
}