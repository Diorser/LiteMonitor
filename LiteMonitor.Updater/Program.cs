using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text; 
using System.Threading;

namespace LiteMonitor.Updater
{
    internal class Program
    {
        private const string ExeName = "LiteMonitor.exe";

        static void Main(string[] args)
        {
            // ★★★ [基础] 注册编码支持 (为智能识别做准备) ★★★
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            if (args.Length == 0) return;

            string zipFile = args[0];
            string resourcesDir = AppContext.BaseDirectory;

            // ===========================================================
            // 1. 智能定位主程序目录
            // ===========================================================
            string? baseDir = GetMainProgramDirectory(resourcesDir);

            if (baseDir == null)
            {
                LogError(resourcesDir, "[Fatal] 找不到 LiteMonitor.exe，更新终止！");
                return;
            }

            // ===========================================================
            // 2. 等待主程序退出 (带缓冲)
            // ===========================================================
            string procName = Path.GetFileNameWithoutExtension(ExeName);
            WaitExit(procName);
            
            // 给系统 1秒 缓冲时间，确保文件句柄彻底释放
            Thread.Sleep(1000); 

            // ===========================================================
            // 3. 解压到 LiteMonitor/_update_tmp 目录
            // ===========================================================
            string tempDir = Path.Combine(baseDir, "_update_tmp");

            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);

                // ★★★ [核心修复] 智能识别编码解压 ★★★
                // 自动判断是用 UTF-8 还是 GBK，杜绝乱码
                ExtractZipSmart(zipFile, tempDir);
            }
            catch (Exception ex)
            {
                LogError(baseDir, "解压失败： " + ex.Message);
                return;
            }

            // ===========================================================
            // 4. 处理 ZIP 的最外层目录
            // ===========================================================
            string realFolder = ResolveZipRoot(tempDir);

