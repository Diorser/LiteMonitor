using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using Debug = System.Diagnostics.Debug;

namespace LiteMonitor.src.SystemServices
{
    public class NetworkManager
    {
        // ★★★ [修复] 状态隔离：每个硬件拥有独立的网络状态，不再全局共享 ★★★
        private class NetworkState
        {
            public NetworkInterface? NativeAdapter;
            public long LastNativeUpload;
            public long LastNativeDownload;
            public DateTime LastMatchAttempt = DateTime.MinValue;
            
            // 缓存 LHM 传感器
            public ISensor? CachedUpSensor;
            public ISensor? CachedDownSensor;
        }
        private readonly Dictionary<IHardware, NetworkState> _netStates = new();
        
        // 网络智能缓存
        private IHardware? _cachedNetHw;
        private DateTime _lastNetScan = DateTime.MinValue;
        private readonly DateTime _startTime = DateTime.Now; // 启动时间
        private static string _staticIPCache = "";// [修改] 增加IP静态缓存，切换主题/重启服务时不丢失，解决闪烁问题
        private static DateTime _lastIPCheckTime = DateTime.MinValue; // 上次检查IP的时间

        public void ClearCache()
        {
            _netStates.Clear();
            _cachedNetHw = null;
        }

        // ===========================================================
        // 更新逻辑 (原 UpdateAll 中的部分)
        // ===========================================================
        public void ProcessUpdate(IHardware hw, Settings cfg, double timeDelta, bool isSlowScanTick)
        {
            bool isTarget = (_cachedNetHw != null && hw == _cachedNetHw) ||
                            (hw.Name == cfg.LastAutoNetwork) ||
                            (hw.Name == cfg.PreferredNetwork);

            bool isStartupPhase = (DateTime.Now - _startTime).TotalSeconds < 3;

            if (isTarget)
            {
                hw.Update();
                AccumulateTraffic(hw, cfg, timeDelta);
            }
            else if (isStartupPhase || IsVirtualNetwork(hw.Name))
            {
                return;
            }
            else if (isSlowScanTick)
            {
                hw.Update();
            }
        }

