using System;
using System.Collections.Generic;
using System.Diagnostics;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 摩尔线程 GPU 监控数据
    /// </summary>
    public class MttGpuData
    {
        public string Name { get; set; } = "";
        public string UUID { get; set; } = "";
        public uint Index { get; set; }
        
        // GPU 核心数据
        public float? Load { get; set; }           // 负载百分比
        public float? Temperature { get; set; }    // 温度 (°C)
        public float? Clock { get; set; }          // 核心频率 (MHz)
        public float? Power { get; set; }          // 功耗 (W)
        
        // 显存数据
        public float? VramUsed { get; set; }       // 已用显存 (MB)
        public float? VramTotal { get; set; }      // 总显存 (MB)
        public float? VramLoad { get; set; }       // 显存负载百分比
        public float? VramClock { get; set; }      // 显存频率 (MHz)
        
        // 风扇
        public float? FanRpm { get; set; }         // 风扇转速 (RPM)
    }

    /// <summary>
    /// 摩尔线程 GPU 监控器
    /// 通过 MTML 库获取摩尔线程显卡的实时数据
    /// </summary>
    public class MttGpuMonitor : IDisposable
    {
        private IntPtr _library = IntPtr.Zero;
        private IntPtr _device = IntPtr.Zero;
        private IntPtr _gpu = IntPtr.Zero;
        private IntPtr _memory = IntPtr.Zero;
        
        private readonly object _lock = new object();
        private bool _initialized = false;
        private bool _disposed = false;
        
        // 缓存设备信息
        private MttGpuData? _cachedData;
        private uint _deviceIndex;
        
        // 是否检测到摩尔线程 GPU
        public bool HasMttGpu => _initialized && _device != IntPtr.Zero;
        public string GpuName => _cachedData?.Name ?? "";
        
        /// <summary>
        /// 初始化 MTML 库并检测摩尔线程 GPU
        /// </summary>
        public bool Initialize()
        {
            lock (_lock)
            {
                if (_initialized) return HasMttGpu;
                
                try
                {
                    // 1. 初始化 MTML 库
                    int result = MtmlNative.mtmlLibraryInit(out _library);
                    if (result != MtmlNative.MTML_SUCCESS || _library == IntPtr.Zero)
                    {
                        Debug.WriteLine($"[MTT] mtmlLibraryInit failed: {result}");
                        _initialized = true;
                        return false;
                    }
                    
                    // 2. 检查设备数量
                    result = MtmlNative.mtmlLibraryCountDevice(_library, out uint count);
                    if (result != MtmlNative.MTML_SUCCESS || count == 0)
                    {
                        Debug.WriteLine($"[MTT] No MTT device found, count: {count}");
                        _initialized = true;
                        return false;
                    }
                    
                    // 3. 初始化第一个设备
                    result = MtmlNative.mtmlLibraryInitDeviceByIndex(_library, 0, out _device);
                    if (result != MtmlNative.MTML_SUCCESS || _device == IntPtr.Zero)
                    {
                        Debug.WriteLine($"[MTT] mtmlLibraryInitDeviceByIndex failed: {result}");
                        _initialized = true;
                        return false;
                    }
                    
                    _deviceIndex = 0;
                    
                    // 4. 初始化 GPU 和 Memory 句柄
                    MtmlNative.mtmlDeviceInitGpu(_device, out _gpu);
                    MtmlNative.mtmlDeviceInitMemory(_device, out _memory);
                    
                    // 5. 获取设备名称
                    _cachedData = new MttGpuData { Index = 0 };
                    
                    byte[] nameBuffer = new byte[MtmlNative.MTML_DEVICE_NAME_BUFFER_SIZE];
                    if (MtmlNative.mtmlDeviceGetName(_device, nameBuffer, (uint)nameBuffer.Length) == MtmlNative.MTML_SUCCESS)
                    {
                        _cachedData.Name = System.Text.Encoding.ASCII.GetString(nameBuffer).TrimEnd('\0');
                    }
                    
                    byte[] uuidBuffer = new byte[MtmlNative.MTML_DEVICE_UUID_BUFFER_SIZE];
                    if (MtmlNative.mtmlDeviceGetUUID(_device, uuidBuffer, (uint)uuidBuffer.Length) == MtmlNative.MTML_SUCCESS)
                    {
                        _cachedData.UUID = System.Text.Encoding.ASCII.GetString(uuidBuffer).TrimEnd('\0');
                    }
                    
                    // 获取总显存 (只需一次)
                    if (_memory != IntPtr.Zero)
                    {
                        if (MtmlNative.mtmlMemoryGetTotal(_memory, out ulong total) == MtmlNative.MTML_SUCCESS)
                        {
                            _cachedData.VramTotal = total / (1024f * 1024f); // 转换为 MB
                            Settings.DetectedGpuVramTotalGB = _cachedData.VramTotal.Value / 1024f;
                        }
                    }
                    
                    _initialized = true;
                    Debug.WriteLine($"[MTT] Initialized: {_cachedData.Name}");
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MTT] Initialize error: {ex.Message}");
                    _initialized = true;
                    return false;
                }
            }
        }
        
        /// <summary>
        /// 更新 GPU 数据
        /// </summary>
        public MttGpuData? Update()
        {
            if (!HasMttGpu || _cachedData == null) return null;
            
            lock (_lock)
            {
                try
                {
                    var data = _cachedData;
                    
                    // GPU 核心数据
                    if (_gpu != IntPtr.Zero)
                    {
                        // 负载
                        if (MtmlNative.mtmlGpuGetUtilization(_gpu, out uint util) == MtmlNative.MTML_SUCCESS)
                        {
                            data.Load = Math.Clamp(util, 0f, 100f);
                        }
                        
                        // 温度
                        if (MtmlNative.mtmlGpuGetTemperature(_gpu, out int temp) == MtmlNative.MTML_SUCCESS)
                        {
                            data.Temperature = temp;
                        }
                        
                        // 核心频率
                        if (MtmlNative.mtmlGpuGetClock(_gpu, out uint clock) == MtmlNative.MTML_SUCCESS)
                        {
                            data.Clock = clock;
                        }
                    }
                    
                    // 功耗
                    if (_device != IntPtr.Zero)
                    {
                        if (MtmlNative.mtmlDeviceGetPowerUsage(_device, out uint power) == MtmlNative.MTML_SUCCESS)
                        {
                            data.Power = power / 1000f; // mW -> W
                        }
                    }
                    
                    // 显存数据
                    if (_memory != IntPtr.Zero)
                    {
                        // 已用显存
                        if (MtmlNative.mtmlMemoryGetUsed(_memory, out ulong used) == MtmlNative.MTML_SUCCESS)
                        {
                            data.VramUsed = used / (1024f * 1024f); // 转换为 MB
                            
                            // 计算显存负载
                            if (data.VramTotal.HasValue && data.VramTotal > 0)
                            {
                                data.VramLoad = (data.VramUsed.Value / data.VramTotal.Value) * 100f;
                            }
                        }
                        
                        // 显存频率
                        if (MtmlNative.mtmlMemoryGetClock(_memory, out uint memClock) == MtmlNative.MTML_SUCCESS)
                        {
                            data.VramClock = memClock;
                        }
                    }
                    
                    // 风扇转速
                    if (_device != IntPtr.Zero)
                    {
                        if (MtmlNative.mtmlDeviceCountFan(_device, out uint fanCount) == MtmlNative.MTML_SUCCESS && fanCount > 0)
                        {
                            if (MtmlNative.mtmlDeviceGetFanRpm(_device, 0, out uint rpm) == MtmlNative.MTML_SUCCESS)
                            {
                                data.FanRpm = rpm;
                            }
                        }
                    }
                    
                    return data;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MTT] Update error: {ex.Message}");
                    return null;
                }
            }
        }
        
        /// <summary>
        /// 获取指定指标的值
        /// </summary>
        public float? GetValue(string key)
        {
            var data = _cachedData;
            if (data == null) return null;
            
            return key switch
            {
                "GPU.Load" => data.Load,
                "GPU.Temp" => data.Temperature,
                "GPU.Clock" => data.Clock,
                "GPU.Power" => data.Power,
                "GPU.VRAM.Used" => data.VramUsed,
                "GPU.VRAM.Total" => data.VramTotal,
                "GPU.VRAM.Load" => data.VramLoad,
                "GPU.VRAM" => data.VramLoad,
                "GPU.Fan" => data.FanRpm,
                _ => null
            };
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            lock (_lock)
            {
                // MTML 的资源由库自动管理，不需要显式释放 GPU/Memory 句柄
                // 只需要关闭库
                if (_library != IntPtr.Zero)
                {
                    try { MtmlNative.mtmlLibraryShutDown(_library); } catch { }
                    _library = IntPtr.Zero;
                }
                
                _device = IntPtr.Zero;
                _gpu = IntPtr.Zero;
                _memory = IntPtr.Zero;
            }
            
            GC.SuppressFinalize(this);
        }
    }
}
