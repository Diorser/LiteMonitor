using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json; // [Fix] Added missing using
using System.Buffers; // [Optimization] Use ArrayPool
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.SystemServices.InfoService;
using System.Diagnostics;

namespace LiteMonitor.src.WebServer
{
    public class LiteWebServer : IDisposable
    {
        private TcpListener? _listener;
        private volatile bool _isRunning = false;
        private int _currentRunningPort = -1;

        public string CurrentPassword { get;set; }

        private readonly Settings _cfg;
        
        // [Refactor] Decoupled WebSocket Logic
        private readonly WebSessionManager _sessionManager;
        
        public static LiteWebServer? Instance { get; private set; }

        public bool IsRunning => _isRunning;
        public int CurrentRunningPort => _isRunning ? _currentRunningPort : -1;

        public LiteWebServer(Settings cfg)
        {
            _cfg = cfg;
            Instance = this;
            // Initialize WebSocket Manager with the data provider delegate
            _sessionManager = new WebSessionManager(WriteSnapshotJson);
        }

        public bool Start(out string errorMsg)
        {
            errorMsg = "";
            if (_isRunning) return true;
            if (!_cfg.WebServerEnabled) return true;

            try
            {
                int port = _cfg.WebServerPort;

                try
                {
                    // [Fix] 优先尝试绑定 IPv6Any 并开启 DualMode，以同时支持 IPv4 和 IPv6
                    _listener = new TcpListener(IPAddress.IPv6Any, port);
                    _listener.Server.DualMode = true;
                    _listener.Start();
                }
                catch (Exception)
                {
                    // 如果系统不支持 IPv6，回退到 IPv4
                    _listener = new TcpListener(IPAddress.Any, port);
                    _listener.Start();
                }

                _isRunning = true;
                _currentRunningPort = port;
                CurrentPassword = _cfg.WebServerPassword;

                // 1. 启动监听连接的循环
                Task.Run(ListenLoop);
                
                // 2. 启动 WebSocket 管理器 (广播循环在内部)
                _sessionManager.Start();
                
                return true;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                Debug.WriteLine("WebServer Start Error: " + ex.Message);
                return false;
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _currentRunningPort = -1;
            try { _listener?.Stop(); } catch { }
            
            // 停止 WebSocket 管理器
            _sessionManager.Stop();
        }

        public void Dispose()
        {
            Stop();
        }

        private async Task ListenLoop()
        {
            while (_isRunning && _listener != null)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    // [Refactor] All connection handling logic moved to WebSessionManager (now serving as unified ConnectionManager)
                    _ = _sessionManager.HandleClientAsync(client);
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { Debug.WriteLine(ex.Message); }
            }
        }

        private void WriteSnapshotJson(Utf8JsonWriter writer)
        {
            var hw = HardwareMonitor.Instance;
            if (hw == null)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
                return;
            }

            // ★★★ 修复：创建列表副本并加锁，防止遍历时 UI 线程修改集合导致崩溃 ★★★
            List<MonitorItemConfig> itemsCopy;
            Dictionary<string, int> groupIndices = new Dictionary<string, int>();
            
            lock (_cfg.MonitorItems)
            {
                // ★★★ 修复：复用主界面 (Panel模式) 的排序逻辑 ★★★
                itemsCopy = _cfg.MonitorItems
                    .GroupBy(x => x.UIGroup)
                    .OrderBy(g => g.Min(x => x.SortIndex))
                    .SelectMany(g => g.OrderBy(x => x.SortIndex))
                    .ToList();

                // 预计算组顺序索引 (基于排序后的列表)
                int gIdx = 0;
                foreach(var item in itemsCopy)
                {
                    if (!groupIndices.ContainsKey(item.UIGroup))
                    {
                        groupIndices[item.UIGroup] = gIdx++;
                    }
                }
            }

            string localIp = hw.GetNetworkIP() ?? "127.0.0.1";

            writer.WriteStartObject();

