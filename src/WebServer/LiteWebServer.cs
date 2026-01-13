using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor.src.WebServer
{
    public class LiteWebServer : IDisposable
    {
        private HttpListener? _listener;
        private Thread? _serverThread;
        private volatile bool _isRunning = false;
        private readonly Settings _cfg;

        public static LiteWebServer? Instance { get; private set; }

        public LiteWebServer(Settings cfg)
        {
            _cfg = cfg;
            Instance = this;
        }

        public void Start()
        {
            if (_isRunning) return;
            if (!_cfg.WebServerEnabled) return;

            try
            {
                // 配置防火墙 (传入当前端口)
                ConfigFirewall(_cfg.WebServerPort);
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://*:{_cfg.WebServerPort}/");
                _listener.AuthenticationSchemes = AuthenticationSchemes.Anonymous;
                _listener.Start();
                _isRunning = true;

                _serverThread = new Thread(ListenLoop) { IsBackground = true };
                _serverThread.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("WebServer Start Error: " + ex.Message);
            }
        }

        public void Stop()
        {
            _isRunning = false;
            try { _listener?.Stop(); _listener?.Close(); } catch { }
        }

        private void ListenLoop()
        {
            while (_isRunning && _listener != null && _listener.IsListening)
            {
                try
                {
                    var context = _listener.GetContext();
                    Task.Run(() => ProcessRequest(context));
                }
                catch { }
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            var response = context.Response;
            string responseString = "";
            string contentType = "text/html";
            response.AddHeader("Access-Control-Allow-Origin", "*");

            try
            {
                if (context.Request.Url?.AbsolutePath == "/api/snapshot")
                {
                    contentType = "application/json";
                    responseString = GetDynamicSnapshotJson();
                }
                else
                {
                    contentType = "text/html; charset=utf-8";
                    responseString = WebPageContent.IndexHtml;
                }

                byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                response.ContentLength64 = buffer.Length;
                response.ContentType = contentType;
                response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            catch { response.StatusCode = 500; }
            finally { response.OutputStream.Close(); }
        }

        private string GetDynamicSnapshotJson()
        {
            var hw = HardwareMonitor.Instance;
            if (hw == null) return "{}";

            var dataList = new List<object>();
            string localIp = hw.GetNetworkIP() ?? "127.0.0.1";

            foreach (var item in _cfg.MonitorItems)
            {
                if (item.Key == "NET.IP") continue; 

                float? val = hw.Get(item.Key);
                if (val == null) continue;

                string displayName = !string.IsNullOrEmpty(item.UserLabel) 
                    ? item.UserLabel 
                    : LanguageManager.T("Items." + item.Key);
                
                string groupId = item.UIGroup.ToUpper();
                string groupDisplay = LanguageManager.T("Groups." + item.UIGroup);

                // 格式化
                var parts = UIUtils.FormatValueParts(item.Key, val.Value);
                string valStr = parts.valStr;
                string unit = parts.unitStr;

                bool isRate = (item.Key.StartsWith("NET") || item.Key.StartsWith("DISK")) && !item.Key.Contains("Temp");
                if (isRate && !string.IsNullOrEmpty(unit)) unit += "/s";

                // 计算百分比
                double pct = 0;
                if (item.Key.Contains("Clock") || item.Key.Contains("Power") || 
                    item.Key.Contains("Fan") || item.Key.Contains("Pump") || item.Key.Contains("FPS"))
                {
                    pct = UIUtils.GetAdaptivePercentage(item.Key, val.Value) * 100;
                }
                else if (item.Key.Contains("Load") || unit.Contains("%"))
                {
                    pct = val.Value;
                }
                else if (item.Key.Contains("Temp"))
                {
                    pct = val.Value; 
                }
                else if (isRate)
                {
                    pct = Math.Min((val.Value / (100 * 1024 * 1024)) * 100, 100);
                }

                int status = UIUtils.GetColorResult(item.Key, val.Value);

                // 【核心修复】哪些指标能决定大卡片的颜色 (Primary)
                // 1. 流量/磁盘/数据：必须算 Primary，否则流量红了卡片不红
                // 2. 负载/温度/内存：算 Primary
                // 3. 排除：风扇、频率、功耗 (这些红了只在列表里红，不影响卡片框)
                bool isPrimary = false;
                
                if (groupId == "NET" || groupId == "DISK" || groupId == "DATA") 
                    isPrimary = true;
                else if (item.Key.StartsWith("NET") || item.Key.StartsWith("DISK") || item.Key.StartsWith("DATA"))
                    isPrimary = true;
                else if (item.Key.Contains("Load") || item.Key.Contains("Temp") || groupId == "MEM")
                    isPrimary = true;

                dataList.Add(new {
                    k = item.Key,
                    n = displayName,
                    v = valStr,
                    u = unit,
                    gid = groupId,
                    gn = groupDisplay,
                    pct = pct,
                    sts = status,
                    primary = isPrimary
                });
            }

            var payload = new {
                sys = new {
                    host = Environment.MachineName,
                    ip = localIp,
                    uptime = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).ToString(@"dd\.hh\:mm\:ss"),
                },
                items = dataList
            };

            return JsonSerializer.Serialize(payload);
        }
        // 添加防火墙规则，允许指定端口的入站流量
        private void ConfigFirewall(int port)
        {
            string ruleName = "LiteMonitor Web Remote";
            
            // 注意：我们不再获取 exePath，也不再限制 program="..."
            // 这样规则就会变成“所有符合指定条件（端口）的程序”，允许 http.sys 流量通过

            try 
            {
                // 1. 删除旧规则
                RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");

                // 2. 添加新规则 (纯端口开放，不绑定特定程序)
                // dir=in       : 入站
                // action=allow : 允许
                // protocol=tcp : TCP协议
                // localport=...: 端口
                // profile=any  : 允许 公用/专用/域 网络
                RunNetsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=tcp localport={port} profile=any");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Firewall Config Error: " + ex.Message);
            }
        }

        // 辅助方法：执行 netsh 命令
        private void RunNetsh(string args)
        {
            var psi = new ProcessStartInfo("netsh", args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.Verb = "runas"; // 确保请求管理员权限(虽然主程序已经是管理员)
            
            var p = Process.Start(psi);
            p.WaitForExit(1000); // 最多等1秒，防止卡死
        }

        public void Dispose() => Stop();
    }
}