            // ===========================================================
            // 5. 覆盖更新文件 (带重试机制)
            // ===========================================================
            try
            {
                foreach (string srcPath in Directory.GetFiles(realFolder, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(realFolder, srcPath);
                    string destPath = Path.Combine(baseDir, rel);

                    // ★★★ [恢复] Updater 跳过自我更新 ★★★
                    // 逻辑：主程序 (PreUpdateUpdater) 已经在 Updater 启动前完成了 Updater 的更新。
                    // 因此，Updater 运行时，它自己已经是最新版，无需再次覆盖。
                    // 直接跳过，避免“文件正在使用”错误。
                    // 兼容旧版 Updater.exe 和新版 LiteMonitor.Updater.exe
                    if (rel.EndsWith("Updater.exe", StringComparison.OrdinalIgnoreCase) || 
                        rel.EndsWith("LiteMonitor.Updater.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue; 
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

                    // 使用带重试机制的复制
                    if (!TryCopyFile(srcPath, destPath))
                    {
                        LogError(baseDir, $"无法覆盖文件 (被占用): {rel}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogError(baseDir, "复制更新文件失败：" + ex.Message);
            }

            // ===========================================================
            // 6. 清理临时目录 & zip
            // ===========================================================
            try { Directory.Delete(tempDir, true); } catch { }
            try { File.Delete(zipFile); } catch { }

            // ===========================================================
            // 7. 重启 LiteMonitor
            // ===========================================================
            RestartMain(baseDir);
        }

        // ======================================================
        // ★★★ [核心方法] 智能解压 (自动兼容 UTF-8 和 GBK) ★★★
        // ======================================================
        private static void ExtractZipSmart(string zipPath, string extractTo)
        {
            // 默认假设是标准 UTF-8
            bool useGbk = false;

            try 
            {
                // 1. 试探性打开：.NET 默认使用 UTF-8 解析
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        // 检查文件名中是否有“未知字符”(Replacement Character )
                        // 如果有，说明 UTF-8 解析失败，这肯定是一个 GBK 编码的旧版压缩包
                        if (entry.FullName.Contains('\uFFFD'))
                        {
                            useGbk = true;
                            break;
                        }
                    }
                }
            }
            catch
            {
                // 如果连头都读不出来，保险起见也尝试 GBK
                useGbk = true;
            }

            // 2. 执行真正的解压
            if (useGbk)
            {
                // 使用 GBK 解压 (解决旧版软件压缩包乱码)
                var gbk = Encoding.GetEncoding("GBK");
                ZipFile.ExtractToDirectory(zipPath, extractTo, gbk, true);
            }
            else
            {
                // 使用默认 UTF-8 解压 (解决 GitHub/新版压缩包乱码)
                ZipFile.ExtractToDirectory(zipPath, extractTo, true);
            }
        }

        // ------------------ 辅助方法 (重试机制) ------------------

        private static bool TryCopyFile(string src, string dest)
        {
            // 最多重试 10 次，每次间隔 500ms (总共等待 5秒)
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    // 简单粗暴：直接覆盖
                    File.Copy(src, dest, true);
                    return true; 
                }
                catch (IOException) // 文件被占用
                {
                    if (i == 9) return false; 
                    Thread.Sleep(500); 
                }
                catch (UnauthorizedAccessException) // 权限不足
                {
                    if (i == 9) return false;
                    Thread.Sleep(500);
                }
            }
            return false;
        }

        private static bool ContainsLiteMonitorExe(string dir)
        {
            return Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly)
                            .Any(f => Path.GetFileName(f)
                                .Equals(ExeName, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetPathHash(string path)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(path.ToLowerInvariant());
                byte[] hashBytes = md5.ComputeHash(inputBytes);
                return BitConverter.ToString(hashBytes).Replace("-", "");
            }
        }

        // ------------------ 自动检测主程序目录 ------------------
        private static string? GetMainProgramDirectory(string resourcesDir)
        {
            // 先检查 resourcesDir 的上级目录
            DirectoryInfo? current = new DirectoryInfo(resourcesDir).Parent;

            if (current != null && ContainsLiteMonitorExe(current.FullName))
                return current.FullName;

            // 再检查当前目录（便携版）
            if (ContainsLiteMonitorExe(resourcesDir))
                return resourcesDir;

            return null;
        }

        // ------------------ 处理 Zip 最外层目录 ------------------
        private static string ResolveZipRoot(string tempDir)
        {
            var entries = Directory.GetFileSystemEntries(tempDir);
            if (entries.Length == 1 && Directory.Exists(entries[0]))
                return entries[0];
            return tempDir;
        }
        //重启主程序
        private static void RestartMain(string baseDir)
        {
            // ★★★ [新增] 创建更新成功标志文件 ★★★
            try 
            {
                string tokenPath = Path.Combine(baseDir, "update_success");
                File.Create(tokenPath).Close(); // 创建并立即关闭释放句柄
            }
            catch { /* 忽略无法创建标志的错误，不影响启动 */ }

            // 原有启动逻辑
            string exePath = Path.Combine(baseDir, ExeName);

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private static void WaitExit(string name)
        {
            // 阶段 1: 礼貌等待 (3秒)
            // 大多数情况下，主程序在启动 Updater 后会立即调用 Application.Exit()
            for (int i = 0; i < 15; i++)
            {
                var processes = Process.GetProcessesByName(name);
                if (processes.Length == 0) return;
                Thread.Sleep(200);
            }

            // 阶段 2: 请求关闭 (尝试 CloseMainWindow)
            try 
            {
                var processes = Process.GetProcessesByName(name);
                foreach (var p in processes)
                {
                    if (!p.HasExited) p.CloseMainWindow();
                }
            }
            catch { }

            // 阶段 3: 最终强制清理 (如果还不行，只能 Kill)
            // 等待最多 5 秒
            for (int i = 0; i < 25; i++)
            {
                var processes = Process.GetProcessesByName(name);
                if (processes.Length == 0) return;
                
                // 只有在超时严重时才尝试 Kill，且增加间隔
                if (i > 10) 
                {
                    foreach (var p in processes) 
                    {
                        try { if (!p.HasExited) p.Kill(); } catch { }
                    }
                }
                Thread.Sleep(200);
            }

            // ★★★ [Fix BF6] 硬杀主程序后清理残留的 FPS 采集组件与 ETW 会话：
            // Process.Kill 不会执行主程序的 finally，LiteMonitorFPS.exe（独立子进程）会继续
            // 持有名为 LiteMonitor_Golden_Session 的系统级 ETW 会话，该会话追踪受反作弊
            // 保护的游戏（如战地6）会导致游戏被反作弊静默闪退 ★★★
            CleanupFpsResidue();
        }

        /// <summary>
        /// [Fix BF6] 清理残留的 LiteMonitorFPS 进程与 ETW 追踪会话
        /// 注意：会话名必须与主程序 FpsCounter.SESSION_NAME 保持一致
        /// </summary>
        private static void CleanupFpsResidue()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("LiteMonitorFPS"))
                {
                    try { if (!p.HasExited) p.Kill(); } catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "logman",
                    Arguments = "stop LiteMonitor_Golden_Session -ets",
                    UseShellExecute = false,
                    CreateNoWindow = true
                })?.WaitForExit(100);
            }
            catch { }
        }

        private static void LogError(string dir, string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(dir, "update_error.log"),
                    DateTime.Now + " " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}