            // "sys" object
            writer.WriteStartObject("sys");
            writer.WriteString("ip", localIp);
            writer.WriteNumber("port", _currentRunningPort);
            writer.WriteString("uptime", (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(@"hh\:mm\:ss"));
            writer.WriteEndObject();

            // "items" array
            writer.WriteStartArray("items");

            foreach (var item in itemsCopy)
            {
                string displayName;
                // [Fix] WebUI should also use MetricLabelResolver to show dynamic names
                string resolved = MetricLabelResolver.ResolveLabel(item);
                if (!string.IsNullOrEmpty(resolved))
                {
                    displayName = resolved;
                }
                else
                {
                    // [Optimization] Use cached keys from MonitorItemConfig
                    string itemsKey = !string.IsNullOrEmpty(item.CachedItemsKey) ? item.CachedItemsKey : UIUtils.Intern("Items." + item.Key);
                    displayName = LanguageManager.T(itemsKey);
                    // 如果翻译失败 (fallback to key suffix)
                    if (displayName.StartsWith("Items.")) displayName = displayName.Split('.')[1];
                }
                
                string groupId = item.UIGroup.ToUpper();
                // [Optimization] Use cached keys
                string groupsKey = !string.IsNullOrEmpty(item.CachedGroupsKey) ? item.CachedGroupsKey : UIUtils.Intern("Groups." + item.UIGroup);
                string groupDisplay = LanguageManager.T(groupsKey);

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
                    // [Optimization] Use cached Dash keys
                    string colorKey = !string.IsNullOrEmpty(item.CachedDashColorKey) ? item.CachedDashColorKey : dashKey + ".Color";
                    string colorStr = InfoService.Instance.GetValue(colorKey);
                    if (int.TryParse(colorStr, out int cVal)) status = cVal;

                    // 读取插件写入的单位 (.Unit)
                    string unitKey = !string.IsNullOrEmpty(item.CachedDashUnitKey) ? item.CachedDashUnitKey : dashKey + ".Unit";
                    string unitStr = InfoService.Instance.GetValue(unitKey);
                    if (!string.IsNullOrEmpty(unitStr)) unit = unitStr;
                }
                else
                {
                    // ★★★ [Fix BF6] 可见性门控：仅当监控项在桌面端（面板/任务栏）可见时才向采集层取值。
                    // 否则即使 FPS 在所有界面隐藏，网页快照也会每秒触发 FpsCounter.GetFps()
                    // 惰性启动 PresentMon ETW 会话（该会话追踪受反作弊保护的游戏会导致游戏闪退） ★★★
                    bool visibleAnywhere = item.VisibleInPanel || item.VisibleInTaskbar;
                    float? val = visibleAnywhere ? hw.Get(item.Key) : (float?)null;

                    // [Fix] 恢复过滤逻辑：跳过没有数据的监控项（如台式机的电池），以符合业务屏蔽需求
                    // 布局顺序问题已由 CSS order (gidx) 机制解决，即使数据晚到也会插入正确位置
                    if (!val.HasValue) continue;

                    if (val.HasValue)
                    {
                        // [Refactor] 使用 MetricUtils 统一格式化逻辑，确保与桌面端完全一致 (包括单位换算、颜色阈值、内存显示模式)
                        valStr = MetricUtils.GetValueStr(item.Key, val.Value, false, item.UnitPanel); // false = Panel Context (Standard)
                        string calculatedUnit = MetricUtils.GetUnitStr(item.Key, val.Value, MetricUtils.UnitContext.Panel);
                        unit = MetricUtils.GetDisplayUnit(item.Key, calculatedUnit, item.UnitPanel);
                        
                        // GetProgressValue 返回 0.0-1.0，Web端通常需要 0-100
                        pct = MetricUtils.GetProgressValue(item.Key, val.Value) * 100.0;
                        
                        // 状态判断 (0=Safe, 1=Warn, 2=Crit)
                        status = MetricUtils.GetState(item.Key, val.Value);
                    }
                
                    // Primary Logic
                    if (item.Key == "BAT.Percent") isPrimary = true;
                    else if (item.Key == "CPU.Load") isPrimary = true;
                    else if (item.Key == "MEM.Load") isPrimary = true;
                    else if (item.Key == "GPU.Core.Load" || item.Key == "GPU.Load") isPrimary = true;
                    else isPrimary = false;
                }

                if (string.IsNullOrEmpty(valStr)) valStr = "--";
                
                writer.WriteStartObject();
                writer.WriteString("k", item.Key);
                writer.WriteString("n", displayName);
                writer.WriteString("gid", groupId);
                writer.WriteString("gn", groupDisplay);
                // ★★★ 新增：发送组顺序索引，供前端 CSS order 使用 ★★★
                writer.WriteNumber("gidx", groupIndices.TryGetValue(item.UIGroup, out int idx) ? idx : 999);
                writer.WriteString("v", valStr);
                writer.WriteString("u", unit);
                writer.WriteNumber("pct", pct);
                writer.WriteNumber("sts", status);
                writer.WriteBoolean("primary", isPrimary);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }
}
