using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.src.WebServer
{
    public class LiteWebServer : IDisposable
    {
        private TcpListener? _listener;
        private volatile bool _isRunning = false;
        private readonly Settings _cfg;
        // 存储所有活跃的 WebSocket 客户端
        private readonly ConcurrentDictionary<TcpClient, bool> _wsClients = new();

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
                _listener = new TcpListener(IPAddress.Any, _cfg.WebServerPort);
                _listener.Start();
                _isRunning = true;

                // 1. 启动监听连接的循环
                Task.Run(ListenLoop);
                
                // 2. 启动数据广播循环 (WebSocket 推送)
                Task.Run(BroadcastLoop);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("WebServer Start Error: " + ex.Message);
            }
        }

        public void Stop()
        {
            _isRunning = false;
            try { _listener?.Stop(); } catch { }
            // 关闭所有客户端
            foreach (var client in _wsClients.Keys) try { client.Close(); } catch { }
            _wsClients.Clear();
        }

        private async Task ListenLoop()
        {
            while (_isRunning && _listener != null)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClient(client));
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { Debug.WriteLine(ex.Message); }
            }
        }

        // === 核心广播循环 ===
        private async Task BroadcastLoop()
        {
            while (_isRunning)
            {
                try
                {
                    if (!_wsClients.IsEmpty)
                    {
                        string json = GetDynamicSnapshotJson();
                        byte[] frame = EncodeWebSocketFrame(json);

                        foreach (var client in _wsClients.Keys)
                        {
                            try
                            {
                                if (!client.Connected) 
                                {
                                    _wsClients.TryRemove(client, out _);
                                    continue;
                                }
                                var stream = client.GetStream();
                                await stream.WriteAsync(frame, 0, frame.Length);
                            }
                            catch 
                            { 
                                // 发送失败移除客户端
                                _wsClients.TryRemove(client, out _);
                                try { client.Close(); } catch { }
                            }
                        }
                    }
                }
                catch { }

                // 每秒推送一次
                await Task.Delay(1000);
            }
        }

        private void HandleClient(TcpClient client)
        {
            // 注意：这里不要用 using(client)，因为如果是 WebSocket 连接，我们需要保持它存活
            // 只有 HTTP 请求处理完后才关闭
            try
            {
                var stream = client.GetStream();
                byte[] buffer = new byte[4096];
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) { client.Close(); return; }

                string requestStr = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                
                // === 1. 判断是否为 WebSocket 升级请求 ===
                if (Regex.IsMatch(requestStr, "GET", RegexOptions.IgnoreCase) && 
                    Regex.IsMatch(requestStr, "Upgrade: websocket", RegexOptions.IgnoreCase))
                {
                    // 处理 WebSocket 握手
                    if (Handshake(stream, requestStr))
                    {
                        // 握手成功，加入客户端列表，保持连接
                        _wsClients.TryAdd(client, true);
                        
                        // ★★★ 核心修复：启动接收循环以处理 Ping/Close 和排空缓冲区 ★★★
                        // 注意：这里不 await，让它在后台运行，HandleClient 可以退出
                        _ = Task.Run(() => ReceiveLoop(client));
                        
                        return; // 退出函数，不关闭 client
                    }
                }
                
                // === 2. 普通 HTTP 请求处理 ===
                HandleHttpRequest(stream, requestStr);
            }
            catch { }
            
            // 如果不是 WebSocket (即上面的 return 没执行)，则处理完 HTTP 后关闭连接
            if (!_wsClients.ContainsKey(client))
            {
                try { client.Close(); } catch { }
            }
        }

        // ★★★ [新增] WebSocket 接收循环 ★★★
        private async Task ReceiveLoop(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                byte[] buffer = new byte[1024];
                
                while (client.Connected && _wsClients.ContainsKey(client))
                {
                    // 阻塞读取，如果断开会抛出异常或返回0
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break; // 客户端关闭连接

                    // 解析帧头判断是否为 Close 帧 (OpCode = 8)
                    // FIN(1) + RSV(3) + OpCode(4)
                    // Byte 0: [FIN][RSV][RSV][RSV][OpCode][OpCode][OpCode][OpCode]
                    // 0x88 = 1000 1000 = FIN + Close
                    if ((buffer[0] & 0x0F) == 0x08)
                    {
                        break; // 收到关闭帧
                    }
                }
            }
            catch { }
            finally
            {
                _wsClients.TryRemove(client, out _);
                try { client.Close(); } catch { }
            }
        }

        private bool Handshake(NetworkStream stream, string request)
        {
            try
            {
                // 1. 提取 Sec-WebSocket-Key
                var match = Regex.Match(request, "Sec-WebSocket-Key: (.*)");
                if (!match.Success) return false;

                string key = match.Groups[1].Value.Trim();

                // 2. 生成 Accept Key (Magic String: 258EAFA5-E914-47DA-95CA-C5AB0DC85B11)
                string magic = key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
                byte[] hash = SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(magic));
                string acceptKey = Convert.ToBase64String(hash);

                // 3. 发送握手响应
                var response = new StringBuilder();
                response.Append("HTTP/1.1 101 Switching Protocols\r\n");
                response.Append("Connection: Upgrade\r\n");
                response.Append("Upgrade: websocket\r\n");
                response.Append($"Sec-WebSocket-Accept: {acceptKey}\r\n");
                response.Append("\r\n");

                byte[] respBytes = Encoding.UTF8.GetBytes(response.ToString());
                stream.Write(respBytes, 0, respBytes.Length);
                return true;
            }
            catch { return false; }
        }

        // ★★★ 优化：缓存 Favicon Base64，避免每次重复转换 ★★★
        private static string _cachedFaviconBase64 = null;

        private static string GetAppIconBase64()
        {
            if (_cachedFaviconBase64 != null) return _cachedFaviconBase64;
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath);
                if (icon != null)
                {
                    using var ms = new System.IO.MemoryStream();
                    icon.ToBitmap().Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    _cachedFaviconBase64 = Convert.ToBase64String(ms.ToArray());
                    return _cachedFaviconBase64;
                }
            }
            catch { }
            return ""; // Fallback
        }

        private void HandleHttpRequest(NetworkStream stream, string request)
        {
            string[] lines = request.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;

            string firstLine = lines[0]; // GET /path HTTP/1.1
            string[] parts = firstLine.Split(' ');
            if (parts.Length < 2) return;
            string path = parts[1];

            string responseBody = "";
            string contentType = "text/html; charset=utf-8";
            int statusCode = 200;
            bool isBinary = false;
            byte[] binaryBody = null;

            if (path.StartsWith("/api/snapshot"))
            {
                contentType = "application/json";
                responseBody = GetDynamicSnapshotJson();
            }
            else if (path == "/" || path == "/index.html" || path.StartsWith("/?"))
            {
                // ★★★ 动态注入 Favicon Base64 ★★★
                string iconBase64 = GetAppIconBase64();
                responseBody = WebPageContent.IndexHtml.Replace("{{FAVICON}}", 
                    !string.IsNullOrEmpty(iconBase64) 
                    ? $"<link rel='icon' type='image/png' href='data:image/png;base64,{iconBase64}'>" 
                    : "");
            }
            else if (path == "/favicon.ico")
            {
                // 如果浏览器请求 favicon.ico，也可以尝试返回二进制流 (可选)
                statusCode = 404; // 暂时保持 404，因为我们已经用了 Base64 内嵌
            }
            else
            {
                string iconBase64 = GetAppIconBase64();
                responseBody = WebPageContent.IndexHtml.Replace("{{FAVICON}}", 
                     !string.IsNullOrEmpty(iconBase64) 
                     ? $"<link rel='icon' type='image/png' href='data:image/png;base64,{iconBase64}'>" 
                     : "");
            }

            SendHttpResponse(stream, statusCode, contentType, responseBody);
        }

        private void SendHttpResponse(NetworkStream stream, int statusCode, string contentType, string body)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {statusCode} OK\r\n");
            sb.Append($"Content-Type: {contentType}\r\n");
            sb.Append($"Content-Length: {bodyBytes.Length}\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("\r\n");

            byte[] headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
        }

        // === WebSocket 帧编码 (只实现发送 Text Frame) ===
        private byte[] EncodeWebSocketFrame(string message)
        {
            byte[] payload = Encoding.UTF8.GetBytes(message);
            List<byte> frame = new List<byte>();

            // FIN(1) + RSV(000) + OpCode(1=Text) => 10000001 => 0x81
            frame.Add(0x81);

            // Mask(0) + Length
            if (payload.Length < 126)
            {
                frame.Add((byte)payload.Length);
            }
            else if (payload.Length <= 65535)
            {
                frame.Add(126);
                frame.Add((byte)((payload.Length >> 8) & 0xFF));
                frame.Add((byte)(payload.Length & 0xFF));
            }
            else
            {
                frame.Add(127);
                // 64-bit length (assuming < 2GB for JSON)
                frame.AddRange(new byte[] { 0, 0, 0, 0 }); 
                frame.Add((byte)((payload.Length >> 24) & 0xFF));
                frame.Add((byte)((payload.Length >> 16) & 0xFF));
                frame.Add((byte)((payload.Length >> 8) & 0xFF));
                frame.Add((byte)(payload.Length & 0xFF));
            }

            frame.AddRange(payload);
            return frame.ToArray();
        }

        private string GetDynamicSnapshotJson()
        {
            var hw = HardwareMonitor.Instance;
            if (hw == null) return "{}";

            var dataList = new List<object>();
            string localIp = hw.GetNetworkIP() ?? "127.0.0.1";

            // ★★★ 修复：创建列表副本并加锁，防止遍历时 UI 线程修改集合导致崩溃 ★★★
            List<MonitorItemConfig> itemsCopy;
            lock (_cfg.MonitorItems)
            {
                // ★★★ 修复：应用与主界面一致的排序逻辑 (Group分组 -> Group排序 -> Item排序) ★★★
                itemsCopy = _cfg.MonitorItems
                    .GroupBy(x => x.UIGroup)
                    .OrderBy(g => g.Min(x => x.SortIndex))
                    .SelectMany(g => g.OrderBy(x => x.SortIndex))
                    .ToList();
            }

            foreach (var item in itemsCopy)
            {
                if (item.Key == "NET.IP") continue; 

                // ★★★ 修复：优先显示 DisplayLabel (包含 DynamicLabel)，解决插件名称不显示问题 ★★★
                string displayName = !string.IsNullOrEmpty(item.DisplayLabel)
                    ? item.DisplayLabel
                    : LanguageManager.T("Items." + item.Key);
                
                string groupId = item.UIGroup.ToUpper();
                string groupDisplay = LanguageManager.T("Groups." + item.UIGroup);

                string valStr = "";
                string unit = "";
                double pct = 0;
                int status = 0;
                bool isPrimary = false;

                if (item.Key.StartsWith("DASH."))
                {
                    // 特殊处理 DASH.HOST 等纯信息项 (以及插件项)
                    string dashKey = item.Key.Substring(5);
                    valStr = InfoService.Instance.GetValue(dashKey);
                    
                    // ★★★ 修复：读取插件写入的颜色状态 (.Color) ★★★
                    string colorStr = InfoService.Instance.GetValue(dashKey + ".Color");
                    if (int.TryParse(colorStr, out int cVal)) status = cVal;

                    // 读取插件写入的单位 (.Unit)
                    string unitStr = InfoService.Instance.GetValue(dashKey + ".Unit");
                    if (!string.IsNullOrEmpty(unitStr)) unit = unitStr;
                }
                else 
                {
                    // 普通监控项 (包括非DASH前缀的插件项，如果有的话)
                    float? val = hw.Get(item.Key);
                    if (val != null)
                    {
                        var parts = UIUtils.FormatValueParts(item.Key, val.Value);
                        valStr = parts.valStr;
                        unit = parts.unitStr;

                        bool isRate = (item.Key.StartsWith("NET") || item.Key.StartsWith("DISK")) && !item.Key.Contains("Temp");
                        if (isRate && !string.IsNullOrEmpty(unit)) unit += "/s";

                        // 计算百分比
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

                        status = UIUtils.GetColorResult(item.Key, val.Value);
                    }

                    // Primary Logic
                     if (groupId == "NET" || groupId == "DISK" || groupId == "DATA") 
                         isPrimary = true;
                     else if (item.Key.StartsWith("NET") || item.Key.StartsWith("DISK") || item.Key.StartsWith("DATA"))
                         isPrimary = true;
                     else if (item.Key.Contains("Load") || item.Key.Contains("Temp") || groupId == "MEM")
                         isPrimary = true;
                }

                if (string.IsNullOrEmpty(valStr)) valStr = "--";

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
                    port = _cfg.WebServerPort,
                    uptime = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).ToString(@"dd\.hh\:mm\:ss"),
                },
                items = dataList
            };

            return JsonSerializer.Serialize(payload);
        }

        public void Dispose() => Stop();
    }
}