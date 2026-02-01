using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LiteMonitor;
using LiteMonitor.src.Core;
using LiteMonitor.src.WebServer;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 插件管理器 (Refactored)
    /// 负责插件的加载、生命周期管理以及调度执行
    /// </summary>
    public class PluginManager
    {
        private static PluginManager _instance;
        public static PluginManager Instance => _instance ??= new PluginManager();

        private readonly List<PluginTemplate> _templates = new();
        // [Fix] 使用 ConcurrentDictionary 替代 Dictionary，确保线程安全
        private readonly ConcurrentDictionary<string, System.Timers.Timer> _timers = new();
        private readonly ConcurrentDictionary<string, System.Threading.CancellationTokenSource> _cts = new();
        private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();
        private readonly ConcurrentDictionary<string, string> _configSnapshots = new();
        private readonly PluginExecutor _executor;

        public event Action OnPluginSchemaChanged;

        private PluginManager()
        {
            _executor = new PluginExecutor();
            _executor.OnSchemaChanged += () => OnPluginSchemaChanged?.Invoke();
        }

        public void LoadPlugins(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                try { Directory.CreateDirectory(directoryPath); } catch { }
                return;
            }

            // 1. 加载模版
            _templates.Clear();
            var files = Directory.GetFiles(directoryPath, PluginConstants.CONFIG_EXT);
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var tmpl = JsonSerializer.Deserialize<PluginTemplate>(json);
                    if (tmpl != null && !string.IsNullOrEmpty(tmpl.Id))
                    {
                        _templates.Add(tmpl);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load plugin {file}: {ex.Message}");
                }
            }

            // 2. 自动同步逻辑 (确保新插件出现在配置中，且已删除的插件被清理)
            var settings = Settings.Load();
            bool changed = false;

            // [新增] 清理失效的插件实例 (Template 不存在的)
            var instancesToRemove = new List<PluginInstanceConfig>();
            foreach (var inst in settings.PluginInstances)
            {
                if (!_templates.Any(t => t.Id == inst.TemplateId))
                {
                    instancesToRemove.Add(inst);
                }
            }

            foreach (var inst in instancesToRemove)
            {
                settings.PluginInstances.Remove(inst);
                // 同时清理关联的 MonitorItems
                PluginMonitorSyncService.Instance.RemoveMonitorItems(inst.Id, saveIfChanged: false);
                changed = true;
            }

            // [原有] 添加新插件实例
            foreach (var tmpl in _templates)
            {
                if (!settings.PluginInstances.Any(x => x.TemplateId == tmpl.Id))
                {
                    string newId = tmpl.Id;
                    if (settings.PluginInstances.Any(x => x.Id == newId))
                    {
                         newId = Guid.NewGuid().ToString("N").Substring(0, 8);
                    }

                    var newInst = new PluginInstanceConfig
                    {
                        Id = newId,
                        TemplateId = tmpl.Id,
                        Enabled = false
                    };
                    
                    if (tmpl.Inputs != null)
                    {
                        foreach(var input in tmpl.Inputs)
                        {
                            newInst.InputValues[input.Key] = input.DefaultValue;
                        }
                    }
                    
                    settings.PluginInstances.Add(newInst);
                    changed = true;
                }
            }
            if (changed) settings.Save();
        }

        public List<PluginTemplate> GetAllTemplates()
        {
            return _templates;
        }

        private void CleanupPluginData(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return;
            
            // 1. Remove raw values injected by plugin (Key starts with instanceId)
            InfoService.Instance.RemoveDataByPrefix(instanceId);
            
            // 2. Remove dynamic properties (Label, ShortLabel, etc.)
            // Keys are like PROP.Label.DASH.{instanceId}.{OutputKey}
            InfoService.Instance.RemoveDataByPrefix("PROP.Label.DASH." + instanceId);
            InfoService.Instance.RemoveDataByPrefix("PROP.ShortLabel.DASH." + instanceId);
            InfoService.Instance.RemoveDataByPrefix("PROP.Unit.DASH." + instanceId); // If we add Unit support later
        }

        public void Reload(Settings cfg)
        {
            // 1. Identify active instances
            // [Fix] 使用 Keys 的快照，ConcurrentDictionary.Keys 返回的是即时快照
            var currentIds = _timers.Keys.ToList();
            var newInstances = cfg.PluginInstances.Where(x => x.Enabled).ToDictionary(x => x.Id);

            // 2. Process Removals
            foreach (var id in currentIds)
            {
                if (!newInstances.ContainsKey(id))
                {
                    StopInstance(id);
                    _configSnapshots.TryRemove(id, out _);
                    CleanupPluginData(id);
                }
            }

            // 3. Process Additions & Updates
            foreach (var kv in newInstances)
            {
                var inst = kv.Value;
                string newHash = GetConfigHash(inst);
                bool needsRestart = false;

                if (_timers.ContainsKey(inst.Id))
                {
                    // Exists. Check if changed.
                    if (_configSnapshots.TryGetValue(inst.Id, out var oldHash) && oldHash == newHash)
                    {
                        continue; // Stable, skip
                    }
                    needsRestart = true;
                    StopInstance(inst.Id);
                }
                else
                {
                    needsRestart = true;
                }

                if (needsRestart)
                {
                    var tmpl = _templates.FirstOrDefault(x => x.Id == inst.TemplateId);
                    if (tmpl != null)
                    {
                        
                        PluginMonitorSyncService.Instance.SyncMonitorItem(inst, tmpl, saveIfChanged: false);
                        StartInstance(inst, tmpl);
                        _configSnapshots[inst.Id] = newHash;
                    }
                }
            }
            
            // Force save once if needed, or rely on caller? 
            // Caller (SettingsForm) already saves. But SyncMonitorItem might have added new items to Settings.
            // Let's save just in case structure changed.
            Settings.Load().Save();
        }
        
        private string GetConfigHash(PluginInstanceConfig inst)
        {
            try { return JsonSerializer.Serialize(inst); } catch { return ""; }
        }

        private void StopInstance(string instanceId)
        {
            // [Fix] 使用 TryRemove 替代 TryGetValue + Remove 以确保线程安全
            if (_timers.TryRemove(instanceId, out var timer))
            {
                timer.Stop();
                timer.Dispose();
            }

            if (_cts.TryRemove(instanceId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            // Reset failure count
            _consecutiveFailures.TryRemove(instanceId, out _);
        }

        public void Start()
        {
            Stop(); 
            _configSnapshots.Clear();

            var settings = Settings.Load();
            bool anyChange = false;
            
            foreach (var inst in settings.PluginInstances)
            {
                if (!inst.Enabled) continue;

                var tmpl = _templates.FirstOrDefault(x => x.Id == inst.TemplateId);
                if (tmpl == null) continue;

                bool changed = PluginMonitorSyncService.Instance.SyncMonitorItem(inst, tmpl, saveIfChanged: false);
                if (changed) anyChange = true;
                
                StartInstance(inst, tmpl);
                _configSnapshots[inst.Id] = GetConfigHash(inst);
            }
            
            if (anyChange) settings.Save();
        }
        
        public void RestartInstance(string instanceId, PluginInstanceConfig configOverride = null)
        {
            StopInstance(instanceId);
            
            PluginInstanceConfig inst;
            if (configOverride != null)
            {
                inst = configOverride;
            }
            else
            {
                var settings = Settings.Load();
                inst = settings.PluginInstances.FirstOrDefault(x => x.Id == instanceId);
            }
            
            if (inst == null || !inst.Enabled)
            {
                // Clean up items if disabled
                PluginMonitorSyncService.Instance.RemoveMonitorItems(instanceId, saveIfChanged: true);
                return;
            }
            
            var tmpl = _templates.FirstOrDefault(x => x.Id == inst.TemplateId);
            if (tmpl == null) return;
            
            PluginMonitorSyncService.Instance.SyncMonitorItem(inst, tmpl, saveIfChanged: true);
            StartInstance(inst, tmpl);
            _configSnapshots[inst.Id] = GetConfigHash(inst);
        }

        public void RemoveInstance(string instanceId)
        {
            StopInstance(instanceId);
            _configSnapshots.TryRemove(instanceId, out _);
            CleanupPluginData(instanceId);

            PluginMonitorSyncService.Instance.RemoveMonitorItems(instanceId, saveIfChanged: true);
        }

        public void Stop()
        {
            // [Fix] 使用安全方式遍历并清理
            foreach (var key in _timers.Keys.ToList())
            {
                if (_timers.TryRemove(key, out var t))
                {
                    t.Stop();
                    t.Dispose();
                }
            }

            foreach (var key in _cts.Keys.ToList())
            {
                if (_cts.TryRemove(key, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                }
            }
        }

        private bool IsPluginVisible(string instanceId)
        {
            var settings = Settings.Load();
            if (settings?.MonitorItems == null) return false;

            // 1. Web Server Check
            if (LiteWebServer.Instance != null && LiteWebServer.Instance.IsRunning) return true;

            // 2. Monitor Items Visibility Check
            string prefix = PluginConstants.DASH_PREFIX + instanceId + ".";
            lock (settings.MonitorItems)
            {
                foreach (var item in settings.MonitorItems)
                {
                    if (item.Key.StartsWith(prefix))
                    {
                        if (item.VisibleInPanel || item.VisibleInTaskbar) return true;
                    }
                }
            }
            return false;
        }

        private void StartInstance(PluginInstanceConfig inst, PluginTemplate tmpl)
        {
            var cts = new System.Threading.CancellationTokenSource();
            _cts[inst.Id] = cts;

            // 设定间隔 (单位：秒)
            int interval = inst.CustomInterval > 0 ? inst.CustomInterval : tmpl.Execution.Interval;
            if (interval < PluginConstants.DEFAULT_INTERVAL) interval = PluginConstants.DEFAULT_INTERVAL;

            // 使用 Timer 统一调度：初始间隔设为极短(例如 50ms)以触发立即执行
            // 这样第一次执行的结果也能被捕获，从而触发失败重试逻辑
            var newTimer = new System.Timers.Timer(50); 
            newTimer.AutoReset = false; // Stop-Wait 模式
            
            newTimer.Elapsed += async (s, e) => 
            {
                if (cts.IsCancellationRequested) return;

                // [Optimization] On-Demand Update: Skip if not visible
                if (!IsPluginVisible(inst.Id))
                {
                    newTimer.Interval = 2000; // Check again in 2s
                    return;
                }

                try 
                {
                    // Execute and get success status
                    bool success = await _executor.ExecuteInstanceAsync(inst, tmpl, cts.Token);
                    
                    // 动态调整间隔：如果失败，使用快速重试间隔 (5s)；如果成功，恢复正常间隔
                    if (!success)
                    {
                        // 失败逻辑：线性退避 (5s, 10s, 15s... max 60s)
                        int fails = _consecutiveFailures.AddOrUpdate(inst.Id, 1, (k, v) => v + 1);
                        int backoff = Math.Min(fails * PluginConstants.RETRY_INTERVAL_MS, 60000);
                        
                        // [Fix] 连续失败达到一定次数时，尝试重置网络客户端，以应对网络环境切换(如VPN/代理)导致的连接僵死
                        // [Optimization] Reduced threshold from 5 to 3 to recover faster from network changes
                        if (fails >= 3 && fails % 3 == 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"Plugin {inst.Id} failed {fails} times. Resetting network clients and clearing cache.");
                            _executor.ResetNetworkClients();
                            _executor.ClearCache(inst.Id);
                        }

                        newTimer.Interval = backoff;
                        System.Diagnostics.Debug.WriteLine($"Plugin {inst.Id} failed ({fails} times). Retry in {backoff}ms.");
                    }
                    else
                    {
                        // 成功：重置失败计数
                        if (_consecutiveFailures.ContainsKey(inst.Id)) _consecutiveFailures.TryRemove(inst.Id, out _);
                        newTimer.Interval = interval * 1000;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Timer execution failed: {ex.Message}");
                    
                    // 异常逻辑：同样应用退避
                    int fails = _consecutiveFailures.AddOrUpdate(inst.Id, 1, (k, v) => v + 1);
                    int backoff = Math.Min(fails * PluginConstants.RETRY_INTERVAL_MS, 60000);// 最大 60s
                    newTimer.Interval = backoff;
                }
                finally 
                {
                    if (_timers.ContainsKey(inst.Id) && inst.Enabled && !cts.IsCancellationRequested)
                    {
                        try { newTimer.Start(); } catch {} 
                    }
                }
            };
            
            newTimer.Start();
            _timers[inst.Id] = newTimer;
            
            // ★★★ Inject Initial Loading State ★★★
            InitDefaultValues(inst, tmpl);
        }

        private void InitDefaultValues(PluginInstanceConfig inst, PluginTemplate tmpl)
        {
            if (tmpl.Outputs == null) return;
            
            // Logic must match PluginExecutor to generate correct keys
            int targetCount = (inst.Targets != null && inst.Targets.Count > 0) ? inst.Targets.Count : 1;
            bool hasTargets = (inst.Targets != null && inst.Targets.Count > 0);

            for (int i = 0; i < targetCount; i++)
            {
                string keySuffix = hasTargets ? $".{i}" : "";
                
                foreach (var output in tmpl.Outputs)
                {
                    string injectKey = inst.Id + keySuffix + "." + output.Key;
                    
                    // Only inject if currently empty to avoid flashing "..." on reload if data exists
                    if (string.IsNullOrEmpty(InfoService.Instance.GetValue(injectKey)))
                    {
                        InfoService.Instance.InjectValue(injectKey, PluginConstants.STATUS_LOADING);
                    }
                }
            }
        }

        // 委托给 Service 的 UI 辅助方法
        public string TryGetSmartLabel(string itemKey, string targetField = "label")
        {
            return PluginMonitorSyncService.Instance.TryGetSmartLabel(itemKey, _templates, targetField);
        }
    }
}
