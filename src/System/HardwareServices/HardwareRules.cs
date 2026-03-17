using System;
using LibreHardwareMonitor.Hardware;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 硬件规则 (原 HardwareClassifier)
    /// 包含硬件优先级、显卡类型判断等核心业务规则
    /// 通俗易懂：这里定义了"什么样的硬件更好"、"什么样的显卡该用什么内存"
    /// </summary>
    public static class HardwareRules
    {
        // 字符串匹配辅助 (高性能版)
        public static bool Has(string source, string sub)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(sub)) return false;
            return source.AsSpan().Contains(sub.AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 获取硬件优先级 (数字越小优先级越高，越强)
        /// 0: 顶级独显 / 1: 顶级核显 / 2: 中端 / 3: 低端 / 4: 其他 / 100: 垃圾
        /// </summary>
        public static int GetHwPriority(IHardware hw)
        {
            string name = hw.Name;
            
            // 1. 特殊处理：Microsoft Basic Render Driver 永远垫底
            if (name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)) return 100;
            
            // ★★★ [新增] 摩尔线程 GPU 优先级 (与 Nvidia/AMD 独显同级) ★★★
            if (IsMttGpu(hw, name)) return 0;

            // 2. Nvidia 显卡 (默认视为独显/最强)
            if (hw.HardwareType == HardwareType.GpuNvidia) return 0;

            // 3. AMD 显卡
            if (hw.HardwareType == HardwareType.GpuAmd)
            {
                // 通用名 "AMD Radeon(TM) Graphics" 通常是核显 -> 优先级 2
                if (name.Equals("AMD Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)) return 2;
                // 其他具体型号 (如 RX 7900 XTX) 视为独显 -> 优先级 0
                return 0;
            }

            // 4. Intel 显卡
            if (hw.HardwareType == HardwareType.GpuIntel)
            {
                // Arc 系列
                if (name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                {
                    // Arc 独显 (A/B/Pro) -> 优先级 0
                    if (IsDiscreteArc(name)) return 0;
                    // Arc 核显 (140V) -> 优先级 1 (核显中的王者)
                    return 1;
                }

                // Iris -> 优先级 2
                if (name.Contains("Iris", StringComparison.OrdinalIgnoreCase)) return 2;

                // UHD -> 优先级 3
                if (name.Contains("UHD", StringComparison.OrdinalIgnoreCase)) return 3;
            }

            // 其他 -> 优先级 4
            return 4;
        }
        
        /// <summary>
        /// ★★★ [新增] 判断是否为摩尔线程 GPU ★★★
        /// </summary>
        public static bool IsMttGpu(IHardware hw, string? name = null)
        {
            name ??= hw.Name;
            if (string.IsNullOrEmpty(name)) return false;
            
            // 摩尔线程 GPU 名称特征：MTT, Moore Threads, 摩尔线程
            return Has(name, "MTT") || 
                   Has(name, "Moore Threads") || 
                   Has(name, "摩尔线程") ||
                   Has(name, "MT ");
        }

        /// <summary>
        /// 判断是否为独显版 Arc (A/B/Pro 系列)
        /// </summary>
        public static bool IsDiscreteArc(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!name.Contains("Arc", StringComparison.OrdinalIgnoreCase)) return false;
            
            // 独显特征：通常带有 A/B/Pro 系列号 (如 A770, B580, Pro A40)
            // 核显特征：通常叫 "Intel Arc Graphics" 或 "Intel Arc 140V" (无系列前缀)
            // 简单启发式：检查是否包含 " A", " B", " Pro" (注意空格)
            return Has(name, " A") || Has(name, " B") || Has(name, " Pro");
        }

        /// <summary>
        /// 判断是否应该使用共享内存 (Shared Memory)
        /// </summary>
        public static bool ShouldUseSharedMemory(IHardware hw)
        {
            // 只有 Intel 核显才优先使用共享内存
            // Intel 独显 (Arc A/B/Pro) 和其他厂商 (Nvidia/AMD/MTT) 都使用专用显存 (Dedicated)
            if (hw.HardwareType == HardwareType.GpuIntel)
            {
                // 如果是 Discrete Arc，则不使用 Shared (即使用 Dedicated)
                if (IsDiscreteArc(hw.Name)) return false;
                
                // 否则是 Integrated (UHD/Iris/Arc Integrated)，使用 Shared
                return true;
            }
            
            return false;
        }
    }
}
