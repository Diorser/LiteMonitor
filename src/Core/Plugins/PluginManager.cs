using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LiteMonitor;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.src.Core.Plugins
{
    /// <summary>
    /// 插件管理器 (核心单例)
    /// 负责插件的加载、生命周期管理、配置同步以及调度执行
    /// </summary>
    public class PluginManager
    {
        private static PluginManager _instance;
        public static PluginManager Instance => _instance ??= new PluginManager();

        // 模版库 (只读)
        private readonly List<PluginTemplate> _templates = new();
        
        // 运行中的定时器 (Key: InstanceId)
        private readonly Dictionary<string, System.Timers.Timer> _timers = new();
        
        // 执行引擎
        private readonly PluginExecutor _executor;

        public event Action OnPluginSchemaChanged;

        private PluginManager()
        {
            _executor = new PluginExecutor();
            // 转发执行引擎的 Schema 变更事件
            _executor.OnSchemaChanged += () => OnPluginSchemaChanged?.Invoke();
        }

        /// <summary>
        /// 从指定目录加载插件模版
        /// </summary>
        public void LoadPlugins(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                try { Directory.CreateDirectory(directoryPath); } catch { }
                return;
            }

            // 1. 加载模版 (不停止服务，只更新模版库)
            _templates.Clear();
            var files = Directory.GetFiles(directoryPath, "*.json");
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

            // 2. 自动同步逻辑：如果 Settings 里没有该模版的实例，自动创建一个默认实例
            var settings = Settings.Load();
            bool changed = false;
            foreach (var tmpl in _templates)
            {
                // 如果没有任何实例使用此模版
                if (!settings.PluginInstances.Any(x => x.TemplateId == tmpl.Id))
                {
                    // 创建默认实例
                    string newId = tmpl.Id;
                    if (settings.PluginInstances.Any(x => x.Id == newId))
                    {
                         newId = Guid.NewGuid().ToString("N").Substring(0, 8);
                    }

                    var newInst = new PluginInstanceConfig
                    {
                        Id = newId,
                        TemplateId = tmpl.Id,
                        Enabled = true
                    };
                    
                    // 填入默认参数
                    foreach(var input in tmpl.Inputs)
                    {
                        newInst.InputValues[input.Key] = input.DefaultValue;
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

        /// <summary>
        /// 使用指定的配置重新加载所有实例 (通常在设置变更后调用)
        /// </summary>
        public void Reload(Settings cfg)
        {
            Stop();
            _executor.ClearCache(); // ★★★ 关键：清除旧缓存，确保参数变更立即生效 ★★★
            
            foreach (var inst in cfg.PluginInstances)
            {
                if (!inst.Enabled) continue;

                var tmpl = _templates.FirstOrDefault(x => x.Id == inst.TemplateId);
                if (tmpl == null) continue;

                SyncMonitorItem(inst); 
                StartInstance(inst, tmpl);
            }
        }

        /// <summary>
        /// 启动所有启用的插件实例
        /// </summary>
        public void Start()
        {
            Stop(); // 先停止所有旧任务

            var settings = Settings.Load();
            foreach (var inst in settings.PluginInstances)
            {
                if (!inst.Enabled) continue;

                var tmpl = _templates.FirstOrDefault(x => x.Id == inst.TemplateId);
                if (tmpl == null) continue;

                SyncMonitorItem(inst); // 确保 MonitorItem 存在且是最新的
                StartInstance(inst, tmpl);
            }
        }
        
        /// <summary>
        /// 重启单个实例 (用户修改配置后调用)
        /// </summary>
        public void RestartInstance(string instanceId)
        {
            // 停止旧的
            if (_timers.TryGetValue(instanceId, out var timer))
            {
                timer.Stop();
                timer.Dispose();
                _timers.Remove(instanceId);
            }
            
            var settings = Settings.Load();
            var inst = settings.PluginInstances.FirstOrDefault(x => x.Id == instanceId);
            if (inst == null || !inst.Enabled) return; // 没找到或已禁用
            
            var tmpl = _templates.FirstOrDefault(x => x.Id == inst.TemplateId);
            if (tmpl == null) return;
            
            SyncMonitorItem(inst); 
            StartInstance(inst, tmpl);
        }

        public void RemoveInstance(string instanceId)
        {
            // 1. Stop timer
            if (_timers.TryGetValue(instanceId, out var timer))
            {
                timer.Stop();
                timer.Dispose();
                _timers.Remove(instanceId);
            }

            // 2. Remove MonitorItem(s)
            var settings = Settings.Load();
            string mainKey = "DASH." + instanceId;
            var itemsToRemove = settings.MonitorItems.Where(x => x.Key == mainKey || x.Key.StartsWith(mainKey + ".")).ToList();
            
            if (itemsToRemove.Count > 0)
            {
                foreach(var item in itemsToRemove)
                {
                    settings.MonitorItems.Remove(item);
                }
                settings.Save();
            }
        }

        public void Stop()
        {
            foreach (var t in _timers.Values)
            {
                t.Stop();
                t.Dispose();
            }
            _timers.Clear();
        }

        private void StartInstance(PluginInstanceConfig inst, PluginTemplate tmpl)
        {
             // 立即执行一次
            Task.Run(() => _executor.ExecuteInstanceAsync(inst, tmpl));

            // 设定定时器
            int interval = inst.CustomInterval > 0 ? inst.CustomInterval : tmpl.Execution.Interval;
            if (interval < 1000) interval = 1000; // 最低 1秒

            var newTimer = new System.Timers.Timer(interval);
            newTimer.Elapsed += (s, e) => _executor.ExecuteInstanceAsync(inst, tmpl);
            newTimer.Start();
            
            _timers[inst.Id] = newTimer;
        }

        /// <summary>
        /// 同步监控项配置 (Sync MonitorItem)
        /// 根据插件配置生成或更新 MonitorItem，确保 UI 能显示
        /// </summary>
        public void SyncMonitorItem(PluginInstanceConfig inst)
        {
            var settings = Settings.Load();
            var tmpl = _templates.FirstOrDefault(x => x.Id == inst.TemplateId);
            if (tmpl == null) return;
            
            bool changed = false;

            // 确定目标 (Targets)
            var targets = inst.Targets != null && inst.Targets.Count > 0 ? inst.Targets : new List<Dictionary<string, string>> { new Dictionary<string, string>() };
            var validKeys = new HashSet<string>();

            for (int i = 0; i < targets.Count; i++)
            {
                var targetInputs = targets[i];
                // 合并 Inputs
                var mergedInputs = new Dictionary<string, string>(inst.InputValues);
                foreach (var kv in targetInputs) mergedInputs[kv.Key] = kv.Value;
                
                // 补全默认值 (用于 Label 预览替换)
                foreach (var input in tmpl.Inputs)
                    if (!mergedInputs.ContainsKey(input.Key))
                        mergedInputs[input.Key] = input.DefaultValue;

                // [Fix] Issue 2: 处理空变量导致的 Label 异常 (如 " Temp")
                // 如果是天气插件且 city 为空，说明是 Auto 模式，给一个临时占位符 "Local" 或 "Auto"
                // 这样 {{city}} Temp 就会变成 "Auto Temp" 而不是 " Temp"
                // 更好的做法是检查 mergedInputs 中的空值，如果有空值，且在 labelPattern 中被引用，则替换为 "Auto"
                if (tmpl.Inputs != null)
                {
                    foreach (var input in tmpl.Inputs)
                    {
                         if (input.Key == "city" && string.IsNullOrEmpty(mergedInputs[input.Key]))
                         {
                             mergedInputs[input.Key] = "Auto"; // 仅用于 Label 预览
                         }
                    }
                }

                string keySuffix = (inst.Targets != null && inst.Targets.Count > 0) ? $".{i}" : "";

                // 注册所有可能的 Output Key
                if (tmpl.Outputs != null)
                {
                    foreach (var output in tmpl.Outputs)
                    {
                        string itemKey = "DASH." + inst.Id + keySuffix + "." + output.Key;
                        validKeys.Add(itemKey);

                        // [Fix] Issue 1: 立即重置 UI 值，防止显示旧数据 (如 "25°C")
                        // 使用 "..." 表示正在加载
                        InfoService.Instance.InjectValue(itemKey, "...");

                        var item = settings.MonitorItems.FirstOrDefault(x => x.Key == itemKey);
                        
                        string labelPattern = output.Label;
                        if (string.IsNullOrEmpty(labelPattern)) labelPattern = (tmpl.Meta.Name) + " " + output.Key;
                        
                        // 使用 PluginProcessor 解析变量
                        string finalName = PluginProcessor.ResolveTemplate(labelPattern, mergedInputs);
                        string finalShort = PluginProcessor.ResolveTemplate(output.ShortLabel, mergedInputs);

                        if (item == null)
                        {
                            item = new MonitorItemConfig
                            {
                                Key = itemKey,
                                UserLabel = finalName,
                                TaskbarLabel = finalShort,
                                UnitPanel = output.Unit,
                                VisibleInPanel = true,
                                SortIndex = -1,
                            };
                            settings.MonitorItems.Add(item);
                            changed = true;
                        }
                        else
                        {
                            // [Fix] 即使 Item 已存在，也需要更新 Label，以便在 API 请求返回前就显示正确的标题 (如 "北京 气温")
                            // 否则用户会看到旧的标题 (如 "深圳 气温") 直到 2秒后数据刷新
                            // 注意：这会覆盖用户的自定义重命名，但对于插件产生的动态 Label，这通常是期望的行为
                            if (item.UserLabel != finalName) 
                            { 
                                item.UserLabel = finalName; 
                                changed = true; 
                            }
                            if (item.TaskbarLabel != finalShort) 
                            { 
                                item.TaskbarLabel = finalShort; 
                                changed = true; 
                            }

                            if (item.UnitPanel != output.Unit) { item.UnitPanel = output.Unit; changed = true; }
                        }

                        // 更新 Label
                        // DynamicLabels 已废弃，直接使用 Output.Label 即可
                        // 实际 Update 在 PluginExecutor 中根据运行时数据进行 set value
                    }
                }
            }

            // 清理已废弃的 Item (例如减少了 Target 数量)
            var toRemove = settings.MonitorItems
                .Where(x => x.Key.StartsWith("DASH." + inst.Id + ".") && !validKeys.Contains(x.Key))
                .ToList();

            foreach (var item in toRemove)
            {
                settings.MonitorItems.Remove(item);
                changed = true;
            }

            if (changed) settings.Save();
        }
    }
}
