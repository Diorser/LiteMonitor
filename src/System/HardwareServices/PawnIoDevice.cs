using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 极简 PawnIO 客户端：直接通过 \\.\GLOBALROOT\Device\PawnIO 设备加载并执行已签名模块。
    /// 重要：PawnIO 驱动每个设备句柄只允许加载一个模块（第二个模块会返回 STATUS_ALREADY_INITIALIZED），
    /// 因此每个模块必须使用独立的 PawnIoDevice 实例。
    /// 仅在管理员权限下可用；失败时静默返回 false，不影响其他功能。
    /// </summary>
    internal sealed class PawnIoDevice : IDisposable
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        private const uint DEVICE_TYPE = 41394u << 16;
        private const int FN_NAME_LENGTH = 32;
        private const uint IOCTL_PIO_LOAD_BINARY = 0x821u << 2;
        private const uint IOCTL_PIO_EXECUTE_FN = 0x841u << 2;
        private const string DEVICE_PATH = @"\\?\GLOBALROOT\Device\PawnIO";

        private SafeFileHandle? _handle;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
        private static extern bool DeviceIoControlNoOut(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            byte[] lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
        private static extern bool DeviceIoControlOut(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            byte[] lpInBuffer,
            uint nInBufferSize,
            byte[] lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        public static PawnIoDevice? TryOpen(out string error)
        {
            error = "";
            try
            {
                var handle = CreateFileW(DEVICE_PATH,
                                         GENERIC_READ | GENERIC_WRITE,
                                         FILE_SHARE_READ | FILE_SHARE_WRITE,
                                         IntPtr.Zero,
                                         OPEN_EXISTING,
                                         FILE_ATTRIBUTE_NORMAL,
                                         IntPtr.Zero);
                if (handle == null || handle.IsInvalid)
                {
                    int code = Marshal.GetLastWin32Error();
                    error = "CreateFile(PawnIO) failed, Win32=" + code;
                    return null;
                }
                return new PawnIoDevice { _handle = handle };
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        public bool TryLoadModule(byte[] binary, out int win32Error)
        {
            win32Error = 0;
            if (_handle == null || _handle.IsInvalid || binary == null || binary.Length == 0)
            {
                win32Error = -1;
                return false;
            }

            try
            {
                bool ok = DeviceIoControlNoOut(_handle,
                                               DEVICE_TYPE | IOCTL_PIO_LOAD_BINARY,
                                               binary,
                                               (uint)binary.Length,
                                               IntPtr.Zero,
                                               0,
                                               out _,
                                               IntPtr.Zero);
                if (!ok)
                    win32Error = Marshal.GetLastWin32Error();
                return ok;
            }
            catch
            {
                return false;
            }
        }

        public bool TryExecute(string function, long[] input, int outputLength, out long[] output)
        {
            output = Array.Empty<long>();
            if (_handle == null || _handle.IsInvalid || string.IsNullOrEmpty(function) || outputLength <= 0)
                return false;

            try
            {
                byte[] inBuffer = new byte[FN_NAME_LENGTH + input.Length * sizeof(long)];
                byte[] name = Encoding.ASCII.GetBytes(function);
                Buffer.BlockCopy(name, 0, inBuffer, 0, Math.Min(name.Length, FN_NAME_LENGTH - 1));
                Buffer.BlockCopy(input, 0, inBuffer, FN_NAME_LENGTH, input.Length * sizeof(long));

                byte[] outBuffer = new byte[outputLength * sizeof(long)];
                if (!DeviceIoControlOut(_handle,
                                        DEVICE_TYPE | IOCTL_PIO_EXECUTE_FN,
                                        inBuffer,
                                        (uint)inBuffer.Length,
                                        outBuffer,
                                        (uint)outBuffer.Length,
                                        out uint bytesReturned,
                                        IntPtr.Zero))
                {
                    return false;
                }

                int count = (int)(bytesReturned / sizeof(long));
                if (count <= 0)
                    return false;

                output = new long[count];
                Buffer.BlockCopy(outBuffer, 0, output, 0, count * sizeof(long));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            try { _handle?.Dispose(); } catch { }
            _handle = null;
        }
    }
}