        // [新增] 获取当前 IP (带 10秒 缓存)
        public string GetCurrentIP()
        {
            // 1. 缓存保护：只有当缓存了【有效IP】且距离上次检查不到 30 秒时，才直接返回
            // 关键修改：如果 _staticIPCache 是空的，忽略时间限制，强制重试
            if (!string.IsNullOrEmpty(_staticIPCache) && (DateTime.Now - _lastIPCheckTime).TotalSeconds < 30)
            {
                return _staticIPCache;
            }

            string foundIP = null;

            // 2. 策略A：直接从当前锁定的硬件中获取 (这是最直接的方式)
            try 
            {
                // ★★★ 核心修复：即使 _cachedNetHw 为空（还没锁定网卡），也要尝试从 _netStates 中寻找已匹配 NativeAdapter 的网卡 ★★★
                // 之前的 bug 是：网速跳动说明 MatchNativeNetworkAdapter 成功了，_netStates 里有 NativeAdapter，
                // 但 _cachedNetHw 还没来得及更新，导致这里直接跳过。
                
                // 如果已锁定，优先用锁定的
                if (_cachedNetHw != null && _netStates.TryGetValue(_cachedNetHw, out var state) && state.NativeAdapter != null)
                {
                     foundIP = GetIPv4FromAdapter(state.NativeAdapter);
                }
                
                // 如果没锁定（或锁定的没取到），遍历所有已知状态的网卡 (这些网卡都是有流量活动的)
                if (string.IsNullOrEmpty(foundIP))
                {
                    foreach (var kv in _netStates)
                    {
                        if (kv.Value.NativeAdapter != null)
                        {
                            string ip = GetIPv4FromAdapter(kv.Value.NativeAdapter);
                            if (!string.IsNullOrEmpty(ip))
                            {
                                foundIP = ip;
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            // 3. 策略B (兜底)：如果上面没拿到，才主动遍历系统网卡 (只跑一次)
            if (string.IsNullOrEmpty(foundIP))
            {
                try
                {
                    var nics = NetworkInterface.GetAllNetworkInterfaces();
                    foreach (var nic in nics)
                    {
                        if (nic.OperationalStatus != OperationalStatus.Up) continue;
                        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        if (IsVirtualNetwork(nic.Description) || IsVirtualNetwork(nic.Name)) continue; // 仅兜底物理网卡

                        foundIP = GetIPv4FromAdapter(nic);
                        if (!string.IsNullOrEmpty(foundIP)) break;
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(foundIP))
            {
                _staticIPCache = foundIP;
                _lastIPCheckTime = DateTime.Now;
                return foundIP;
            }
            
            return _staticIPCache; 
        }

        private string GetIPv4FromAdapter(NetworkInterface nic)
        {
            try
            {
                var props = nic.GetIPProperties();
                foreach (var ip in props.UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return ip.Address.ToString();
                    }
                }
            }
            catch { }
            return null;
        }

        // ===========================================================
        // 获取最佳网络数值 (原 Logic.cs 中的 GetNetworkValue/GetBestNetworkValue)
        // ===========================================================
        public float? GetBestValue(string key, Computer computer, Settings cfg, Dictionary<string, float> lastValidMap, object syncLock)
        {
            // 1. 优先手动指定
            if (!string.IsNullOrWhiteSpace(cfg.PreferredNetwork))
            {
                var hw = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Network && h.Name.Equals(cfg.PreferredNetwork, StringComparison.OrdinalIgnoreCase));
                if (hw != null) return ReadNetworkSensor(hw, key, lastValidMap, syncLock);
            }

            // 2. 自动选优 (带缓存) - 原 GetBestNetworkValue
            // A. 尝试运行时缓存
            if (_cachedNetHw != null)
            {
                // ★★★ 【修复 1】存活检查：如果缓存的硬件对象已经不在当前的硬件列表中（已失效），强制丢弃 ★★★
                if (!computer.Hardware.Contains(_cachedNetHw))
                {
                    _cachedNetHw = null;
                }
                else
                {
                    float? cachedVal = ReadNetworkSensor(_cachedNetHw, key, lastValidMap, syncLock);
                    // 逻辑优化：如果有流量，直接用；如果没流量但距离上次全盘扫描 < 3秒，也直接用。
                    if ((cachedVal.HasValue && cachedVal.Value > 0.1f) ||
                        (DateTime.Now - _lastNetScan).TotalSeconds < 3)
                    {
                        return cachedVal;
                    }
                }
            }

            // ★★★ [漏掉的部分] B. 尝试启动时缓存 (Settings 中的记录) ★★★
            if (_cachedNetHw == null && !string.IsNullOrEmpty(cfg.LastAutoNetwork))
            {
                // 尝试直接找上次记住的网卡
                var savedHw = computer.Hardware.FirstOrDefault(h => h.Name == cfg.LastAutoNetwork);
                if (savedHw != null)
                {
                    // 找到了！直接设为缓存，跳过全盘扫描
                    _cachedNetHw = savedHw;
                    _lastNetScan = DateTime.Now;
                    return ReadNetworkSensor(savedHw, key, lastValidMap, syncLock);
                }
            }

            // C. 全盘扫描
            IHardware? bestHw = null;
            double bestScore = double.MinValue;
            ISensor? bestTarget = null;

            foreach (var hw in computer.Hardware.Where(h => h.HardwareType == HardwareType.Network))
            {
                double penalty = IsVirtualNetwork(hw.Name) ? -1e9 : 0;
                ISensor? up = null, down = null;
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Throughput) continue;
                    if (_upKW.Any(k => SensorMap.Has(s.Name, k))) up ??= s;
                    if (_downKW.Any(k => SensorMap.Has(s.Name, k))) down ??= s;
                }
                if (up == null && down == null) continue;
                double score = (up?.Value ?? 0) + (down?.Value ?? 0) + penalty;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestHw = hw;
                    bestTarget = (key == "NET.Up") ? up : down;
                }
            }

            // D. 更新缓存
            if (bestHw != null)
            {
                _cachedNetHw = bestHw;
                _lastNetScan = DateTime.Now;
                
                // ★★★ [漏掉的部分] 记住这次的选择 ★★★
                if (cfg.LastAutoNetwork != bestHw.Name)
                {
                    cfg.LastAutoNetwork = bestHw.Name;
                }
            }

            // E. 返回
            if (bestTarget?.Value is float v && !float.IsNaN(v))
            {
                lock (syncLock) lastValidMap[key] = v;
                return v;
            }
            lock (syncLock) { if (lastValidMap.TryGetValue(key, out var last)) return last; }
            return null;
        }

        private float? ReadNetworkSensor(IHardware hw, string key, Dictionary<string, float> lastValidMap, object syncLock)
        {
            ISensor? target = null;
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Throughput) continue;
                if (key == "NET.Up" && _upKW.Any(k => SensorMap.Has(s.Name, k))) { target = s; break; } 
                if (key == "NET.Down" && _downKW.Any(k => SensorMap.Has(s.Name, k))) { target = s; break; }
            }

            if (target?.Value is float v && !float.IsNaN(v))
            {
                lock (syncLock) lastValidMap[key] = v;
                return v;
            }
            lock (syncLock) { if (lastValidMap.TryGetValue(key, out var last)) return last; }
            return null;
        }

