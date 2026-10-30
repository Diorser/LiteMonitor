using System;
using System.IO;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// Intel 核显温度兜底读取器。
    ///
    /// 背景：在 Panther Lake（Core Ultra 系列 3）等新平台上，Intel IGCL 的
    /// gpuCurrentTemperature.bSupported 为 false，LibreHardwareMonitor 因此不会创建
    /// GPU 温度传感器。但硬件仍在 MCHBAR + 0x59C0 的 THERM_STATUS_GT 寄存器中暴露
    /// GT 温度（bit31 = 有效位，bit23:16 = 距 TjMax 的度数），该偏移在 Meteor Lake
    /// 上已被验证（LHM PR #2551 / Intel 寄存器文档 819323），本类把它作为 GPU.Temp 的兜底。
    ///
    /// 依赖：管理员权限 + PawnIO 2.x 驱动 + 随程序附带的 IntelMSR.bin / IntelMCHBAR.bin。
    /// 任何一步失败都会静默禁用，不影响其他监控项。
    /// </summary>
    internal static class IntelGpuTemperature
    {
        private const uint MSR_IA32_TEMPERATURE_TARGET = 0x1A2;
        private const uint MCHBAR_THERM_STATUS_GT = 0x59C0;
        private const int READ_CACHE_MS = 900;
        private const long LOG_MAX_BYTES = 256 * 1024;

        private static readonly object _sync = new object();
        private static bool _initTried;
        private static bool _available;
        private static string _status = "not initialized";
        private static PawnIoDevice? _device;
        private static uint _tjMax = 100;
        private static bool _tjMaxFromMsr;
        private static float? _lastValue;
        private static DateTime _lastReadUtc = DateTime.MinValue;
        private static int _loggedReads;

        /// <summary>当前兜底读取器的状态（用于日志/调试）。</summary>
        public static string Status { get { lock (_sync) { return _status; } } }

        /// <summary>尝试读取 GT 温度（摄氏度）。不可用时返回 null。</summary>
        public static float? GetTemperature()
        {
            lock (_sync)
            {
                try
                {
                    if (!_initTried)
                        Initialize();

                    if (!_available)
                        return null;

                    if ((DateTime.UtcNow - _lastReadUtc).TotalMilliseconds < READ_CACHE_MS)
                        return _lastValue;

                    _lastReadUtc = DateTime.UtcNow;
                    _lastValue = ReadOnce(out uint raw, out string? error);
                    if (_lastValue == null && !string.IsNullOrEmpty(error))
                        Log("read failed: " + error);
                    else if (_lastValue.HasValue && _loggedReads < 10)
                    {
                        _loggedReads++;
                        Log("temp=" + _lastValue.Value.ToString("0.0") + "C raw=0x" + raw.ToString("X8") + " tjMax=" + _tjMax + (_tjMaxFromMsr ? " (msr)" : " (default)"));
                    }
                    return _lastValue;
                }
                catch
                {
                    return null;
                }
            }
        }

        private static void Initialize()
        {
            _initTried = true;
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string msrPath = Path.Combine(baseDir, "resources", "PawnIo", "IntelMSR.bin");
                string mchbarPath = Path.Combine(baseDir, "resources", "PawnIo", "IntelMCHBAR.bin");

                if (!File.Exists(msrPath) || !File.Exists(mchbarPath))
                {
                    _status = "module files missing: " + msrPath + " / " + mchbarPath;
                    Log("disabled: " + _status);
                    return;
                }

                PawnIoDevice? device = PawnIoDevice.TryOpen(out string openError);
                if (device == null)
                {
                    _status = "PawnIO device unavailable (" + openError + ")";
                    Log("disabled: " + _status);
                    return;
                }

                if (!device.TryLoadModule(File.ReadAllBytes(msrPath)))
                {
                    _status = "IntelMSR.bin load failed";
                    Log("disabled: " + _status);
                    device.Dispose();
                    return;
                }

                if (device.TryExecute("ioctl_read_msr", new long[] { MSR_IA32_TEMPERATURE_TARGET }, 1, out long[] msrOut) && msrOut.Length > 0)
                {
                    uint eax = (uint)(msrOut[0] & 0xFFFFFFFF);
                    uint tjMax = (eax >> 16) & 0xFF;
                    if (tjMax >= 60 && tjMax <= 125)
                    {
                        _tjMax = tjMax;
                        _tjMaxFromMsr = true;
                    }
                }

                if (!device.TryLoadModule(File.ReadAllBytes(mchbarPath)))
                {
                    _status = "IntelMCHBAR.bin load failed (cpu msr " + (_tjMaxFromMsr ? "ok" : "unavailable") + ")";
                    Log("disabled: " + _status);
                    device.Dispose();
                    return;
                }

                _device = device;
                _available = true;
                _status = "enabled (tjMax=" + _tjMax + (_tjMaxFromMsr ? ", from MSR" : ", default") + ")";
                Log("initialized: " + _status);

                // 初始化立即做一次试读，验证寄存器偏移是否可用。
                float? probe = ReadOnce(out uint rawProbe, out string? probeError);
                if (probe.HasValue)
                {
                    _lastValue = probe;
                    _lastReadUtc = DateTime.UtcNow;
                    Log("probe ok: temp=" + probe.Value.ToString("0.0") + "C raw=0x" + rawProbe.ToString("X8") + " tjMax=" + _tjMax);
                }
                else
                {
                    _status = "enabled but probe read invalid (" + (probeError ?? "invalid register value") + ")";
                    Log("warning: " + _status + " raw=0x" + rawProbe.ToString("X8") + " tjMax=" + _tjMax);
                    DumpCandidates();
                }
            }
            catch (Exception ex)
            {
                _status = "init exception: " + ex.GetType().Name + ": " + ex.Message;
                Log("disabled: " + _status);
            }
        }

        /// <summary>
        /// 当 0x59C0 试读失败时，扫描 PCU 区块（0x5800-0x5FFF）中“像 THERM_STATUS_GT”的寄存器，
        /// 便于在其他平台（如 Panther Lake）上定位正确偏移，结果只写日志、不影响显示。
        /// </summary>
        private static void DumpCandidates()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                for (uint offset = 0x5800; offset <= 0x5FFF; offset += 4)
                {
                    if (!_device.TryExecute("ioctl_read_dword", new long[] { offset }, 1, out long[] output) || output.Length == 0)
                        continue;

                    uint value = (uint)(output[0] & 0xFFFFFFFF);
                    if ((value & 0x80000000u) == 0)
                        continue;

                    int degreesBelowTjMax = (int)((value >> 16) & 0xFF);
                    float temp = (int)_tjMax - degreesBelowTjMax;
                    if (temp < 10f || temp > 120f)
                        continue;

                    sb.Append("0x").Append(offset.ToString("X")).Append("=0x").Append(value.ToString("X8"))
                      .Append("(").Append(temp.ToString("0")).Append("C) ");
                }

                Log("candidate scan: " + (sb.Length > 0 ? sb.ToString() : "none"));
            }
            catch
            {
            }
        }

        private static float? ReadOnce(out uint raw, out string? error)
        {
            raw = 0;
            error = null;
            if (_device == null)
            {
                error = "device not open";
                return null;
            }

            if (!_device.TryExecute("ioctl_read_dword", new long[] { MCHBAR_THERM_STATUS_GT }, 1, out long[] output) ||
                output.Length == 0)
            {
                error = "ioctl_read_dword(0x" + MCHBAR_THERM_STATUS_GT.ToString("X") + ") failed";
                return null;
            }

            raw = (uint)(output[0] & 0xFFFFFFFF);

            // THERM_STATUS_GT: bit31 = valid, bit23:16 = degrees below TjMax.
            if ((raw & 0x80000000u) == 0)
            {
                error = "valid bit not set";
                return null;
            }

            int degreesBelowTjMax = (int)((raw >> 16) & 0xFF);
            float temp = (int)_tjMax - degreesBelowTjMax;

            if (temp < -20f || temp > 130f || degreesBelowTjMax > 127)
            {
                error = "implausible value (deg=" + degreesBelowTjMax + ", temp=" + temp.ToString("0.0") + ")";
                return null;
            }

            return temp;
        }

        private static void Log(string message)
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "GpuTemp.log");
                FileInfo info = new FileInfo(path);
                if (info.Exists && info.Length > LOG_MAX_BYTES)
                    File.WriteAllText(path, "");

                File.AppendAllText(path, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine);
            }
            catch
            {
                // 日志失败不影响监控
            }
        }
    }
}
