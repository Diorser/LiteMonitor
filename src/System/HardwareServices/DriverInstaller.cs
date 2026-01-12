using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Security; // 引用 SslClientAuthenticationOptions
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using Debug = System.Diagnostics.Debug;

namespace LiteMonitor.src.SystemServices
{
    public class DriverInstaller
    {
        private readonly Settings _cfg;
        private readonly Computer _computer;
        private readonly Action _onReloadRequired; // 回调：通知主程序重载

        // 建议把最快的 Gitee/国内源放在第一个
        private readonly string[] _driverUrls = new[]
        {
            "https://gitee.com/Diorser/LiteMonitor/raw/master/resources/assets/PawnIO_setup.exe",
            "https://litemonitor.cn/update/PawnIO_setup.exe", 
            "https://github.com/Diorser/LiteMonitor/raw/master/resources/assets/PawnIO_setup.exe" 
        };

        // ★★★ [新增] PresentMon 下载源 ★★★
        private readonly string[] _presentMonUrls = new[]
        {
            "https://gitee.com/Diorser/LiteMonitor/raw/master/resources/assets/LiteMonitorFPS.exe",
            "https://litemonitor.cn/update/LiteMonitorFPS.exe",
            "https://github.com/Diorser/LiteMonitor/raw/master/resources/assets/LiteMonitorFPS.exe"
        };

        private const string ManualDownloadPage = "https://gitee.com/Diorser/LiteMonitor/raw/master/resources/assets/PawnIO_setup.exe";

        public DriverInstaller(Settings cfg, Computer computer, Action onReloadRequired)
        {
            _cfg = cfg;
            _computer = computer;
            _onReloadRequired = onReloadRequired;
        }

        // ================================================================
        // ★★★ 公共入口 1：PawnIO 驱动检查 (启动时调用) ★★★
        // ================================================================
        public async Task SmartCheckDriver()
        {
            // 顺便启动 PresentMon 的静默检查 (Fire and forget)
            // 这样主程序启动时就会在后台下载，不阻塞界面
            _ = CheckAndDownloadPresentMon(silent: true);

            if (!_cfg.IsAnyEnabled("CPU")) return;

            bool isDriverInstalled = IsPawnIOInstalled();
            var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            bool isCpuValid = cpu != null && cpu.Sensors.Length > 0;

            if (!isDriverInstalled || !isCpuValid)
            {
                if (!isDriverInstalled)
                {
                    Debug.WriteLine("[Driver] Driver missing. Attempting silent install...");
                    bool installed = await SilentInstallPawnIO();
                    
                    if (installed)
                    {
                        Debug.WriteLine("[Driver] Installed. Reloading...");
                        _onReloadRequired?.Invoke();
                    }
                }
            }
        }

        // ================================================================
        // ★★★ 公共入口 2：PresentMon 检查 (FPS功能调用/启动调用) ★★★
        // ================================================================
        /// <summary>
        /// 检查 PresentMon 是否存在，不存在则自动下载。
        /// <para>可以在 FPS 读取逻辑初始化前调用此方法。</para>
        /// </summary>
        /// <returns>文件是否准备就绪 (true=存在或下载成功)</returns>
        public async Task<bool> CheckAndDownloadPresentMon(bool silent = false)
        {
            string targetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources");
            string targetPath = Path.Combine(targetDir, "LiteMonitorFPS.exe");

            // 1. 如果文件已存在，直接返回 true
            if (File.Exists(targetPath)) return true;

            // 2. 不存在，尝试下载
            Debug.WriteLine("[PresentMon] Missing. Downloading...");
            bool success = await DownloadFileFromMirrors(_presentMonUrls, targetPath);

            if (success)
            {
                Debug.WriteLine("[PresentMon] Download success.");
                return true;
            }
            else
            {
                Debug.WriteLine("[PresentMon] Download failed.");
                // 如果不是静默模式（比如用户点击开启FPS时），可以考虑弹窗提示，或者由上层逻辑决定是否弹窗
                // 这里暂时保持静默，只返回 false
                return false;
            }
        }

        private bool IsPawnIOInstalled()
        {
            try
            {
                string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
                using var k1 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(keyPath);
                if (k1 != null) return true;
                using var k2 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(keyPath);
                if (k2 != null) return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// PawnIO 专用安装逻辑 (调用通用下载 + 安装进程)
        /// </summary>
        private async Task<bool> SilentInstallPawnIO()
        {
            // 使用 GUID 生成随机文件名
            string tempFileName = $"PawnIO_{Guid.NewGuid()}.exe";
            string tempPath = Path.Combine(Path.GetTempPath(), tempFileName);
            
            try 
            {
                // 1. 调用通用下载逻辑
                bool downloadSuccess = await DownloadFileFromMirrors(_driverUrls, tempPath);

                if (!downloadSuccess)
                {
                    ShowManualFailDialog("下载超时或连接失败，请检查网络。");
                    return false;
                }

                // 2. 安装逻辑
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = tempPath,
                        Arguments = "-install -silent",
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        await proc.WaitForExitAsync();
                        return proc.ExitCode == 0;
                    }
                }
                catch { } // UAC 取消
                
                ShowManualFailDialog("自动安装被取消或拦截。");
                return false;
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        /// <summary>
        /// ★★★ [核心重构] 通用多源下载逻辑 ★★★
        /// </summary>
        private async Task<bool> DownloadFileFromMirrors(string[] urls, string savePath)
        {
            // 确保目标目录存在 (针对 PresentMon 存放在 resources/assets 的情况)
            try
            {
                string? dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            catch { return false; }

            // 使用 SocketsHttpHandler 来控制 SSL 选项，忽略证书错误
            using (var handler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
                }
            })
            using (var client = new HttpClient(handler))
            {
                client.DefaultRequestHeaders.Add("User-Agent", "LiteMonitor-AutoUpdater");

                foreach (var url in urls)
                {
                    if (string.IsNullOrWhiteSpace(url)) continue;

                    try
                    {
                        Debug.WriteLine($"[Downloader] Trying: {url}");

                        // 设置 15秒 超时
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                        {
                            var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                            
                            if (response.IsSuccessStatusCode)
                            {
                                var data = await response.Content.ReadAsByteArrayAsync(cts.Token);
                                
                                // 写入文件
                                await File.WriteAllBytesAsync(savePath, data, cts.Token);

                                // 简单校验：文件大于 1KB 认为成功
                                if (new FileInfo(savePath).Length > 1024*300)
                                {
                                    return true; // 下载成功，直接返回
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Downloader] Error: {url} -> {ex.Message}");
                    }
                }
            }

            return false; // 所有源都失败
        }

        private void ShowManualFailDialog(string reason)
        {
            // 确保在 UI 线程弹窗
            if (Application.OpenForms.Count > 0)
            {
                Application.OpenForms[0]?.Invoke(new Action(() => 
                {
                    DoShowDialog(reason);
                }));
            }
            else
            {
                DoShowDialog(reason);
            }
        }

        private void DoShowDialog(string reason)
        {
             var result = MessageBox.Show(
                $"PawnIO驱动缺失！\n\nLiteMonitor 无法自动配置 CPU 所需的PawnIO驱动\n将无法读取部分CPU数据！\n原因：{reason}\n\n点击“确定”手动下载安装。",
                "LiteMonitor",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);

            if (result == DialogResult.OK)
            {
                try { Process.Start(new ProcessStartInfo(ManualDownloadPage) { UseShellExecute = true }); } catch { }
            }
        }
    }
}