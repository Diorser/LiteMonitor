using System;
using System.Diagnostics;
using LiteMonitor.src.WebServer;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.src.Core.Actions
{
    public static class WebActions
    {
        public static void OpenWebMonitor(Settings cfg)
        {
            // 不再阻断，只尝试获取运行端口，如果没运行则使用配置端口
            int port = 0;
            
            if (LiteWebServer.Instance != null && LiteWebServer.Instance.IsRunning)
            {
                port = LiteWebServer.Instance.CurrentRunningPort;
            }

            // 如果服务没运行或端口无效，使用配置端口
            if (port <= 0 && cfg != null)
            {
                port = cfg.WebServerPort;
            }

            if (port > 0)
            {
                string host = "localhost";
                // 优先使用内网IP
                if (HardwareMonitor.Instance != null)
                {
                    string ip = HardwareMonitor.Instance.GetNetworkIP();
                    if (!string.IsNullOrEmpty(ip) && ip != "0.0.0.0" && ip != "127.0.0.1") 
                    {
                        host = ip;
                    }
                }

                string url = $"http://{host}:{port}";
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    // 如果使用 IP 打开失败，尝试回退到 localhost
                    if (host != "localhost")
                    {
                         try 
                         {
                             Process.Start(new ProcessStartInfo($"http://localhost:{port}") { UseShellExecute = true });
                         }
                         catch { }
                    }
                    Debug.WriteLine("Failed to open web monitor: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// 判断某个监控项是否带有可点击链接（由插件输出里的 link 字段注入）
        /// </summary>
        public static bool HasMonitorLink(string dashItemKey)
        {
            return !string.IsNullOrEmpty(GetMonitorLink(dashItemKey));
        }

        /// <summary>
        /// 用默认浏览器打开监控项对应的链接，成功返回 true
        /// </summary>
        public static bool TryOpenMonitorLink(string dashItemKey)
        {
            string url = GetMonitorLink(dashItemKey);
            if (string.IsNullOrEmpty(url)) return false;

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to open monitor link: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 监控项 Key 为 DASH.WeiboHot.hot，对应插件注入的 WeiboHot.hot.Link
        /// </summary>
        private static string GetMonitorLink(string dashItemKey)
        {
            if (string.IsNullOrEmpty(dashItemKey) ||
                !dashItemKey.StartsWith("DASH.", StringComparison.OrdinalIgnoreCase))
                return "";

            string link = InfoService.Instance.GetValue(dashItemKey.Substring(5) + ".Link");
            if (string.IsNullOrWhiteSpace(link)) return "";

            link = link.Trim().Replace(" ", "%20");

            if (!link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return "";

            return link;
        }
    }
}
