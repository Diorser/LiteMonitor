using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 系统优化器：负责内存清理、性能修剪及后台维护任务
    /// </summary>
    public static class SystemOptimizer
    {
        #region Native Methods
        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);
        #endregion

        /// <summary>
        /// 执行深度内存清理 (针对所有进程)
        /// </summary>
        public static void CleanMemory(Action<int>? onProgress = null)
        {
            // 1. 自身 GC (0-5%)
            onProgress?.Invoke(0);
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            onProgress?.Invoke(5);

            // 2. 全局清理 (5-100%)
            try
            {
                var procs = Process.GetProcesses();
                int total = procs.Length;
                int current = 0;

                if (total == 0) { onProgress?.Invoke(100); return; }

                foreach (var proc in procs)
                {
                    try
                    {
                        using (proc)
                        {
                            if (!proc.HasExited) EmptyWorkingSet(proc.Handle);
                        }
                    }
                    catch 
                    {
                        // 忽略无权限访问的系统进程
                    }
                    finally
                    {
                        current++;
                        int p = 5 + (int)((double)current / total * 95);
                        onProgress?.Invoke(Math.Min(p, 100));
                    }
                }
            }
            catch { }
            onProgress?.Invoke(100);
        }

        /// <summary>
        /// 修剪当前进程的工作集内存
        /// </summary>
        public static void TrimWorkingSet()
        {
            try
            {
                using var proc = Process.GetCurrentProcess();
                EmptyWorkingSet(proc.Handle);
            }
            catch { }
        }

        /// <summary>
        /// 执行定时维护任务 (流量保存、GC 优化、内存修剪)
        /// </summary>
        /// <param name="secondsCounter">系统运行秒数计数器</param>
        public static void RunMaintenanceTasks(long secondsCounter)
        {
            // 1. 流量保存: 每 60 秒 (Offset 5s: 避开整点)
            if (secondsCounter % 60 == 5)
            {
                TrafficLogger.Save();
            }

            // 1b. 应用流量: 每秒计算实时速度，每 60 秒保存 (Offset 15s)
            AppTrafficLogger.Tick();
            if (secondsCounter % 60 == 15)
            {
                AppTrafficLogger.Save();
            }

            // 2. 内存软清理: 每 180 秒 (Offset 30s)
            if (secondsCounter % 180 == 30)
            {
                GC.Collect(2, GCCollectionMode.Optimized);
            }

            // 3. 内存硬清理: 每 300 秒 (5分钟) (Offset 45s)
            if (secondsCounter % 300 == 45)
            {
                try
                {
                    using var proc = Process.GetCurrentProcess();
                    // 阈值 30MB，确保极致轻量
                    if (proc.WorkingSet64 > 30 * 1024 * 1024) 
                    {
                        EmptyWorkingSet(proc.Handle);
                    }
                }
                catch { }
            }
        }
    }
}
