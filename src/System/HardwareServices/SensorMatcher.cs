using System;
using LibreHardwareMonitor.Hardware;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 传感器匹配器 (原 SensorMap.NormalizeKey)
    /// 职责：负责将花里胡哨的原始传感器名称，识别为标准 Key (如 "CPU.Temp")
    /// 通俗易懂：这是"翻译官"，把硬件术语翻译成软件能懂的通用词
    /// </summary>
    public static class SensorMatcher
    {
        // 复用 HardwareRules 的字符串匹配，避免重复造轮子
        private static bool Has(string source, string sub) => HardwareRules.Has(source, sub);

        /// <summary>
        /// 尝试匹配传感器名称到标准 Key
        /// </summary>
        public static string? Match(IHardware hw, ISensor s)
        {
            string name = s.Name;
            var type = hw.HardwareType;

            // --- CPU ---
            if (type == HardwareType.Cpu)
            {
                // 新代码：增加 "package" 支持，防止某些 CPU 把总负载叫 "CPU Package"
                if (s.SensorType == SensorType.Load)
                {
                    if (Has(name, "total") || Has(name, "package")) 
                        return "CPU.Load";
                }
                // [深度优化后的温度匹配逻辑]
                if (s.SensorType == SensorType.Temperature)
                {
                    // 1. 黄金标准：包含这些词的通常就是我们要的
                    if (Has(name, "package") ||  // Intel/AMD 标准
                        Has(name, "average") ||  // LHM 聚合数据
                        Has(name, "tctl") ||     // AMD 风扇控制温度 (最准)
                        Has(name, "tdie") ||     // AMD 核心硅片温度
                        Has(name, "ccd") ||       // AMD 核心板
                        Has(name, "cores"))     // 通用核心温度
                    {
                        return "CPU.Temp";
                    }

                    // 2. 银牌标准：通用名称兜底 (修复 AMD 7840HS 等移动端 CPU)
                    // 必须严格排除干扰项 (如 SOC, VRM, Pump 等)
                    if ((Has(name, "cpu") || Has(name, "core")) && 
                        !Has(name, "soc") &&     // 排除核显/片上系统
                        !Has(name, "vrm") &&     // 排除供电
                        !Has(name, "fan") &&     // 排除风扇(虽类型不同，但防名字干扰)
                        !Has(name, "pump") &&    // 排除水泵
                        !Has(name, "liquid") &&  // 排除水冷液
                        !Has(name, "coolant") && // 排除冷却液
                        !Has(name, "distance"))  // 排除 "Distance to TjMax"
                    {
                        return "CPU.Temp";
                    }
                }
                if (s.SensorType == SensorType.Power && (Has(name, "package") || Has(name, "cores"))) return "CPU.Power";
                // [New] CPU Voltage
                if (s.SensorType == SensorType.Voltage && (Has(name, "core") || Has(name, "cpu") || Has(name, "vcore") || Has(name, "vid"))) 
                {
                    // Exclude distractions
                    if (!Has(name, "soc") && !Has(name, "gt") && !Has(name, "sa") && !Has(name, "aux")) 
                        return "CPU.Voltage";
                }
            }

            // --- GPU ---
            if (type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                if (s.SensorType == SensorType.Load && (Has(name, "core") || Has(name, "d3d 3d"))) return "GPU.Load";
                if (s.SensorType == SensorType.Temperature && (Has(name, "core") || Has(name, "hot spot") || Has(name, "soc") || Has(name, "vr"))) return "GPU.Temp";
                
                // VRAM Logic (简化且准确)
                // 1. 根据硬件规则判断是否应该优先找共享内存 (核显)
                bool preferShared = HardwareRules.ShouldUseSharedMemory(hw);

                if (s.SensorType == SensorType.SmallData && (Has(name, "memory") || Has(name, "dedicated")))
                {
                    bool isShared = Has(name, "shared");
                    bool isDedicated = Has(name, "dedicated");
                    // Generic "Memory Used" (Vendor sensors often just say "Memory Used")
                    bool isGenericMem = !isShared && !isDedicated && Has(name, "memory");

                    if (preferShared)
                    {
                        // Integrated: Only accept Shared Memory
                        if (isShared && Has(name, "used")) return "GPU.VRAM.Used";
                        if (isShared && Has(name, "total")) return "GPU.VRAM.Total";
                    }
                    else
                    {
                        // Discrete: Accept Dedicated or Generic Memory (Block Shared)
                        if (!isShared) 
                        {
                            if (Has(name, "used")) return "GPU.VRAM.Used";
                            if (Has(name, "total")) return "GPU.VRAM.Total";
                        }
                    }
                }
                
                if (s.SensorType == SensorType.Load && Has(name, "memory")) return "GPU.VRAM.Load";
            }

            // --- Memory ---
            if (type == HardwareType.Memory) 
            {
                // [Critical Fix] 严格排除虚拟内存 (Virtual Memory)
                // LibreHardwareMonitor 会同时列出 "Generic Memory" 和 "Virtual Memory"
                // 必须通过 Hardware 名称进行过滤，否则可能错误地映射到虚拟内存的 Load 传感器
                if (Has(hw.Name, "virtual")) return null;
                
                // 1. 负载
                // ★★★ 增强版匹配：支持名称仅为 "Memory" 的负载传感器 (常见于 LibreHardwareMonitor) ★★★
                if (s.SensorType == SensorType.Load)
                {
                     if (Has(name, "memory")) return "MEM.Load";
                     // 兼容只有 "Load" 字样的传感器
                     if (name.Equals("Load", StringComparison.OrdinalIgnoreCase)) return "MEM.Load";
                }
                
                // 2. ★ 增强版匹配：同时接受 Data 和 SmallData
                if (s.SensorType == SensorType.Data || s.SensorType == SensorType.SmallData)
                {
                    if (Has(name, "used")) return "MEM.Used";
                    if (Has(name, "available")) return "MEM.Available";
                }
            }

            // --- Battery ---
            if (type == HardwareType.Battery)
            {
                if (s.SensorType == SensorType.Level)
                {
                    // Fix: 过滤掉电池损耗/健康度数据 (通常也标记为 Level 类型)
                    if (Has(name, "Degradation") || Has(name, "Wear")) return null;
                    
                    // ★★★ 优先选择包含 "Charge" 的传感器 ★★★
                    if (Has(name, "Charge")) return "BAT.Percent";
                    
                    // 其他作为备选 (Weak)
                    return "BAT.Percent";
                }
                if (s.SensorType == SensorType.Power)
                {
                    // 无论当前是充电还是放电，只要是 Power 类型的电池传感器，都进行映射。
                    return "BAT.Power";
                }
                if (s.SensorType == SensorType.Voltage) return "BAT.Voltage";
                if (s.SensorType == SensorType.Current) return "BAT.Current";
            }

            return null;
        }
    }
}