        // ===========================================================
        // 流量累积与匹配 (原 HardwareMonitor.cs 核心逻辑)
        // ===========================================================
        private void AccumulateTraffic(IHardware hw, Settings cfg, double seconds)
        {
            // 1. 获取或创建当前硬件的独立状态
            if (!_netStates.TryGetValue(hw, out var state))
            {
                state = new NetworkState();
                _netStates[hw] = state;
            }

            long finalUp = 0;
            long finalDown = 0;

            // A. LHM 估算值
            if (state.CachedUpSensor == null || state.CachedDownSensor == null)
            {
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Throughput) continue;
                    if (_upKW.Any(k => SensorMap.Has(s.Name, k))) state.CachedUpSensor ??= s;
                    if (_downKW.Any(k => SensorMap.Has(s.Name, k))) state.CachedDownSensor ??= s;
                }
            }
            long lhmUpDelta = (long)((state.CachedUpSensor?.Value ?? 0) * seconds);
            long lhmDownDelta = (long)((state.CachedDownSensor?.Value ?? 0) * seconds);

            // B. 原生精准值
            MatchNativeNetworkAdapter(hw.Name, state);
            
            bool nativeValid = false;
            long nativeUpDelta = 0;
            long nativeDownDelta = 0;

            if (state.NativeAdapter != null)
            {
                try
                {
                    var stats = state.NativeAdapter.GetIPStatistics();
                    long currUp = stats.BytesSent;
                    long currDown = stats.BytesReceived;

                    if (currUp >= state.LastNativeUpload) nativeUpDelta = currUp - state.LastNativeUpload;
                    if (currDown >= state.LastNativeDownload) nativeDownDelta = currDown - state.LastNativeDownload;

                    state.LastNativeUpload = currUp;
                    state.LastNativeDownload = currDown;
                    nativeValid = true;
                }
                catch { state.NativeAdapter = null; }
            }

            // C. 决策时刻
            if (nativeValid)
            {
                if ((nativeUpDelta + nativeDownDelta == 0) && (lhmUpDelta + lhmDownDelta > 51200))
                {
                    // 匹配错误
                    finalUp = lhmUpDelta;
                    finalDown = lhmDownDelta;
                    state.NativeAdapter = null; 
                }
                else
                {
                    finalUp = nativeUpDelta;
                    finalDown = nativeDownDelta;
                }
            }
            else
            {
                finalUp = lhmUpDelta;
                finalDown = lhmDownDelta;
            }

            // D. 存入数据
            // ★★★ [新增] 安全阀：单次增量超过 10GB 视为异常丢弃 ★★★
            if (finalUp > 10737418240L || finalDown > 10737418240L) return;

