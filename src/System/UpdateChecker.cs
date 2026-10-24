using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor.src.Core;

using System.IO;
using System.IO.Compression;

namespace LiteMonitor
{
    /// <summary>
    /// LiteMonitor 自动更新模块
    /// - 从当前仓库的最新正式 Release 读取更新清单
    /// - ZIP 通过发布清单的 SHA256 校验后再执行更新
    /// - ZIP 下载完成后，主程序抢先更新 Updater.exe，防止自更新死锁
    /// - CheckAsync() 可被右键菜单直接调用
    /// </summary>
    public static class UpdateChecker
    {
        // 全局 HttpClient（降低系统资源消耗）
        private static readonly HttpClient http = new HttpClient(new SocketsHttpHandler
        {
            // 更新清单必须通过系统证书校验，防止下载地址被伪造。
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        /// <summary>
        /// 缓存最新版本信息，供菜单等处使用
        /// </summary>
        public static (string latest, string changelog, string releaseDate)? LatestVersionInfo { get; private set; }

        /// <summary>
        /// 是否发现了新版本
        /// </summary>
        public static bool IsUpdateFound => LatestVersionInfo != null;


        // ========================================================
        // 【3】主入口：检查更新
        // ========================================================
        /// <summary>
        /// 检查更新主入口。
        /// showMessage = true 时，在无更新或失败时提示用户。
        /// </summary>
        public static async Task CheckAsync(bool showMessage = false)
        {
            try
            {
                // ---- 获取最新正式发布的版本信息 ----
                var info = await GetVersionInfo();
                if (info == null)
                {
                    if (showMessage)
                    {
                        // 根据当前语言设置显示中文或英文版本
                        if (LanguageManager.CurrentLang == "zh")
                        {
                            MessageBox.Show("无法连接到更新服务器，请稍后重试。",
                                "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        else
                        {
                            MessageBox.Show("Unable to connect to update server, please try again later.",
                                "Update Check", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                    return;
                }

                string latest = info.Version;
                string changelog = info.Changelog;
                string releaseDate = info.ReleaseDate;
                string current = GetCurrentVersion();

                if (new Version(latest) > new Version(current))
                {
                    // 记录发现新版本
                    LatestVersionInfo = (latest, changelog, releaseDate);

                    // ★★★ [新增逻辑] 预检查设置：是否开启自动更新 ★★★
                    // 如果不是手动触发 (showMessage=false) 且未开启自动检查，则只记录不弹窗
                    var settings = Settings.Load();
                    if (!showMessage && !settings.AutoCheckUpdate) return;

                    // ---- 使用同一次发布的下载地址 ----
                    var sortedUrls = new[] { info.DownloadUrl };

                    // ---- 加载设置并弹出更新窗口 ----
                    bool isZh = settings?.Language?.ToLower() == "zh";

                    var context = new DownloadContext
                    {
                        Title = isZh ? "发现新版本！" : "New Version!",
                        VersionLabel = $"⚡️LiteMonitor_v{latest}",
                        Description = $"更新日志：\n{changelog} \n更新日期：\n{releaseDate}\n\nGitHub：https://github.com/{ReleaseManifest.Repository}",
                        Urls = sortedUrls.ToArray(),
                        SavePath = Path.Combine(AppContext.BaseDirectory, "resources", "update.zip"),
                        ActionButtonText = "Update",
                        AutoExitOnSuccess = true,
                        ExpectedSha256 = info.Sha256
                    };

                    new UpdateDialog(context, settings).ShowDialog();
                }
                else
                {
                    // 已是最新版本，清除缓存
                    LatestVersionInfo = null;

                    if (showMessage)
                    {
                        // 根据当前语言设置显示中文或英文版本
                        if (LanguageManager.CurrentLang == "zh")
                        {
                            MessageBox.Show($"当前已是最新版本 ：v{current}\n发布日期：{releaseDate}", "检查更新", 
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            MessageBox.Show($"Already the latest version: v{current}\nRelease date: {releaseDate}", "Update Check", 
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[UpdateChecker] Error: " + ex.Message);
                if (showMessage)
                {
                    // 根据当前语言设置显示中文或英文版本
                    if (LanguageManager.CurrentLang == "zh")
                    {
                        MessageBox.Show("检查更新失败，可能是网络问题。", 
                            "检查更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    else
                    {
                        MessageBox.Show("Update check failed, possibly due to network issues.", 
                            "Update Check Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
        }


        // 更新清单作为 Release 附件，与 ZIP 一起在草稿验证完成后公开。
        private static async Task<ReleaseManifest?> GetVersionInfo()
        {
            string url = $"https://github.com/{ReleaseManifest.Repository}/releases/latest/download/version.json";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("LiteMonitor/" + GetCurrentVersion());
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                response.EnsureSuccessStatusCode();
                return ReleaseManifest.Parse(await response.Content.ReadAsStringAsync(cts.Token), ReleaseManifest.Repository);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Update] 更新清单读取失败：{ex.Message}");
                return null;
            }
        }

        public static string GetCurrentVersion()
        {
            // 优先读取 AssemblyInformationalVersion (对应 csproj 中的 <Version>)
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            // 如果读取失败，回退到 ProductVersion
            if (string.IsNullOrWhiteSpace(version))
                version = Application.ProductVersion;

            // 这里的 version 可能会包含后缀 (如 1.0.7+abcdef)，需要截断
            int plusIndex = version.IndexOf('+');
            if (plusIndex > 0)
                version = version.Substring(0, plusIndex);

            return version;
        }


        // ========================================================
        // 【8】主程序抢先更新 Updater.exe (解决自更新死锁)
        // ========================================================
        /// <summary>
        /// 在启动 Updater 之前，强制从 ZIP 中解压出最新的 Updater.exe 覆盖旧版。
        /// 这样 Updater 运行时就是最新的，无需再尝试更新自己。
        /// </summary>
        /// <param name="zipPath">下载好的 update.zip 路径</param>
        /// <returns>返回成功解压的 Updater 路径，失败则返回 null</returns>
        public static string? PreUpdateUpdater(string zipPath)
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string resourcesDir = Path.Combine(baseDir, "resources");

                // 1. 确保 resources 目录存在
                if (!Directory.Exists(resourcesDir)) Directory.CreateDirectory(resourcesDir);

                // 2. 杀掉所有残留的 Updater 进程 (防止占用)
                // 涵盖新旧两个名字
                string[] updaterNames = { "Updater", "LiteMonitor.Updater" };
                foreach (var name in updaterNames)
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try 
                        {
                            // 优化：检查进程路径是否匹配
                             if (p.MainModule != null && 
                                 p.MainModule.FileName.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                            {
                                p.Kill(); 
                            }
                        } 
                        catch { }
                    }
                }
                
                // 给一点时间释放句柄
                System.Threading.Thread.Sleep(200);

                // 3. 打开 ZIP 查找 Updater
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    // 优先找新版
                    var entry = archive.Entries.FirstOrDefault(e => 
                        e.FullName.EndsWith("LiteMonitor.Updater.exe", StringComparison.OrdinalIgnoreCase));
                    
                    // 没找到则找旧版
                    if (entry == null)
                    {
                        entry = archive.Entries.FirstOrDefault(e => 
                            e.FullName.EndsWith("Updater.exe", StringComparison.OrdinalIgnoreCase));
                    }

                    if (entry != null)
                    {
                        // 确定解压目标路径 (保持文件名一致)
                        string fileName = Path.GetFileName(entry.FullName);
                        string targetPath = Path.Combine(resourcesDir, fileName);

                        // 4. 解压新文件 (覆盖)
                        entry.ExtractToFile(targetPath, true);
                        
                        Debug.WriteLine($"[UpdateChecker] Updater 预更新成功: {targetPath}");
                        
                        return targetPath;
                    }
                }
            }
            catch (Exception ex)
            {
                // 即使失败也不要阻断流程
                Debug.WriteLine($"[UpdateChecker] Updater 预更新失败: {ex.Message}");
            }
            
            return null;
        }
    }
}
