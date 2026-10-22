using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace LiteMonitor.src.SystemServices
{
    internal sealed class EcFanReader : IDisposable
    {
        private const string DevicePath = @"\\.\ACPIDriver";
        private const uint IoctlReadEc = 0x9C40A488;
        private const uint GenericReadWrite = 0xC0000000;
        private const uint FileShareReadWrite = 0x00000003;
        private const uint OpenExisting = 3;

        private static readonly TimeSpan CacheDuration = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

        private readonly object _lock = new();
        private readonly bool _isSupportedDevice;
        private SafeFileHandle? _handle;
        private DateTime _lastRead = DateTime.MinValue;
        private DateTime _nextRetry = DateTime.MinValue;
        private FanSnapshot _snapshot;

        public EcFanReader()
        {
            _isSupportedDevice = IsSupportedDevice();
        }

        public bool TryGetRpm(string key, out float rpm)
        {
            rpm = 0f;
            if (!_isSupportedDevice) return false;
            if (!TryReadSnapshot(out var snapshot)) return false;

            if (key == "CPU.Fan")
            {
                rpm = snapshot.CpuFanRpm;
                return true;
            }

            if (key == "GPU.Fan")
            {
                rpm = snapshot.GpuFanRpm < 100 ? 0 : snapshot.GpuFanRpm;
                return true;
            }

            return false;
        }

        private bool TryReadSnapshot(out FanSnapshot snapshot)
        {
            lock (_lock)
            {
                var now = DateTime.Now;
                if (_snapshot.IsValid && now - _lastRead < CacheDuration)
                {
                    snapshot = _snapshot;
                    return true;
                }

                if (now < _nextRetry)
                {
                    snapshot = default;
                    return false;
                }

                try
                {
                    EnsureOpen();

                    int cpuHi = Read8(0x0464);
                    int cpuLo = Read8(0x0465);
                    int gpuLo = Read8(0x046B);
                    int gpuHi = Read8(0x046C);

                    _snapshot = new FanSnapshot
                    {
                        CpuFanRpm = (cpuHi << 8) | cpuLo,
                        GpuFanRpm = (gpuHi << 8) | gpuLo,
                        IsValid = true
                    };
                    _lastRead = now;

                    snapshot = _snapshot;
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[EC Fan] Read failed: {ex.Message}");
                    CloseHandle();
                    _nextRetry = now + RetryDelay;
                    snapshot = default;
                    return false;
                }
            }
        }

        private void EnsureOpen()
        {
            if (_handle != null && !_handle.IsInvalid && !_handle.IsClosed) return;

            _handle = CreateFileW(
                DevicePath,
                GenericReadWrite,
                FileShareReadWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);

            if (_handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                CloseHandle();
                throw new InvalidOperationException($"CreateFileW({DevicePath}) failed, Win32={error}");
            }
        }

        private int Read8(int address)
        {
            uint input = (uint)address;
            uint output = 0;
            uint returned;

            bool ok = DeviceIoControl(
                _handle!,
                IoctlReadEc,
                ref input,
                sizeof(uint),
                ref output,
                sizeof(uint),
                out returned,
                IntPtr.Zero);

            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"ReadEC 0x{address:X4} failed, Win32={error}");
            }

            return (int)(output & 0xFF);
        }

        private static bool IsSupportedDevice()
        {
            try
            {
                using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                string manufacturer = (bios?.GetValue("SystemManufacturer") as string ?? "").ToUpperInvariant();
                string product = (bios?.GetValue("SystemProductName") as string ?? "").ToUpperInvariant();
                string boardManufacturer = (bios?.GetValue("BaseBoardManufacturer") as string ?? "").ToUpperInvariant();
                string boardProduct = (bios?.GetValue("BaseBoardProduct") as string ?? "").ToUpperInvariant();
                string combined = $"{manufacturer} {product} {boardManufacturer} {boardProduct}";

                bool isMechrevo = combined.Contains("MECHREVO") || combined.Contains("MECHNEVO");
                bool isYilong15 = combined.Contains("YILONG") || combined.Contains("GM5HG7A");
                return isMechrevo && isYilong15;
            }
            catch
            {
                return false;
            }
        }

        private void CloseHandle()
        {
            _handle?.Dispose();
            _handle = null;
            _snapshot = default;
            _lastRead = DateTime.MinValue;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                CloseHandle();
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            ref uint lpInBuffer,
            int nInBufferSize,
            ref uint lpOutBuffer,
            int nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        private struct FanSnapshot
        {
            public int CpuFanRpm;
            public int GpuFanRpm;
            public bool IsValid;
        }
    }
}
