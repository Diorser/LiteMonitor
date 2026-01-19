using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LiteMonitor;

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
        private readonly Dictionary<string, System.Timers.Timer> _timers = new();
        private readonly Dictionary<string, System.Threading.CancellationTokenSource> _cts = new();
        private readonly Dictionary<string, string> _configSnapshots = new();
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

            // 2. 自动同步逻辑 (确保新插件出现在配置中)
            var settings = Settings.Load();
            bool changed = false;
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

        public void Reload(Settings cfg)
        {
            // 1. Identify active instances
            var currentIds = _timers.Keys.ToList();
            var newInstances = cfg.PluginInstances.Where(x => x.Enabled).ToDictionary(x => x.Id);

            // 2. Process Removals
            foreach (var id in currentIds)
            {
                if (!newInstances.ContainsKey(id))
                {
                    StopInstance(id);
                    _configSnapshots.Remove(id);
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
                        PluginMonitorSyncService.Instance.SyncMonitorItem(inst, tmpl);
                        StartInstance(inst, tmpl);
                        _configSnapshots[inst.Id] = newHash;
                    }
                }
            }
        }
        
        private string GetConfigHash(PluginInstanceConfig inst)
        {
            try { return JsonSerializer.Serialize(inst); } catch { return ""; }
        }

        private void StopInstance(string instanceId)
        {
            if (_timers.TryGetValue(instanceId, out var timer))
            {
                timer.Stop();
                timer.Dispose();
                _timers.Remove(instanceId);
            }

            if (_cts.TryGetValue(instanceId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _cts.Remove(instanceId);
            }
        }

        public void Start()
        {
            Stop(); 
            _configSnapshots.Clear();

            var settings = Settings.Load();
            foreach (var inst in settings.PluginInstances)
            {
                if (!inst.Enabled) continue;

                var tmpl = _templates.FirstOrDefault(x => x.Id == inst.TemplateId);
                if (tmpl == null) continue;

                PluginMonitorSyncService.Instance.SyncMonitorItem(inst, tmpl);
                StartInstance(inst, tmpl);
                _configSnapshots[inst.Id] = GetConfigHash(inst);
            }
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
                PluginMonitorSyncService.Instance.RemoveMonitorItems(instanceId);
                return;
            }
            
            var tmpl = _templates.FirstOrDefault(x => x.Id == inst.TemplateId);
            if (tmpl == null) return;
            
            PluginMonitorSyncService.Instance.SyncMonitorItem(inst, tmpl);
            StartInstance(inst, tmpl);
            _configSnapshots[inst.Id] = GetConfigHash(inst);
        }

        public void RemoveInstance(string instanceId)
        {
            StopInstance(instanceId);
            _configSnapshots.Remove(instanceId);

            PluginMonitorSyncService.Instance.RemoveMonitorItems(instanceId);
        }

        public void Stop()
        {
            foreach (var t in _timers.Values)
            {
                t.Stop();
                t.Dispose();
            }
            _timers.Clear();

            foreach (var cts in _cts.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _cts.Clear();
        }

        private void StartInstance(PluginInstanceConfig inst, PluginTemplate tmpl)
        {
            var cts = new System.Threading.CancellationTokenSource();
            _cts[inst.Id] = cts;

            // 立即执行一次
            Task.Run(() => _executor.ExecuteInstanceAsync(inst, tmpl, cts.Token));

            // 设定间隔 (单位：秒)
            int interval = inst.CustomInterval > 0 ? inst.CustomInterval : tmpl.Execution.Interval;
            if (interval < PluginConstants.DEFAULT_INTERVAL) interval = PluginConstants.DEFAULT_INTERVAL;

            var newTimer = new System.Timers.Timer(interval * 1000); 
            newTimer.AutoReset = false; // Stop-Wait 模式
            
            newTimer.Elapsed += async (s, e) => 
            {
                if (cts.IsCancellationRequested) return;
                try 
                {
                    // Execute and get success status
                    bool success = await _executor.ExecuteInstanceAsync(inst, tmpl, cts.Token);
                    
                    // 动态调整间隔：如果失败，使用快速重试间隔 (5s)；如果成功，恢复正常间隔
                    if (!success)
                    {
                        newTimer.Interval = 5000; // 5 seconds retry
                    }
                    else
                    {
                        newTimer.Interval = interval * 1000;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Timer execution failed: {ex.Message}");
                    newTimer.Interval = 5000; // Exception case also fast retry
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
        }

        // 委托给 Service 的 UI 辅助方法
        public string TryGetSmartLabel(string itemKey, string targetField = "label")
        {
            return PluginMonitorSyncService.Instance.TryGetSmartLabel(itemKey, _templates, targetField);
        }
    }
}
