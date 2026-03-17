using System;
using System.Runtime.InteropServices;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// MTML (Moore Threads Management Library) P/Invoke 封装
    /// 用于获取摩尔线程显卡的监控数据
    /// </summary>
    internal static class MtmlNative
    {
        private const string DLL_NAME = "mtml.dll";

        #region Return Codes
        public const int MTML_SUCCESS = 0;
        public const int MTML_ERROR_DRIVER_NOT_LOADED = 1;
        public const int MTML_ERROR_DRIVER_FAILURE = 2;
        public const int MTML_ERROR_INVALID_ARGUMENT = 3;
        public const int MTML_ERROR_NOT_SUPPORTED = 4;
        public const int MTML_ERROR_NO_PERMISSION = 5;
        public const int MTML_ERROR_INSUFFICIENT_SIZE = 6;
        public const int MTML_ERROR_NOT_FOUND = 7;
        #endregion

        #region Buffer Sizes
        public const int MTML_DEVICE_NAME_BUFFER_SIZE = 32;
        public const int MTML_DEVICE_UUID_BUFFER_SIZE = 48;
        #endregion

        #region Opaque Handles
        // 不透明句柄使用 IntPtr
        // MtmlLibrary, MtmlSystem, MtmlDevice, MtmlGpu, MtmlMemory, MtmlVpu
        #endregion

        #region Library Functions
        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlLibraryInit(out IntPtr lib);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlLibraryShutDown(IntPtr lib);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlLibraryCountDevice(IntPtr lib, out uint count);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlLibraryInitDeviceByIndex(IntPtr lib, uint index, out IntPtr dev);
        #endregion

        #region Device Functions
        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlDeviceGetName(IntPtr dev, byte[] name, uint length);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlDeviceGetUUID(IntPtr dev, byte[] uuid, uint length);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlDeviceGetPowerUsage(IntPtr dev, out uint power);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlDeviceGetFanRpm(IntPtr dev, uint fanIndex, out uint fanRpm);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlDeviceCountFan(IntPtr dev, out uint count);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlDeviceInitGpu(IntPtr dev, out IntPtr gpu);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlDeviceInitMemory(IntPtr dev, out IntPtr mem);
        #endregion

        #region GPU Functions
        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlGpuGetUtilization(IntPtr gpu, out uint utilization);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlGpuGetTemperature(IntPtr gpu, out int temp);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlGpuGetClock(IntPtr gpu, out uint clockMhz);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlGpuGetMaxClock(IntPtr gpu, out uint clockMhz);
        #endregion

        #region Memory Functions
        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlMemoryGetTotal(IntPtr mem, out ulong total);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlMemoryGetUsed(IntPtr mem, out ulong used);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlMemoryGetUtilization(IntPtr mem, out uint utilization);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mtmlMemoryGetClock(IntPtr mem, out uint clockMhz);
        #endregion

        #region Helper Methods
        /// <summary>
        /// 检查 MTML 库是否可用
        /// </summary>
        public static bool IsAvailable()
        {
            try
            {
                int result = mtmlLibraryInit(out IntPtr lib);
                if (result == MTML_SUCCESS && lib != IntPtr.Zero)
                {
                    mtmlLibraryShutDown(lib);
                    return true;
                }
            }
            catch { }
            return false;
        }
        #endregion
    }
}
