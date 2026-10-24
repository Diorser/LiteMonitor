using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LiteMonitor.src.Plugins.Native
{
    /// <summary>
    /// 多地址 Ping 原生实现
    /// - 每个目标独立间隔（按目标参数缓存结果，未到期直接返回上次结果）
    /// - 多目标由插件框架并行驱动
    /// - 协议：icmp / tcp / http
    /// - 路径：auto（检测到代理则走代理）/ proxy / direct / nic（强制物理网卡）
    /// </summary>
    public static class MultiPingNative
    {
        public const string DEFAULT_PROXY = "127.0.0.1:7890";

        // 常见本地代理端口（HTTP）。按顺序探测，命中即用。
        private static readonly int[] ProxyProbePorts = { 7890, 7897, 10809, 8889, 8080, 1080 };

        private sealed class TargetState
        {
            public DateTime LastStartUtc = DateTime.MinValue;
            public string LastJson = "";
            public Task<string>? Inflight;
        }

        private static readonly ConcurrentDictionary<string, TargetState> _states = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _stateLock = new();

        private static readonly ConcurrentDictionary<string, HttpClient> _httpClients = new();

        // ---------------- 代理探测 ----------------
        private static DateTime _proxyProbeTime = DateTime.MinValue;
        private static string _detectedProxy = "";
        private static readonly object _proxyLock = new();

        private static string DetectProxy()
        {
            lock (_proxyLock)
            {
                if ((DateTime.UtcNow - _proxyProbeTime).TotalSeconds < 15) return _detectedProxy;
                _proxyProbeTime = DateTime.UtcNow;
                _detectedProxy = "";

                // 1. Windows 系统代理
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                    if (key?.GetValue("ProxyEnable") is int en && en == 1)
                    {
                        string server = key.GetValue("ProxyServer") as string ?? "";
                        var first = server.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(first))
                        {
                            int eq = first.IndexOf('=');
                            if (eq >= 0) first = first.Substring(eq + 1);
                            first = first.Replace("http://", "").Replace("https://", "").Trim().TrimEnd('/');
                            if (first.Length > 0) { _detectedProxy = first; return _detectedProxy; }
                        }
                    }
                }
                catch { }

                // 2. 探测常见本地代理端口
                foreach (var port in ProxyProbePorts)
                {
                    try
                    {
                        using var client = new TcpClient();
                        var task = client.ConnectAsync("127.0.0.1", port);
                        if (task.Wait(120) && client.Connected)
                        {
                            _detectedProxy = $"127.0.0.1:{port}";
                            return _detectedProxy;
                        }
                    }
                    catch { }
                }

                return _detectedProxy;
            }
        }

        private static string ResolveProxy(string mode, string userProxy)
        {
            mode = (mode ?? "").Trim().ToLowerInvariant();
            switch (mode)
            {
                case "direct":
                case "nic":
                    return "";
                case "proxy":
                    if (!string.IsNullOrWhiteSpace(userProxy)) return NormalizeProxy(userProxy);
                    var detected = DetectProxy();
                    return string.IsNullOrEmpty(detected) ? DEFAULT_PROXY : detected;
                default: // auto
                    return DetectProxy();
            }
        }

        private static string NormalizeProxy(string p)
        {
            p = p.Trim();
            p = p.Replace("http://", "").Replace("https://", "").TrimEnd('/');
            return p;
        }

        // ---------------- 物理网卡 ----------------
        private static NetworkInterface? PickPhysicalNic()
        {
            try
            {
                var all = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .Where(n => !IsVirtualNic(n.Name + " " + n.Description))
                    .Where(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                    .ToList();

                var withGateway = all.Where(n => n.GetIPProperties().GatewayAddresses.Count > 0)
                                     .OrderByDescending(n => n.Speed)
                                     .ToList();

                return withGateway.FirstOrDefault() ?? all.OrderByDescending(n => n.Speed).FirstOrDefault();
            }
            catch { return null; }
        }

        private static bool IsVirtualNic(string text)
        {
            text = text.ToLowerInvariant();
            string[] keys = { "wintun", "tap", "tun", "clash", "wireguard", "tailscale",
                              "zerotier", "vpn", "virtual", "hyper-v", "vmware", "virtualbox", "loopback" };
            return keys.Any(k => text.Contains(k));
        }

        private static void BindToInterface(Socket socket, NetworkInterface? nic)
        {
            if (nic == null) return;

            try
            {
                var props = nic.GetIPProperties();
                int idx = props.GetIPv4Properties()?.Index ?? -1;
                if (idx > 0)
                {
                    // IP_UNICAST_IF = 31（.NET 未提供友好枚举名）
                    const SocketOptionName UnicastInterface = (SocketOptionName)31;

                    try { socket.SetSocketOption(SocketOptionLevel.IP, UnicastInterface, idx); }
                    catch
                    {
                        // 部分系统要求网络字节序
                        try { socket.SetSocketOption(SocketOptionLevel.IP, UnicastInterface, BitConverter.GetBytes(IPAddress.HostToNetworkOrder(idx))); }
                        catch { }
                    }
                }

                var local = props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                if (local != null)
                {
                    try { socket.Bind(new IPEndPoint(local, 0)); } catch { }
                }
            }
            catch { }
        }

        // ---------------- 入口 ----------------
        public static async Task<string> RunAsync(string host, string protocol, int port, int intervalSec,
            string mode, string proxy, int timeoutMs, int rounds)
        {
            host = (host ?? "").Trim();
            protocol = string.IsNullOrWhiteSpace(protocol) ? "icmp" : protocol.Trim().ToLowerInvariant();
            if (intervalSec < 1) intervalSec = 1;
            if (timeoutMs < 200) timeoutMs = 200;
            if (timeoutMs > 10000) timeoutMs = 10000;
            if (rounds < 1) rounds = 1;
            if (rounds > 10) rounds = 10;

            if (string.IsNullOrEmpty(host))
            {
                return BuildJson("", "none", "empty", host);
            }

            string stateKey = $"{mode}|{proxy}|{host}|{protocol}|{port}|{intervalSec}|{timeoutMs}|{rounds}";

            var state = _states.GetOrAdd(stateKey, _ => new TargetState());
            Task<string>? work = null;

            lock (_stateLock)
            {
                bool due = state.LastStartUtc == DateTime.MinValue ||
                           (DateTime.UtcNow - state.LastStartUtc).TotalSeconds >= intervalSec;

                if (!due)
                {
                    // 未到间隔：直接返回上次结果（首次则为加载态）
                    if (!string.IsNullOrEmpty(state.LastJson)) return state.LastJson;
                }

                if (state.Inflight != null && !state.Inflight.IsCompleted)
                {
                    work = state.Inflight; // 上一次测量尚未结束，复用
                }
                else if (due)
                {
                    state.LastStartUtc = DateTime.UtcNow;
                    string key = stateKey;
                    work = MeasureSafeAsync(host, protocol, port, mode, proxy, timeoutMs, rounds);
                    state.Inflight = work;
                    _ = work.ContinueWith(t =>
                    {
                        try
                        {
                            if (t.Status == TaskStatus.RanToCompletion)
                            {
                                state.LastJson = t.Result;
                            }
                        }
                        catch { }
                        finally
                        {
                            lock (_stateLock) { state.Inflight = null; }
                            // 一段时间未访问则清理，避免无界增长
                            if (_states.Count > 200) CleanupStates();
                        }
                    });
                }
                else
                {
                    return string.IsNullOrEmpty(state.LastJson) ? BuildJson("", "none", "waiting", host) : state.LastJson;
                }
            }

            if (work != null) return await work.ConfigureAwait(false);
            return string.IsNullOrEmpty(state.LastJson) ? BuildJson("", "none", "waiting", host) : state.LastJson;
        }

        private static void CleanupStates()
        {
            var stale = _states.Where(kv => kv.Value.Inflight == null &&
                                           (DateTime.UtcNow - kv.Value.LastStartUtc).TotalMinutes > 30)
                               .Select(kv => kv.Key).Take(50).ToList();
            foreach (var k in stale) _states.TryRemove(k, out _);
        }

        private static async Task<string> MeasureSafeAsync(string host, string protocol, int port,
            string mode, string proxy, int timeoutMs, int rounds)
        {
            string path = mode;
            try
            {
                long ms;
                switch (protocol)
                {
                    case "tcp":
                        ms = await MeasureTcpAsync(host, port, mode, proxy, timeoutMs, rounds);
                        break;
                    case "http":
                    case "https":
                        ms = await MeasureHttpAsync(host, port, mode, proxy, timeoutMs, protocol == "https");
                        break;
                    default:
                        ms = await MeasureIcmpAsync(host, mode, timeoutMs, rounds);
                        break;
                }

                if (ms >= 0) return BuildJson(ms.ToString(), path, "ok", host);
                return BuildJson("", path, "timeout", host);
            }
            catch (Exception ex)
            {
                return BuildJson("", path, "err:" + ex.GetType().Name, host);
            }
        }

        // ---------------- ICMP ----------------
        private static async Task<long> MeasureIcmpAsync(string host, string mode, int timeoutMs, int rounds)
        {
            bool nicMode = string.Equals(mode, "nic", StringComparison.OrdinalIgnoreCase);

            IPAddress? addr = null;
            if (nicMode)
            {
                if (IPAddress.TryParse(host, out var parsed)) addr = parsed;
                else
                {
                    try
                    {
                        var addrs = await Dns.GetHostAddressesAsync(host);
                        addr = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                    }
                    catch { }
                }

                // 仅 IPv4 支持强制网卡（原始套接字），失败则退回普通 Ping
                if (addr != null && addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    var nic = PickPhysicalNic();
                    if (nic != null)
                    {
                        long raw = await RawIcmpAsync(addr, nic, timeoutMs, rounds);
                        if (raw >= 0) return raw;
                    }
                }
            }

            // 普通 ICMP（direct / auto / proxy 均直接发 ICMP，代理不参与 ICMP）
            long best = -1;
            using var ping = new Ping();
            for (int i = 0; i < rounds; i++)
            {
                try
                {
                    var reply = await ping.SendPingAsync(host, timeoutMs);
                    if (reply.Status == IPStatus.Success && (best < 0 || reply.RoundtripTime < best))
                        best = reply.RoundtripTime;
                }
                catch { }
            }
            return best;
        }

        private static async Task<long> RawIcmpAsync(IPAddress addr, NetworkInterface nic, int timeoutMs, int rounds)
        {
            long best = -1;

            for (int i = 0; i < rounds; i++)
            {
                try
                {
                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
                    BindToInterface(socket, nic);

                    ushort id = (ushort)Environment.ProcessId;
                    ushort seq = (ushort)(i + 1);
                    byte[] packet = BuildIcmpEcho(id, seq, 32);

                    var sw = Stopwatch.StartNew();
                    await socket.SendToAsync(new ArraySegment<byte>(packet), SocketFlags.None,
                        new IPEndPoint(addr, 0));

                    using var cts = new CancellationTokenSource(timeoutMs);
                    byte[] buf = new byte[1500];
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

                    while (!cts.IsCancellationRequested)
                    {
                        int n;
                        try
                        {
                            var result = await socket.ReceiveFromAsync(new ArraySegment<byte>(buf), SocketFlags.None,
                                remote, cts.Token);
                            n = result.ReceivedBytes;
                        }
                        catch (OperationCanceledException) { break; }
                        catch (SocketException) { break; }

                        if (n < 8) continue;
                        int ihl = (buf[0] & 0x0F) * 4;
                        if (n < ihl + 8) continue;
                        if (buf[ihl] != 0) continue; // Echo Reply

                        int rid = (buf[ihl + 4] << 8) | buf[ihl + 5];
                        int rseq = (buf[ihl + 6] << 8) | buf[ihl + 7];
                        if (rid == id && rseq == seq)
                        {
                            long ms = sw.ElapsedMilliseconds;
                            if (best < 0 || ms < best) best = ms;
                            break;
                        }
                    }
                }
                catch { }
            }

            return best;
        }

        private static byte[] BuildIcmpEcho(ushort id, ushort seq, int payloadSize)
        {
            byte[] packet = new byte[8 + payloadSize];
            packet[0] = 8; // Echo Request
            packet[1] = 0;
            packet[4] = (byte)(id >> 8);
            packet[5] = (byte)(id & 0xFF);
            packet[6] = (byte)(seq >> 8);
            packet[7] = (byte)(seq & 0xFF);

            for (int i = 8; i < packet.Length; i++) packet[i] = (byte)(i & 0xFF);

            ushort checksum = ComputeChecksum(packet);
            packet[2] = (byte)(checksum >> 8);
            packet[3] = (byte)(checksum & 0xFF);
            return packet;
        }

        private static ushort ComputeChecksum(byte[] data)
        {
            long sum = 0;
            for (int i = 0; i < data.Length; i += 2)
            {
                sum += (i + 1 < data.Length) ? (ushort)((data[i] << 8) | data[i + 1]) : (ushort)(data[i] << 8);
            }
            while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
            return (ushort)~sum;
        }

        // ---------------- TCP ----------------
        private static async Task<long> MeasureTcpAsync(string host, int port, string mode, string proxy,
            int timeoutMs, int rounds)
        {
            if (port <= 0 || port > 65535) port = 443;
            bool nicMode = string.Equals(mode, "nic", StringComparison.OrdinalIgnoreCase);

            string proxyAddr = ResolveProxy(mode, proxy);
            bool useProxy = !string.IsNullOrEmpty(proxyAddr) && !nicMode;

            long best = -1;
            for (int i = 0; i < rounds; i++)
            {
                long ms = await TcpConnectOnceAsync(host, port, useProxy ? proxyAddr : null, timeoutMs,
                    nicMode ? PickPhysicalNic() : null);
                if (ms >= 0 && (best < 0 || ms < best)) best = ms;
                if (best == 0) break;
            }
            return best;
        }

        private static async Task<long> TcpConnectOnceAsync(string host, int port, string? proxyAddr,
            int timeoutMs, NetworkInterface? nic)
        {
            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(timeoutMs);

            try
            {
                if (!string.IsNullOrEmpty(proxyAddr))
                {
                    var (pHost, pPort) = SplitHostPort(proxyAddr, 7890);
                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    await socket.ConnectAsync(pHost, pPort, cts.Token);

                    string req = $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\nProxy-Connection: keep-alive\r\n\r\n";
                    var ns = new NetworkStream(socket, ownsSocket: false);
                    byte[] reqBytes = Encoding.ASCII.GetBytes(req);
                    await ns.WriteAsync(reqBytes, cts.Token);

                    byte[] buf = new byte[512];
                    var sb = new StringBuilder();
                    while (sb.Length < 1024)
                    {
                        int n = await ns.ReadAsync(buf, cts.Token);
                        if (n <= 0) break;
                        sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                        if (sb.ToString().Contains("\r\n\r\n")) break;
                    }

                    string head = sb.ToString();
                    if (Regex.IsMatch(head, @"^HTTP/1\.[01]\s+200", RegexOptions.IgnoreCase))
                        return sw.ElapsedMilliseconds;
                    return -1;
                }

                using var direct = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                if (nic != null) BindToInterface(direct, nic);
                await direct.ConnectAsync(host, port, cts.Token);
                return sw.ElapsedMilliseconds;
            }
            catch { return -1; }
        }

        // ---------------- HTTP ----------------
        private static async Task<long> MeasureHttpAsync(string host, int port, string mode, string proxy,
            int timeoutMs, bool forceHttps)
        {
            if (port <= 0 || port > 65535) port = forceHttps ? 443 : 80;

            string url;
            if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = host;
            }
            else
            {
                url = $"{(forceHttps ? "https" : "http")}://{host}:{port}/";
            }

            bool nicMode = string.Equals(mode, "nic", StringComparison.OrdinalIgnoreCase);
            string proxyAddr = ResolveProxy(mode, proxy);
            bool useProxy = !string.IsNullOrEmpty(proxyAddr) && !nicMode;

            string cacheKey = nicMode ? "nic" : (useProxy ? "proxy:" + proxyAddr : "direct");

            var client = _httpClients.GetOrAdd(cacheKey, _ =>
            {
                var handler = new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    MaxConnectionsPerServer = 10
                };

                if (useProxy)
                {
                    handler.Proxy = new WebProxy(proxyAddr);
                    handler.UseProxy = true;
                }
                else
                {
                    handler.UseProxy = false;
                }

                if (nicMode)
                {
                    var nic = PickPhysicalNic();
                    handler.ConnectCallback = async (ctx, ct) =>
                    {
                        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                        BindToInterface(socket, nic);
                        try
                        {
                            await socket.ConnectAsync(ctx.DnsEndPoint, ct);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }
                    };
                }

                var c = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(Math.Max(timeoutMs, 1000) + 2000) };
                c.DefaultRequestHeaders.Add("User-Agent", "LiteMonitor/1.0");
                c.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
                return c;
            });

            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                return sw.ElapsedMilliseconds;
            }
            catch { return -1; }
        }

        // ---------------- 工具 ----------------
        private static (string host, int port) SplitHostPort(string value, int defaultPort)
        {
            value = NormalizeProxy(value);
            int idx = value.LastIndexOf(':');
            if (idx > 0 && idx < value.Length - 1 &&
                int.TryParse(value.Substring(idx + 1), out int p) && p > 0 && p <= 65535)
            {
                return (value.Substring(0, idx), p);
            }
            return (value, defaultPort);
        }

        private static string BuildJson(string lat, string path, string status, string target)
        {
            // 手写 JSON，避免序列化开销与转义问题
            return "{\"lat\":\"" + Escape(lat) + "\",\"path\":\"" + Escape(path) +
                   "\",\"status\":\"" + Escape(status) + "\",\"target\":\"" + Escape(target) + "\"}";
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