            if (finalUp > 0 || finalDown > 0)
            {
                cfg.SessionUploadBytes += finalUp;
                cfg.SessionDownloadBytes += finalDown;
                TrafficLogger.AddTraffic(finalUp, finalDown);
            }
        }

        private void MatchNativeNetworkAdapter(string lhmName, NetworkState state)
        {
            if (state.NativeAdapter != null) return;
            if ((DateTime.Now - state.LastMatchAttempt).TotalSeconds < 10) return;
            state.LastMatchAttempt = DateTime.Now;

            try
            {
                var nics = NetworkInterface.GetAllNetworkInterfaces();
                
                // 预先分配令牌列表容量
                var lhmTokens = new List<string>(capacity: 10);
                SplitTokens(lhmName, lhmTokens);

                foreach (var nic in nics)
                {
                    // 1. 匹配连接名称
                    if (nic.Name.Equals(lhmName, StringComparison.OrdinalIgnoreCase)) { SetNativeAdapter(nic, state); return; }
                    // 2. 匹配硬件描述
                    if (nic.Description.Equals(lhmName, StringComparison.OrdinalIgnoreCase)) { SetNativeAdapter(nic, state); return; }
                    // 3. 模糊匹配
                    if (lhmTokens.Count > 0 && lhmName.Length > 5) 
                    {
                        var nicTokens = new List<string>(capacity: 10);
                        SplitTokens(nic.Description, nicTokens);
                        
                        // 优化匹配算法，减少内存分配
                        int matchCount = 0;
                        foreach (var token in lhmTokens)
                        {
                            if (nicTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                            {
                                matchCount++;
                                if (matchCount > 2) break; // 提前退出，满足条件即可
                            }
                        }
                        
                        if (matchCount > 2 && (double)matchCount / lhmTokens.Count > 0.6)
                        {
                            SetNativeAdapter(nic, state); 
                            return;
                        }
                    }
                }
            }
            catch { state.NativeAdapter = null; }
        }
        private static readonly char[] _tokenSeparators = { ' ', '(', ')', '[', ']', '-', '_', '#' };
        // 优化的SplitTokens方法，使用预分配的列表减少内存分配
        private void SplitTokens(string input, List<string> result)
        {
            result.Clear();
            int startIndex = 0;
            int length = input.Length;
            //char[] _tokenSeparators = { ' ', '(', ')', '[', ']', '-', '_', '#' };
            
            for (int i = 0; i < length; i++)
            {
                if (Array.IndexOf(_tokenSeparators, input[i]) >= 0)
                {
                    if (i > startIndex)
                    {
                        result.Add(UIUtils.Intern(input.Substring(startIndex, i - startIndex)));
                    }
                    startIndex = i + 1;
                }
            }
            
            if (startIndex < length)
            {
                result.Add(UIUtils.Intern(input.Substring(startIndex)));
            }
        }
        
        // 保持向后兼容性
        private List<string> SplitTokens(string input)
        {
            var result = new List<string>(capacity: 10);
            SplitTokens(input, result);
            return result;
        }

        private void SetNativeAdapter(NetworkInterface nic, NetworkState state)
        {
            state.NativeAdapter = nic;
            try
            {
                var stats = nic.GetIPStatistics();
                state.LastNativeUpload = stats.BytesSent;
                state.LastNativeDownload = stats.BytesReceived;
            }
            catch { state.NativeAdapter = null; }
        }

        private bool IsVirtualNetwork(string name)
        {
            foreach (var k in _virtualNicKW)
            {
                if (name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static readonly string[] _upKW = { "upload", "up", "sent", "send", "tx", "transmit" };
        private static readonly string[] _downKW = { "download", "down", "received", "receive", "rx" };
        private static readonly string[] _virtualNicKW = { "virtual", "vmware", "hyper-v", "hyper v", "vbox", "loopback", "tunnel", "tap", "tun", "bluetooth", "zerotier", "tailscale", "wan miniport" };
    }
}