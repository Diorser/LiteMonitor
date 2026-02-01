using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using System.Windows.Forms;
using System.Net.Security;
using LiteMonitor.src.Core;

using System.IO;
using System.IO.Compression;

namespace LiteMonitor
{
    /// <summary>
    /// LiteMonitor 自动更新模块（最终完整版）
    /// - version.json 支持国内 / GitHub 两源自动 fallback
    /// - ZIP 下载支持两源测速自动选择最快
    /// - ZIP 下载完成后，主程序抢先更新 Updater.exe，防止自更新死锁
    /// - CheckAsync() 可被右键菜单直接调用
    /// </summary>
    public static class UpdateChecker
    {
        // 全局 HttpClient（降低系统资源消耗）
        private static readonly HttpClient http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2), // 保持连接复用
            SslOptions = new SslClientAuthenticationOptions
            {
                // 强制信任所有证书（解决用户证书报错问题）
                RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            }
        })
        {
            Timeout = TimeSpan.FromSeconds(6) // 适当放宽超时时间，避免网络波动导致失败
        };

        // ========================================================
        // 【1】两个 version.json 源（自动 fallback）
        // ========================================================
        private static readonly string[] VersionJsonUrls =
        {
             // 国官网源
            "https://litemonitor.cn/update/version.json",
            
            // Gitee RAW（自动 fallback 使用）
             "https://gitee.com/Diorser/LiteMonitor/raw/master/resources/version.json",

            // GitHub RAW（自动 fallback 使用）
             "https://raw.githubusercontent.com/Diorser/LiteMonitor/master/resources/version.json",
             
        };

        // ========================================================
        // 【2】两个 ZIP 下载镜像（测速自动选择最快）
        // ========================================================
        private static readonly string[] Mirrors =
        {
            
            // Gitee Releases
            "https://gitee.com/Diorser/LiteMonitor/releases/download/v{0}/LiteMonitor_v{0}-win-x64.zip",
            // 国内 CDN
            "https://litemonitor.cn/update/LiteMonitor_v{0}-win-x64.zip",
            // Github Releases
            "https://github.com/Diorser/LiteMonitor/releases/download/v{0}/LiteMonitor_v{0}-win-x64.zip",

            
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
                // ---- 获取版本信息（自动 fallback）----
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

                string latest = info.Value.latest;
                string changelog = info.Value.changelog;
                string releaseDate = info.Value.releaseDate;
                string current = GetCurrentVersion();

                if (new Version(latest) > new Version(current))
                {
                    // 记录发现新版本
                    LatestVersionInfo = info;

                    // ★★★ [新增逻辑] 预检查设置：是否开启自动更新 ★★★
                    // 如果不是手动触发 (showMessage=false) 且未开启自动检查，则只记录不弹窗
                    var settings = Settings.Load();
                    if (!showMessage && !settings.AutoCheckUpdate) return;

                    // ---- 获取排序后的下载源 (最快优先) ----
                    var sortedUrls = await GetSortedZipUrls(latest);

                    // ---- 加载设置并弹出更新窗口 ----
                    bool isZh = settings?.Language?.ToLower() == "zh";

                    var context = new DownloadContext
                    {
                        Title = isZh ? "发现新版本！" : "New Version!",
                        VersionLabel = $"⚡️LiteMonitor_v{latest}",
                        Description = $"更新日志：\n{changelog} \n更新日期：\n{releaseDate}\n\n官网：https://litemonitor.cn \nGitHub：https://github.com/Diorser/LiteMonitor",
                        Urls = sortedUrls.ToArray(),
                        SavePath = Path.Combine(AppContext.BaseDirectory, "resources", "update.zip"),
                        ActionButtonText = "Update",
                        AutoExitOnSuccess = true
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


        // ========================================================
        // 【4】version.json 自动 fallback
        // ========================================================
       private static async Task<(string latest, string changelog, string releaseDate)?> GetVersionInfo()
        {
            foreach (var url in VersionJsonUrls)
            {
                try
                {
                    using var cts = new CancellationTokenSource();
                    cts.CancelAfter(3000); // 最大等待 3 秒（连接+读取全部）

                    // 构造真正带连接超时的 HttpRequest
                    var request = new HttpRequestMessage(HttpMethod.Get, url);

                    var task = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                    // 这里我们用 WhenAny 避免 HttpClient 自身的内部卡顿
                    var finished = await Task.WhenAny(task, Task.Delay(3000, cts.Token));

                    if (finished != task)
                        throw new TimeoutException("Connection timeout");

                    var resp = await task;

                    if (!resp.IsSuccessStatusCode)
                        throw new Exception("Bad status");

                    string json = await resp.Content.ReadAsStringAsync(cts.Token);

                    var doc = JsonDocument.Parse(json);

                    // [Fix] 移除空抑制操作符，使用空合并运算符提供默认值
                    string latest = doc.RootElement.GetProperty("version").GetString() ?? "";
                    string log = doc.RootElement.GetProperty("changelog").GetString() ?? "";
                    string releaseDate = doc.RootElement.GetProperty("releaseDate").GetString() ?? "";
                    //string downloadUrl = doc.RootElement.GetProperty("downloadUrl").GetString()!;
                    
                    // [Fix] 验证必需字段不为空
                    if (string.IsNullOrEmpty(latest)) throw new Exception("Missing version field");

                    // ---- 成功，立即返回 ----
                    return (latest, log, releaseDate);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Update] 源失败：{url} -> {ex.Message}");
                    continue; // 换下一个源
                }
            }

            return null; // 两个源都失败
        }



        // ========================================================
        // 【5】测速获取排序后的 ZIP 下载源
        // ========================================================
        private static async Task<List<string>> GetSortedZipUrls(string version)
        {
            var tests = new Task<(string url, long speed)>[Mirrors.Length];

            for (int i = 0; i < Mirrors.Length; i++)
            {
                string url = string.Format(Mirrors[i], version);
                tests[i] = TestMirrorSpeed(url);
            }

            var results = await Task.WhenAll(tests);

            // 速度降序排序 (Speed > 0 的优先)
            var sorted = results
                .OrderByDescending(r => r.speed)
                .Select(r => r.url)
                .ToList();

            // 如果所有源都失败(speed=0)，至少保留默认顺序的 URL 以供重试
            if (sorted.Count == 0 || results.All(r => r.speed == 0))
            {
                 return Mirrors.Select(m => string.Format(m, version)).ToList();
            }

            return sorted;
        }


        // ========================================================
        // 【6】轻量测速（读取 32KB 换算下载速度）
        // ========================================================
        private static async Task<(string url, long speed)> TestMirrorSpeed(string url)
        {
            try
            {
                var sw = Stopwatch.StartNew();

                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode)
                    return (url, 0);

                using var stream = await resp.Content.ReadAsStreamAsync();

                byte[] testBuf = new byte[32 * 1024];
                int read = await stream.ReadAsync(testBuf, 0, testBuf.Length);

                sw.Stop();

                if (read <= 0)
                    return (url, 0);

                // Bytes per second
                long speed = (long)(read * 1000.0 / Math.Max(sw.ElapsedMilliseconds, 1));

                return (url, speed);
            }
            catch
            {
                return (url, 0);
            }
        }


        // ========================================================
        // 【7】获取当前版本号
        // ========================================================
                // ========================================================
        // 【7】获取当前版本号 (修复版)
        // ========================================================
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
