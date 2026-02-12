using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// Windows 性能计数器统一管理器
    /// <para>负责管理所有基于 System.Diagnostics.PerformanceCounter 的系统级监控指标。</para>
    /// <para>优势：比 LHM 更快、更准（尤其是 CPU 频率和内存占用），且不占用硬件总线。</para>
    /// </summary>
    public class PerformanceCounterManager : IDisposable
    {
        // --- 核心计数器实例 ---
        private PerformanceCounter? _cpuLoadCounter;      // CPU 总使用率 (含内核时间)
        private PerformanceCounter? _cpuFreqCounter;      // CPU 性能百分比 (用于计算频率)
        private PerformanceCounter? _ramAvailableCounter; // 可用内存 (MB)
        private PerformanceCounter? _diskReadCounter;     // 磁盘总读取速度
        private PerformanceCounter? _diskWriteCounter;    // 磁盘总写入速度
        private PerformanceCounter? _diskActiveCounter;   // 磁盘活动时间 (%)
        private PerformanceCounter? _uptimeCounter;       // 系统运行时间

        // --- SMB 计数器 (用于忽略内网流量) ---
        // 经过探测发现，SMB Client Shares 经常缺失，而 Redirector (RDR) 和 Server 是更底层的组件
        // Redirector = 客户端流量 (我访问别人)
        // Server = 服务端流量 (别人访问我)
        // 这些通常是 SingleInstance 计数器，实例名为空字符串 ""
        private PerformanceCounter? _smbClientReadCounter;
        private PerformanceCounter? _smbClientWriteCounter;
        private PerformanceCounter? _smbServerReadCounter;
        private PerformanceCounter? _smbServerWriteCounter;

        // --- 静态基准数据 (启动时获取一次即可) ---
        private float _cpuBaseFreq = 0;   // CPU 基准频率 (MHz)
        private float _totalMemoryMB = 0; // 物理内存总量 (MB)

        /// <summary>
        /// 标记计数器是否已完成初始化和预热。
        /// </summary>
        public bool IsInitialized { get; private set; } = false;

        /// <summary>
        /// 获取检测到的物理内存总量 (GB)
        /// </summary>
        public float TotalMemoryGB => _totalMemoryMB / 1024f;

        /// <summary>
        /// 异步初始化所有计数器。
        /// <para>建议在程序启动时调用，运行在后台线程，避免阻塞 UI。</para>
        /// </summary>
        public void InitializeAsync()
        {
            Task.Run(() =>
            {
                try
                {
                    // 1. 获取静态硬件信息 (总内存、基准频率)
                    InitStaticHardwareInfo();

                    // 2. 创建计数器实例
                    // 注意：CategoryName 即使在中文系统通常也支持英文，为了兼容性优先用英文
                    
                    // CPU 负载：使用 Processor Information 类别 (Win8+ 推荐)
                    // [Fix] 使用 "% Processor Utility" 而非 "% Processor Time"
                    // 任务管理器在 Win8+ 显示的是 "Utility" (考虑睿频)，而非 "Time"
                    _cpuLoadCounter = CreateCounter("Processor Information", "% Processor Utility", "_Total");
                    if (_cpuLoadCounter == null)
                        _cpuLoadCounter = CreateCounter("Processor Information", "% Processor Time", "_Total"); // 回退1
                    if (_cpuLoadCounter == null)
                        _cpuLoadCounter = CreateCounter("Processor", "% Processor Time", "_Total"); // 旧系统回退

                    // CPU 频率：读取性能百分比 (Performance Limit)，需配合基准频率计算
                    _cpuFreqCounter = CreateCounter("Processor Information", "% Processor Performance", "_Total");
                    if (_cpuFreqCounter == null) 
                        _cpuFreqCounter = CreateCounter("Processor", "% Processor Performance", "_Total");

                    // 内存：只读取"可用内存"，通过 (总内存 - 可用) 计算占用，比直接读占用率更准
                    _ramAvailableCounter = CreateCounter("Memory", "Available MBytes");

                    // 磁盘：读取物理磁盘总和 (_Total)
                    _diskReadCounter = CreateCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                    _diskWriteCounter = CreateCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                    _diskActiveCounter = CreateCounter("PhysicalDisk", "% Disk Time", "_Total"); // 任务管理器里的磁盘%

                    // 系统：运行时间
                    _uptimeCounter = CreateCounter("System", "System Up Time");

                    // SMB (内网流量)：使用 SMB Client/Server Shares 类别
                    // Client: Read/Write Bytes/sec
                    _smbClientReadCounter = CreateCounter("SMB Client Shares", "Read Bytes/sec", "_Total");
                    _smbClientWriteCounter = CreateCounter("SMB Client Shares", "Write Bytes/sec", "_Total");

                    // Server: Received/Sent Bytes/sec
                    _smbServerReadCounter = CreateCounter("SMB Server Shares", "Received Bytes/sec", "_Total");
                    _smbServerWriteCounter = CreateCounter("SMB Server Shares", "Sent Bytes/sec", "_Total");

                    // 3. ★关键步骤★：预热 (Pre-warming)
                    // PerformanceCounter 的机制是计算两次采样的时间差。
                    // 第一次调用 NextValue() 永远返回 0。
                    // 必须在这里先读一次，这样用户在 UI 上第一次看到数据时就是有值的。
                    // [Fix] 使用 SafeRead 代替直接调用 NextValue，防止某个计数器崩溃导致整个初始化失败 (#268)
                    
                    SafeRead(_cpuLoadCounter);
                    SafeRead(_cpuFreqCounter);
                    SafeRead(_ramAvailableCounter);
                    SafeRead(_diskReadCounter);
                    SafeRead(_diskWriteCounter);
                    SafeRead(_diskActiveCounter);
                    SafeRead(_uptimeCounter);
                    
                    SafeRead(_smbClientReadCounter);
                    SafeRead(_smbClientWriteCounter);
                    SafeRead(_smbServerReadCounter);
                    SafeRead(_smbServerWriteCounter);

                    IsInitialized = true;
                }
                catch
                {
                    // 初始化失败通常是因为系统组件损坏 (如精简版 Windows)
                    // 这里只记录日志，IsInitialized 保持为 false，上层逻辑会自动回退到 LHM
                }
            });
        }

        /// <summary>
        /// 安全创建计数器的辅助方法
        /// </summary>
        private PerformanceCounter? CreateCounter(string category, string counter, string instance = "")
        {
            try
            {
                if (!PerformanceCounterCategory.Exists(category)) return null;
                return string.IsNullOrEmpty(instance) 
                    ? new PerformanceCounter(category, counter) 
                    : new PerformanceCounter(category, counter, instance);
            }
            catch
            {
                return null;
            }
        }

        private void InitStaticHardwareInfo()
        {
            // A. 获取 CPU 基准频率 (从注册表读取，比 WMI 快)
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                    if (key != null)
                    {
                        var val = key.GetValue("~MHz");
                        if (val is int freq) _cpuBaseFreq = freq;
                    }
                }
            }
            catch { }
            // 兜底策略：如果读不到，设为 2.5GHz，防止除零错误
            if (_cpuBaseFreq <= 0) _cpuBaseFreq = 2500; 

            // B. 获取物理内存总量 (调用 Win32 API GlobalMemoryStatusEx)
            try
            {
                MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(memStatus))
                {
                    _totalMemoryMB = memStatus.ullTotalPhys / 1024f / 1024f;
                }
            }
            catch { }
            // 兜底策略：默认 16GB
            if (_totalMemoryMB <= 0) _totalMemoryMB = 16 * 1024;
        }

        // ==========================================
        // 安全取值方法 (内部消化异常，失败返回 null)
        // ==========================================

        public float? GetCpuLoad() => SafeRead(_cpuLoadCounter);

        public float? GetCpuFreq()
        {
            // 算法：基准频率 * (性能百分比 / 100)
            // 例子：基准 3.0GHz * 150% = 4.5GHz
            var percent = SafeRead(_cpuFreqCounter);
            if (percent.HasValue && _cpuBaseFreq > 0)
            {
                return _cpuBaseFreq * (percent.Value / 100.0f);
            }
            return null;
        }

        /// <summary>
        /// 获取内存数据。
        /// 返回元组：(占用率%, 已用GB)
        /// </summary>
        public (float? Load, float? UsedGB) GetMemoryData()
        {
            var availableMB = SafeRead(_ramAvailableCounter);
            if (availableMB.HasValue && _totalMemoryMB > 0)
            {
                float usedMB = _totalMemoryMB - availableMB.Value;
                float load = (usedMB / _totalMemoryMB) * 100f;
                float usedGB = usedMB / 1024f;
                return (load, usedGB);
            }
            return (null, null);
        }

        public float? GetDiskRead() => SafeRead(_diskReadCounter);
        public float? GetDiskWrite() => SafeRead(_diskWriteCounter);
        public float? GetDiskActive() => SafeRead(_diskActiveCounter);
        public float? GetUptime() => SafeRead(_uptimeCounter);

        /// <summary>
        /// 获取本周期内估算的 SMB 流量增量 (含协议开销补偿)
        /// </summary>
        /// <param name="seconds">距离上次采样的秒数</param>
        public (long UpBytes, long DownBytes) GetEstimatedSmbBytes(double seconds)
        {
            var rate = GetSmbTrafficRate();

            // 增加 10% 的冗余系数，用于抵消 TCP/IP 协议头、SMB 协议头等开销
            // 否则会出现 "下载 600MB 文件，仍显示有 30MB 流量" 的情况
            // [Fix] 恢复 v1.2.8 的 1.1 系数。虽然理论上 1.0 更准确，但用户反馈 v1.2.8 有效而 v1.3.0 失效，
            // 可能是因为 PerformanceCounter 的平均值滞后导致 1.0 扣不干净。
            // [Update] 增加到 1.2 以更激进地扣除，防止剩余 10-20% 的流量
            const double OverheadFactor = 1.2;

            long up = (long)(rate.UpRate * seconds * OverheadFactor);
            long down = (long)(rate.DownRate * seconds * OverheadFactor);

            return (up, down);
        }

        /// <summary>
        /// 获取当前内网 SMB 流量速率 (Client + Server)
        /// <para>单位：Bytes/sec</para>
        /// </summary>
        private (float UpRate, float DownRate) GetSmbTrafficRate()
        {
            float up = 0;
            float down = 0;

            // 1. Client (Redirector)
            // Upload = Transmitted
            if (_smbClientWriteCounter != null) up += (SafeRead(_smbClientWriteCounter) ?? 0);
            // Download = Received
            if (_smbClientReadCounter != null) down += (SafeRead(_smbClientReadCounter) ?? 0);

            // 2. Server
            // Upload = Transmitted (别人下载我的文件)
            if (_smbServerWriteCounter != null) up += (SafeRead(_smbServerWriteCounter) ?? 0);
            // Download = Received (别人传文件给我)
            if (_smbServerReadCounter != null) down += (SafeRead(_smbServerReadCounter) ?? 0);

            return (up, down);
        }

        /// <summary>
        /// 包裹 try-catch 的读取，防止运行时因为系统组件重置导致的崩溃
        /// </summary>
        private float? SafeRead(PerformanceCounter? pc)
        {
            if (pc == null) return null;
            try
            {
                var val = pc.NextValue();
                return val;
            }
            catch
            {
                return null; 
            }
        }

        public void Dispose()
        {
            // 释放所有计数器资源
            _cpuLoadCounter?.Dispose();
            _cpuFreqCounter?.Dispose();
            _ramAvailableCounter?.Dispose();
            _diskReadCounter?.Dispose();
            _diskWriteCounter?.Dispose();
            _diskActiveCounter?.Dispose();
            _uptimeCounter?.Dispose();
            
            // 释放 SMB 计数器
            _smbClientReadCounter?.Dispose();
            _smbClientWriteCounter?.Dispose();
            _smbServerReadCounter?.Dispose();
            _smbServerWriteCounter?.Dispose();
        }

        // --- Win32 API 内存结构体定义 ---
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
    }
}
