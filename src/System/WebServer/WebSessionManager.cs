using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Buffers;
using System.Diagnostics;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.WebServer
{
    public class WebSessionManager
    {
        private readonly ConcurrentDictionary<TcpClient, bool> _wsClients = new();
        private readonly Action<Utf8JsonWriter> _dataProvider;
        private volatile bool _isRunning = false;

        public WebSessionManager(Action<Utf8JsonWriter> dataProvider)
        {
            _dataProvider = dataProvider;
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            Task.Run(BroadcastLoop);
        }

        public void Stop()
        {
            _isRunning = false;
            foreach (var client in _wsClients.Keys)
            {
                try { client.Close(); } catch { }
            }
            _wsClients.Clear();
        }

        /// <summary>
        /// 统一处理客户端连接（自动识别 WebSocket 或 HTTP）
        /// </summary>
        public async Task HandleClientAsync(TcpClient client)
        {
            byte[] buffer = null;
            try
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 5000; // 防恶意连接

                // 1. 读取请求头 (最多 8KB)
                buffer = ArrayPool<byte>.Shared.Rent(8192);
                int bytesRead = await ReadHeaderAsync(stream, buffer);
                
                if (bytesRead == 0) { client.Close(); return; }

                string requestStr = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                // 2. 协议分流
                if (IsWebSocketUpgrade(requestStr))
                {
                    if (Handshake(stream, requestStr))
                    {
                        _wsClients.TryAdd(client, true);
                        await ReceiveLoop(client); // 进入 WS 循环
                    }
                }
                else
                {
                    HandleHttpRequest(stream, requestStr);
                    client.Close(); // HTTP 是短连接
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Client Error: {ex.Message}");
                try { client.Close(); } catch { }
            }
            finally
            {
                if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // --- 内部网络辅助方法 ---

        private async Task<int> ReadHeaderAsync(NetworkStream stream, byte[] buffer)
        {
            int totalBytes = 0;
            while (totalBytes < buffer.Length)
            {
                // 预留空间检查
                if (totalBytes >= buffer.Length) break;

                int read = await stream.ReadAsync(buffer, totalBytes, buffer.Length - totalBytes);
                if (read == 0) break;
                
                totalBytes += read;
                if (FindHeaderEnd(buffer, totalBytes) >= 0) return totalBytes;
            }
            return totalBytes;
        }

        private int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 0; i < length - 3; i++)
            {
                if (buffer[i] == 13 && buffer[i+1] == 10 && buffer[i+2] == 13 && buffer[i+3] == 10)
                    return i + 4; // 返回包含结束符的长度
            }
            return -1;
        }

        private bool IsWebSocketUpgrade(string req)
        {
            return Regex.IsMatch(req, "GET", RegexOptions.IgnoreCase) && 
                   Regex.IsMatch(req, "Upgrade: websocket", RegexOptions.IgnoreCase);
        }

        private void HandleHttpRequest(NetworkStream stream, string request)
        {
            string[] lines = request.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;
            
            // [Fix] 避免重复 Split() 调用，减少内存分配
            var requestParts = lines[0].Split(' ');
            string path = requestParts.Length > 1 ? requestParts[1] : "/";
            
            if (path.StartsWith("/api/snapshot"))
            {
                if (!CheckAuth(request))
                {
                    SendHttpResponse(stream, 401, "text/plain", Encoding.UTF8.GetBytes("Unauthorized"), 12);
                    return;
                }

                using var ms = new MemoryStream();
                using var writer = new Utf8JsonWriter(ms);
                _dataProvider(writer);
                writer.Flush();
                SendHttpResponse(stream, 200, "application/json", ms.GetBuffer(), (int)ms.Length);
            }
            else if (path == "/favicon.ico")
            {
                SendHttpResponse(stream, 404, "text/html", Array.Empty<byte>(), 0);
            }
            else
            {
                // 默认返回首页
                string iconBase64 = WebPageContent.GetAppIconBase64();
                bool authRequired = !string.IsNullOrEmpty(Settings.Load().WebServerPassword);
                
                string html = WebPageContent.IndexHtml
                    .Replace("{{FAVICON}}", !string.IsNullOrEmpty(iconBase64) ? $"<link rel='icon' type='image/png' href='data:image/png;base64,{iconBase64}'>" : "")
                    .Replace("{{AUTH_REQUIRED}}", authRequired.ToString().ToLower());
                
                byte[] data = Encoding.UTF8.GetBytes(html);
                SendHttpResponse(stream, 200, "text/html; charset=utf-8", data, data.Length);
            }
        }

        private bool CheckAuth(string requestHeader)
        {
            var pwd = Settings.Load().WebServerPassword;
            if (string.IsNullOrEmpty(pwd)) return true;

            var firstLine = requestHeader.Split('\n')[0]; 
            var match = Regex.Match(firstLine, @"[?&]pwd=([^&\s]+)");
            if (match.Success)
            {
                return match.Groups[1].Value == pwd;
            }
            return false;
        }

        private void SendHttpResponse(NetworkStream stream, int code, string type, byte[] body, int len)
        {
            string headers = $"HTTP/1.1 {code} OK\r\nContent-Type: {type}\r\nContent-Length: {len}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
            byte[] hBytes = Encoding.UTF8.GetBytes(headers);
            stream.Write(hBytes, 0, hBytes.Length);
            if (len > 0) stream.Write(body, 0, len);
        }

        // --- WebSocket 核心逻辑 ---

        private async Task BroadcastLoop()
        {
            while (_isRunning)
            {
                // [Optimization] 如果没有任何客户端连接，降低循环频率以节省资源
                if (_wsClients.IsEmpty)
                {
                    await Task.Delay(2000);
                    continue;
                }

                byte[] buffer = null;
                try
                {

                    buffer = ArrayPool<byte>.Shared.Rent(32 * 1024); 
                    
                    int payloadLen;
                    using (var ms = new System.IO.MemoryStream(buffer))
                    using (var writer = new Utf8JsonWriter(ms))
                    {
                        _dataProvider(writer);
                        writer.Flush();
                        payloadLen = (int)ms.Position;
                    }

                    int headerLen = 2;
                    if (payloadLen >= 65536) headerLen += 8;
                    else if (payloadLen >= 126) headerLen += 2;
                    int totalLen = headerLen + payloadLen;

                    // Rent a target buffer for the frame
                    byte[] frameBuffer = ArrayPool<byte>.Shared.Rent(totalLen);
                    try
                    {
                        int payloadOffset = EncodeFrameHeader(payloadLen, frameBuffer);
                        Buffer.BlockCopy(buffer, 0, frameBuffer, payloadOffset, payloadLen);

                        // Now we can return the "JSON Scratchpad" buffer IMMEDIATELY!
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = null; // Prevent double return in finally

                        // Broadcast using frameBuffer
                        var tasks = new List<Task>();
                        // Capture the buffer and length for the closure
                        var bufToSend = frameBuffer; 
                        var lenToSend = totalLen;

                        foreach (var client in _wsClients.Keys)
                        {
                            if (!client.Connected)
                            {
                                _wsClients.TryRemove(client, out _);
                                continue;
                            }
                            // We must NOT return frameBuffer until ALL sends are done.
                            // But since we await Task.WhenAll, it's fine.
                            tasks.Add(SendFrameSafeAsync(client, bufToSend, lenToSend));
                        }

                        if (tasks.Count > 0)
                        {
                            await Task.WhenAll(tasks);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(frameBuffer);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Broadcast Error: {ex.Message}");
                }
                finally
                {
                    if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
                }

                // 正常广播频率：每秒一次
                await Task.Delay(1000);
            }
        }

        private int EncodeFrameHeader(int payloadLen, byte[] buffer)
        {
            int offset = 0;

            // FIN(1) + RSV(000) + OpCode(1=Text) => 10000001 => 0x81
            buffer[offset++] = 0x81;

            // Mask(0) + Length
            if (payloadLen < 126)
            {
                buffer[offset++] = (byte)payloadLen;
            }
            else if (payloadLen <= 65535)
            {
                buffer[offset++] = 126;
                buffer[offset++] = (byte)((payloadLen >> 8) & 0xFF);
                buffer[offset++] = (byte)(payloadLen & 0xFF);
            }
            else
            {
                buffer[offset++] = 127;
                // 64-bit length
                buffer[offset++] = 0; buffer[offset++] = 0; buffer[offset++] = 0; buffer[offset++] = 0;
                buffer[offset++] = (byte)((payloadLen >> 24) & 0xFF);
                buffer[offset++] = (byte)((payloadLen >> 16) & 0xFF);
                buffer[offset++] = (byte)((payloadLen >> 8) & 0xFF);
                buffer[offset++] = (byte)(payloadLen & 0xFF);
            }
            return offset;
        }

        private async Task SendFrameSafeAsync(TcpClient client, byte[] buffer, int length)
        {
            try
            {
                var stream = client.GetStream();
                // [Fix] Add timeout to prevent memory leak from stuck tasks
                using var cts = new CancellationTokenSource(2000); // 2s timeout
                await stream.WriteAsync(buffer, 0, length, cts.Token);
            }
            catch
            {
                // Remove dead client
                _wsClients.TryRemove(client, out _);
                try { client.Close(); } catch { }
            }
        }


        private async Task ReceiveLoop(TcpClient client)
        {
            byte[] buffer = null;
            try
            {
                var stream = client.GetStream();
                // [Optimization] Use ArrayPool for receive buffer
                buffer = ArrayPool<byte>.Shared.Rent(1024);

                while (client.Connected && _wsClients.ContainsKey(client))
                {
                    // 阻塞读取，如果断开会抛出异常或返回0
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break; // 客户端关闭连接

                    // 解析帧头判断是否为 Close 帧 (OpCode = 8)
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
                if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
                _wsClients.TryRemove(client, out _);
                try { client.Close(); } catch { }
            }
        }

        private bool Handshake(NetworkStream stream, string request)
        {
            try
            {
                if (!CheckAuth(request))
                {
                    SendHttpResponse(stream, 401, "text/plain", Encoding.UTF8.GetBytes("Unauthorized"), 12);
                    return false;
                }

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
    }
}
