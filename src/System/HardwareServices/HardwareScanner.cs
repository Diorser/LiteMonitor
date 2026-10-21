using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 硬件扫描器：负责硬件的发现、列举和命名逻辑 (主要服务于 UI 设置)
    /// </summary>
    public static class HardwareScanner
    {
        private static List<string>? _cachedFanList = null;
        private static List<string>? _cachedNetworkList = null;
        private static List<string>? _cachedDiskList = null;
        private static List<GpuOption>? _cachedGpuOptions = null;
        private static List<SensorOption>? _cachedCpuTempOptions = null;
        private static List<string>? _cachedMoboTempList = null;

        public sealed class GpuOption
        {
            public string Label { get; set; } = "";
            public string Value { get; set; } = "";
            public string Name { get; set; } = "";
        }

        public sealed class SensorOption
        {
            public string Label { get; set; } = "";
            public string Value { get; set; } = "";
            public string Name { get; set; } = "";
        }

        /// <summary>
        /// 清除所有扫描缓存
        /// </summary>
        public static void ClearCache()
        {
            _cachedFanList = null;
            _cachedNetworkList = null;
            _cachedDiskList = null;
            _cachedGpuOptions = null;
            _cachedCpuTempOptions = null;
            _cachedMoboTempList = null;
        }

        /// <summary>
        /// 智能命名：将传感器与其所属硬件结合命名，并处理 SuperIO 芯片名的替换
        /// </summary>
        public static string GenerateSmartName(ISensor sensor, IHardware hardware, IComputer computer)
        {
            string hwName = hardware.Name;
            // 如果是 SuperIO，尝试替换为主板名
            if (hardware.HardwareType == HardwareType.SuperIO)
            {
                var mobo = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
                if (mobo != null) hwName = mobo.Name;
            }
            return $"{sensor.Name} [{hwName}]";
        }

        /// <summary>
        /// 列出所有网卡名称
        /// </summary>
        public static List<string> ListAllNetworks(IComputer computer)
        {
            if (_cachedNetworkList != null && _cachedNetworkList.Count > 0)
                return _cachedNetworkList.ToList();

            var list = computer.Hardware
                .Where(h => h.HardwareType == HardwareType.Network)
                .Select(h => h.Name).Distinct().ToList();

            if (list.Count > 0) _cachedNetworkList = list;
            return list.ToList();
        }

        /// <summary>
        /// 列出所有硬盘名称
        /// </summary>
        public static List<string> ListAllDisks(IComputer computer)
        {
            if (_cachedDiskList != null && _cachedDiskList.Count > 0)
                return _cachedDiskList.ToList();

            var list = computer.Hardware
                .Where(h => h.HardwareType == HardwareType.Storage)
                .Select(h => h.Name).Distinct().ToList();

            if (list.Count > 0) _cachedDiskList = list;
            return list.ToList();
        }

        /// <summary>
        /// 列出所有显卡名称，供旧调用点使用。
        /// </summary>
        public static List<string> ListAllGpus(IComputer computer)
        {
            return ListAllGpuOptions(computer).Select(x => x.Label).ToList();
        }

        /// <summary>
        /// 列出所有显卡选项，Value 使用硬件 Identifier，避免同型号多显卡被合并。
        /// </summary>
        public static List<GpuOption> ListAllGpuOptions(IComputer computer)
        {
            if (_cachedGpuOptions != null && _cachedGpuOptions.Count > 0)
                return CloneGpuOptions(_cachedGpuOptions);

            var gpus = computer.Hardware.Where(IsGpuHardware).ToList();
            var nameCounts = gpus
                .GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var nameIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var list = new List<GpuOption>();

            foreach (var gpu in gpus)
            {
                string name = gpu.Name ?? "";
                nameIndexes.TryGetValue(name, out int currentIndex);
                currentIndex++;
                nameIndexes[name] = currentIndex;

                bool duplicatedName = nameCounts.TryGetValue(name, out int count) && count > 1;
                string label = duplicatedName ? $"{name} #{currentIndex}" : name;

                list.Add(new GpuOption
                {
                    Label = label,
                    Value = GetHardwareValue(gpu),
                    Name = name
                });
            }

            if (list.Count > 0) _cachedGpuOptions = CloneGpuOptions(list);
            return CloneGpuOptions(list);
        }

        public static IHardware? FindGpuByConfig(IComputer computer, string configValue)
        {
            if (string.IsNullOrWhiteSpace(configValue)) return null;

            var gpus = computer.Hardware.Where(IsGpuHardware).ToList();
            var byIdentifier = gpus.FirstOrDefault(h =>
                string.Equals(GetHardwareValue(h), configValue, StringComparison.OrdinalIgnoreCase));
            if (byIdentifier != null) return byIdentifier;

            // 兼容旧配置：旧版本保存的是显卡名。若同名多卡，旧值无法唯一定位，交给自动模式。
            var byName = gpus.Where(h => string.Equals(h.Name, configValue, StringComparison.OrdinalIgnoreCase)).ToList();
            return byName.Count == 1 ? byName[0] : null;
        }

        public static GpuOption? FindGpuOptionByConfig(IComputer computer, string configValue)
        {
            if (string.IsNullOrWhiteSpace(configValue)) return null;

            var options = ListAllGpuOptions(computer);
            var byValue = options.FirstOrDefault(o =>
                string.Equals(o.Value, configValue, StringComparison.OrdinalIgnoreCase));
            if (byValue != null) return byValue;

            var byName = options.Where(o => string.Equals(o.Name, configValue, StringComparison.OrdinalIgnoreCase)).ToList();
            return byName.Count == 1 ? byName[0] : null;
        }

        public static bool IsGpuHardware(IHardware hw)
        {
            return hw.HardwareType == HardwareType.GpuNvidia ||
                   hw.HardwareType == HardwareType.GpuAmd ||
                   hw.HardwareType == HardwareType.GpuIntel;
        }

        private static string GetHardwareValue(IHardware hardware)
        {
            string identifier = hardware.Identifier?.ToString() ?? "";
            return string.IsNullOrWhiteSpace(identifier) ? (hardware.Name ?? "") : identifier;
        }

        private static List<GpuOption> CloneGpuOptions(List<GpuOption> source)
        {
            return source
                .Select(x => new GpuOption { Label = x.Label, Value = x.Value, Name = x.Name })
                .ToList();
        }

        private static List<SensorOption> CloneSensorOptions(List<SensorOption> source)
        {
            return source
                .Select(x => new SensorOption { Label = x.Label, Value = x.Value, Name = x.Name })
                .ToList();
        }

        /// <summary>
        /// 列出所有 CPU 温度传感器，供用户在 AMD/多 CPU/多 CCD 场景下手动选择 Entry。
        /// </summary>
        public static List<SensorOption> ListAllCpuTempOptions(IComputer computer, object syncLock)
        {
            if (_cachedCpuTempOptions != null && _cachedCpuTempOptions.Count > 0)
                return CloneSensorOptions(_cachedCpuTempOptions);

            var list = new List<SensorOption>();
            lock (syncLock)
            {
                var cpus = computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu).ToList();
                var nameCounts = cpus
                    .GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
                var nameIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var cpu in cpus)
                {
                    string cpuName = cpu.Name ?? "";
                    nameIndexes.TryGetValue(cpuName, out int currentIndex);
                    currentIndex++;
                    nameIndexes[cpuName] = currentIndex;

                    bool duplicatedName = nameCounts.TryGetValue(cpuName, out int count) && count > 1;
                    string cpuLabel = duplicatedName ? $"{cpuName} #{currentIndex}" : cpuName;

                    foreach (var s in cpu.Sensors)
                    {
                        if (s.SensorType != SensorType.Temperature) continue;
                        if (SensorMap.Has(s.Name, "Distance")) continue;

                        string label = $"{s.Name} [{cpuLabel}]";
                        string value = s.Identifier?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(value)) value = label;

                        list.Add(new SensorOption
                        {
                            Label = label,
                            Value = value,
                            Name = s.Name ?? ""
                        });
                    }
                }
            }

            var final = list
                .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (final.Count > 0) _cachedCpuTempOptions = CloneSensorOptions(final);
            return CloneSensorOptions(final);
        }

        /// <summary>
        /// 列出所有风扇传感器 (排除 CPU/GPU 等核心自带风扇，主要针对主板/机箱风扇)
        /// </summary>
        public static List<string> ListAllFans(IComputer computer, object syncLock)
        {
            if (_cachedFanList != null && _cachedFanList.Count > 0)
                return _cachedFanList.ToList();

            var list = new List<string>();
            lock (syncLock)
            {
                void Scan(IHardware hw)
                {
                    bool isExcluded = hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd ||
                                      hw.HardwareType == HardwareType.GpuIntel || hw.HardwareType == HardwareType.Cpu ||
                                      hw.HardwareType == HardwareType.Storage || hw.HardwareType == HardwareType.Memory ||
                                      hw.HardwareType == HardwareType.Network;

                    if (!isExcluded)
                    {
                        foreach (var s in hw.Sensors)
                        {
                            if (s.SensorType == SensorType.Fan) 
                                list.Add(GenerateSmartName(s, hw, computer));
                        }
                    }
                    foreach (var sub in hw.SubHardware) Scan(sub);
                }
                foreach (var hw in computer.Hardware) Scan(hw);
            }

            list.Sort();
            var final = list.Distinct().ToList();
            if (final.Count > 0) _cachedFanList = final;
            return final.ToList();
        }

        /// <summary>
        /// 列出所有适合作为主板/系统温度的传感器
        /// </summary>
        public static List<string> ListAllMoboTemps(IComputer computer, object syncLock)
        {
            if (_cachedMoboTempList != null && _cachedMoboTempList.Count > 0)
                return _cachedMoboTempList.ToList();

            var list = new List<string>();
            lock (syncLock)
            {
                void Scan(IHardware hw)
                {
                    bool isExcluded = hw.HardwareType == HardwareType.Cpu || hw.HardwareType == HardwareType.GpuNvidia ||
                                      hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuIntel ||
                                      hw.HardwareType == HardwareType.Storage || hw.HardwareType == HardwareType.Memory ||
                                      hw.HardwareType == HardwareType.Network;

                    if (!isExcluded)
                    {
                        foreach (var s in hw.Sensors)
                        {
                            if (s.SensorType == SensorType.Temperature) 
                                list.Add(GenerateSmartName(s, hw, computer));
                        }
                    }
                    foreach (var sub in hw.SubHardware) Scan(sub);
                }
                foreach (var hw in computer.Hardware) Scan(hw);
            }

            list.Sort();
            var final = list.Distinct().ToList();
            if (final.Count > 0) _cachedMoboTempList = final;
            return final.ToList();
        }
    }
}
