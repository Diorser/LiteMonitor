using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 核心服务：负责将物理硬件传感器映射为标准 Key (如 CPU.Temp)
    /// </summary>
    public class SensorMap
    {
        // 核心映射字典
        private readonly Dictionary<string, ISensor> _map = new();
        
        // 高性能缓存
        // CPU 核心传感器对 (用于加权平均计算)
        public class CpuCoreSensors
        {
            public ISensor? Clock;
            public ISensor? Load;
        }
        public List<CpuCoreSensors> CpuCoreCache { get; private set; } = new();
        
        // 显卡硬件缓存 (用于快速定位)
        public IHardware? CachedGpu { get; private set; }
        // ★★★ [新增] 缓存总线传感器 (用于 Zen 5 频率修正) ★★★
        public ISensor? CpuBusSpeedSensor { get; private set; }

        private DateTime _lastMapBuild = DateTime.MinValue;
        private readonly object _lock = new object();
        
        // ★★★ [新增] 配置引用 ★★★
        private Settings _cfg;

        public void EnsureFresh(Computer computer, Settings cfg) // ★ 签名修改
        {
            if ((DateTime.Now - _lastMapBuild).TotalMinutes > 10)
                Rebuild(computer, cfg);
        }

        public void Clear()
        {
            lock (_lock)
            {
                _map.Clear();
                CpuCoreCache.Clear();
                CachedGpu = null;
                CpuBusSpeedSensor = null;
            }
        }

        public bool TryGetSensor(string key, out ISensor? sensor)
        {
            lock (_lock)
            {
                return _map.TryGetValue(key, out sensor);
            }
        }

        // =======================================================================
        // [核心] 构建传感器映射与缓存 (最复杂的构建逻辑)
        // =======================================================================
        public void Rebuild(Computer computer, Settings cfg) // ★ 签名修改
        {
            _cfg = cfg;
            // 1. 准备临时容器 (线程安全)
            var newMap = new Dictionary<string, ISensor>();
            var newCpuCache = new List<CpuCoreSensors>();
            IHardware? newGpu = null;
            ISensor? newBusSensor = null; // 临时变量
            
            // ★★★ [新增] 临时列表：用于智能匹配 ★★★
            // 注意：这里不再在递归中直接处理风扇匹配，而是收集起来统一交给 ScanAndMapFans 处理
            // 但为了保持原代码结构，我们依然用candidates收集主板相关数据
            var candidatesMoboTemps = new List<ISensor>();

            // 局部递归函数
            void RegisterTo(IHardware hw)
            {
                hw.Update();

                // --- 填充 CPU 缓存 (用于加权平均) ---
                if (hw.HardwareType == HardwareType.Cpu)
                {
                    // ★★★ [新增] 查找并缓存 Bus Speed 传感器 ★★★
                    // 优先查找名为 "Bus Speed" 的时钟传感器
                    if (newBusSensor == null)
                    {
                        newBusSensor = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Clock && s.Name.Contains("Bus Speed"));
                    }

                    // 查找所有核心频率 (排除总线频率)
                    var clocks = hw.Sensors.Where(s => s.SensorType == SensorType.Clock && Has(s.Name, "core") && !Has(s.Name, "bus"));
                    
                    foreach (var clock in clocks)
                    {
                        // ★★★ 修复：AMD 负载叫 "CPU Core #1"，频率叫 "Core #1"，不相等。
                        // 改用 EndsWith 匹配，既支持 Intel (名字一样) 也支持 AMD (带前缀)
                        var load = hw.Sensors.FirstOrDefault(s => 
                            s.SensorType == SensorType.Load && 
                            s.Name.EndsWith(clock.Name, StringComparison.OrdinalIgnoreCase)); 
                            
                        newCpuCache.Add(new CpuCoreSensors { Clock = clock, Load = load });
                    }
                }

                // --- 填充 GPU 缓存 (优化版：智能选择独显) ---
                if (hw.HardwareType == HardwareType.GpuNvidia || 
                    hw.HardwareType == HardwareType.GpuAmd || 
                    hw.HardwareType == HardwareType.GpuIntel)
                {
                    // 如果还没找到显卡，直接用当前这个
                    if (newGpu == null)
                    {
                        newGpu = hw;
                    }
                    else
                    {
                        // 如果已经找到了一个显卡，但它是“弱鸡”核显，而当前这个是“强力”独显，则替换！
                        bool oldIsGeneric = IsGenericGpuName(newGpu.Name);
                        bool newIsSpecific = !IsGenericGpuName(hw.Name);
                        bool newIsArc = hw.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase);
                        bool oldIsArc = newGpu.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase);

                        // 优先选 Arc，其次选非通用名称的卡
                        if ((!oldIsArc && newIsArc) || (oldIsGeneric && newIsSpecific))
                        {
                            newGpu = hw;
                        }
                    }
                    
                    // ★★★ [新增] GPU 风扇映射 ★★★
                    var fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan);
                    if (fan == null) fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Control);
                    if (fan != null) newMap["GPU.Fan"] = fan;
                }
                
                // ★★★ [新增] 收集主板/SuperIO 传感器 ★★★
                if (hw.HardwareType == HardwareType.Motherboard || hw.HardwareType == HardwareType.SuperIO)
                {
                    foreach (var s in hw.Sensors)
                    {
                        // 风扇数据由后续 ScanAndMapFans 全局扫描，这里只收集温度备用
                        if (s.SensorType == SensorType.Temperature) candidatesMoboTemps.Add(s);
                    }
                }

                // --- 普通传感器映射 ---
                foreach (var s in hw.Sensors)
                {
                    string? key = NormalizeKey(hw, s); // 调用 Logic 文件中的方法
                    if (!string.IsNullOrEmpty(key) && !newMap.ContainsKey(key))
                        newMap[key] = s;
                }

                foreach (var sub in hw.SubHardware) RegisterTo(sub);
            }

            // 按优先级排序并注册
            var ordered = computer.Hardware.OrderBy(h => GetHwPriority(h));
            foreach (var hw in ordered) RegisterTo(hw);
            
            // ============================================
            // ★★★ [新增] 智能匹配逻辑 ★★★
            // ============================================
            
            // A. 主板温度 (策略：System > Motherboard > Chipset > MaxValue)
            if (!newMap.ContainsKey("MOBO.Temp") && candidatesMoboTemps.Count > 0)
            {
                // 1. 优先匹配标准名称 (System, Motherboard)
                var best = candidatesMoboTemps.FirstOrDefault(x => Has(x.Name, "System"));
                if (best == null) best = candidatesMoboTemps.FirstOrDefault(x => Has(x.Name, "Motherboard"));
                
                // 2. 其次匹配芯片组 (Chipset)
                if (best == null) best = candidatesMoboTemps.FirstOrDefault(x => Has(x.Name, "Chipset") || Has(x.Name, "PCH"));
                
                // 3. 兜底策略：找有效值中最大的 (假设热点即关键点，且排除 0 和 200+ 异常值)
                if (best == null)
                {
                    float maxVal = -999f;
                    foreach (var t in candidatesMoboTemps)
                    {
                        if (!t.Value.HasValue) continue;
                        float v = t.Value.Value;
                        // 过滤掉 0 和 >150 的异常读数
                        if (v > 0 && v < 150 && v > maxVal) 
                        { 
                            maxVal = v; 
                            best = t; 
                        }
                    }
                }
                
                if (best != null) newMap["MOBO.Temp"] = best;
            }

            // B. 风扇匹配 (调用全新智能算法)
            // 传入 newMap 以便写入 CPU.Fan 和 CASE.Fan
            ScanAndMapFans(computer, cfg, newMap);

            // 2. 原子交换数据 (加锁)
            lock (_lock)
            {
                _map.Clear();
                foreach (var kv in newMap) _map[kv.Key] = kv.Value;
                
                CpuCoreCache = newCpuCache;
                CachedGpu = newGpu;
                CpuBusSpeedSensor = newBusSensor; // ★ 更新 Bus Sensor 缓存
                _lastMapBuild = DateTime.Now;
            }
        }

        // ===========================================================
        // ★★★ [优化] 全局智能风扇匹配算法 (增加低转速过滤) ★★★
        // ===========================================================
        private void ScanAndMapFans(Computer computer, Settings cfg, Dictionary<string, ISensor> targetMap)
        {
            ISensor? cpuFan = null;
            ISensor? cpuPump = null; 
            ISensor? caseFan = null;
            
            // 1. 搜集全系统所有“活着”的风扇 
            // ★★★ 优化 1：初始搜集时稍微提高门槛，过滤掉 0-10 RPM 的纯底噪 ★★★
            var activeFans = new List<(IHardware Hw, ISensor S, float Rpm)>();
            
            void CollectFans(IHardware hw)
            {
                hw.Update();
                foreach (var s in hw.Sensors)
                {
                    // 包含 HardwareType.Cooler 下的风扇/Control
                    // 这里过滤 > 10 RPM，避免绝对的 1-2 转干扰
                    if ((s.SensorType == SensorType.Fan) && s.Value.HasValue && s.Value.Value > 10) 
                    {
                        activeFans.Add((hw, s, s.Value.Value));
                    }
                }
                foreach (var sub in hw.SubHardware) CollectFans(sub);
            }
            foreach (var hw in computer.Hardware) CollectFans(hw);

            // 辅助函数：判断是否已被占用
            bool IsTaken(ISensor s) => s == cpuFan || s == cpuPump || s == caseFan;

            // -----------------------------------------------------------
            // 阶段 1: 尊重用户手动配置 (最高优先级 - 即使转速低也认)
            // -----------------------------------------------------------
            if (!string.IsNullOrEmpty(cfg.PreferredCpuFan)) cpuFan = FindFanByConfig(activeFans, cfg.PreferredCpuFan);
            if (!string.IsNullOrEmpty(cfg.PreferredCaseFan)) caseFan = FindFanByConfig(activeFans, cfg.PreferredCaseFan);
            if (!string.IsNullOrEmpty(cfg.PreferredCpuPump)) cpuPump = FindFanByConfig(activeFans, cfg.PreferredCpuPump);

            // -----------------------------------------------------------
            // 阶段 2: 确定 CPU 风扇 (核心任务 - 优先找名字匹配的)
            // -----------------------------------------------------------
            if (cpuFan == null && activeFans.Count > 0)
            {
                // 1. 优先找名字里带 CPU 的未占用风扇
                var match = activeFans.FirstOrDefault(x => !IsTaken(x.S) && Has(x.S.Name, "CPU"));
                
                if (match.S != null)
                {
                    cpuFan = match.S;
                }
                else
                {
                    // 2. 如果没名字带 CPU 的，默认取列表里第一个未占用的 (通常主板 Fan #1 是 CPU)
                    // 注意：这里我们信任主板的顺序，通常第一个非零风扇就是 CPU
                    var firstAvailable = activeFans.FirstOrDefault(x => !IsTaken(x.S));
                    if (firstAvailable.S != null) cpuFan = firstAvailable.S;
                }
            }

            // -----------------------------------------------------------
            // 阶段 3: 基于名称的精确匹配 (Pump / Case)
            // -----------------------------------------------------------
            if (cpuPump == null)
            {
                var match = activeFans.FirstOrDefault(x => !IsTaken(x.S) && 
                    (Has(x.S.Name, "Pump") || Has(x.S.Name, "Water") || Has(x.S.Name, "AIO") || x.Hw.HardwareType == HardwareType.Cooler));
                if (match.S != null) cpuPump = match.S;
            }

            if (caseFan == null)
            {
                var match = activeFans.FirstOrDefault(x => !IsTaken(x.S) && 
                    (Has(x.S.Name, "Chassis") || Has(x.S.Name, "Sys") || Has(x.S.Name, "Case")));
                if (match.S != null) caseFan = match.S;
            }

            // -----------------------------------------------------------
            // 阶段 4: 剩余风扇的“猜谜”逻辑 (基于转速对比)
            // -----------------------------------------------------------
            // 获取所有还未被分配的风扇
            var leftovers = activeFans.Where(x => !IsTaken(x.S)).ToList();

            // ★★★ 优化 2：在自动分配机箱/水泵时，严格过滤掉低转速（如停转或幽灵读数） ★★★
            // 只有 > 300 RPM 的风扇才有资格参与“竞标”
            // 这样 2 RPM 的风扇会被直接忽略，不会被当成“最小转速”分配给机箱
            var validLeftovers = leftovers.Where(x => x.Rpm > 300).OrderBy(x => x.Rpm).ToList();

            if (validLeftovers.Count > 0)
            {
                // 场景 A: 还需要分配机箱风扇
                if (caseFan == null)
                {
                    // 取有效转速中最低的 (静音逻辑)
                    caseFan = validLeftovers.First().S;
                    validLeftovers.RemoveAt(0); 
                }
            }

            if (validLeftovers.Count > 0)
            {
                // 场景 B: 还需要分配水泵
                if (cpuPump == null)
                {
                    // 取有效转速中最高的 (水泵通常转速高)
                    cpuPump = validLeftovers.Last().S;
                }
            }

            // 3. 写入 Map
            if (cpuFan != null) targetMap["CPU.Fan"] = cpuFan;
            if (caseFan != null) targetMap["CASE.Fan"] = caseFan;
            if (cpuPump != null) targetMap["CPU.Pump"] = cpuPump;
        }

        // 辅助：全局查找指定配置的风扇 (即使它转速为0也能找到)
       private ISensor? FindFanByConfig(List<(IHardware Hw, ISensor S, float Rpm)> fans, string configStr)
        {
            // 需要处理一下 activeFans 列表可能没有包含用户指定的情况(如果是0转速)
            // 但为了简化，假设用户选的都是转的。如果需要极其严谨，可以保留原先遍历 computer 的逻辑。
            foreach (var item in fans)
            {
                string uid = $"[{item.Hw.Name}] {item.S.Name}";
                if (uid == configStr || item.S.Name == configStr) return item.S;
            }
            return null; // 这里简化了，如果 activeFans 里没有就返回 null
        }

        // ===========================================================
        // [重要] 传感器名称标准化映射 (原 Logic.cs 中的 NormalizeKey)
        // ===========================================================
        private string? NormalizeKey(IHardware hw, ISensor s)
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
            }

            // --- GPU ---
            if (type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                if (s.SensorType == SensorType.Load && (Has(name, "core") || Has(name, "d3d 3d"))) return "GPU.Load";
                if (s.SensorType == SensorType.Temperature && (Has(name, "core") || Has(name, "hot spot") || Has(name, "soc") || Has(name, "vr"))) return "GPU.Temp";
                
                // VRAM
                if (s.SensorType == SensorType.SmallData && (Has(name, "memory") || Has(name, "dedicated")))
                {
                    if (Has(name, "used")) return "GPU.VRAM.Used";
                    if (Has(name, "total")) return "GPU.VRAM.Total";
                }
                if (s.SensorType == SensorType.Load && Has(name, "memory")) return "GPU.VRAM.Load";
            }

            // --- Memory ---
            if (type == HardwareType.Memory) 
            {
                if (Has(hw.Name, "virtual")) return null;
                // 1. 负载 (保持不变)
                if (s.SensorType == SensorType.Load && Has(name, "memory")) return "MEM.Load";
                
                // 2. ★ 增强版匹配：同时接受 Data 和 SmallData
                if (s.SensorType == SensorType.Data || s.SensorType == SensorType.SmallData)
                {
                    if (Has(name, "used")) return "MEM.Used";
                    if (Has(name, "available")) return "MEM.Available";
                }
            }

            return null;
        }

        // 公共辅助方法
        public static bool Has(string source, string sub)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(sub)) return false;
            return source.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsGenericGpuName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            // 常见核显名称: "AMD Radeon(TM) Graphics", "Intel(R) UHD Graphics"
            if (name.Equals("AMD Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Contains("UHD Graphics", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Contains("Iris", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private int GetHwPriority(IHardware hw)
        {
            // 如果是 Intel Arc，提到最高优先级
            if (hw.HardwareType == HardwareType.GpuIntel && 
                hw.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                return 0;

            return hw.HardwareType switch
            {
                HardwareType.GpuNvidia => 0,
                HardwareType.GpuAmd => 1,
                HardwareType.GpuIntel => 2,
                _ => 3
            };
        }
    }
}