using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 插件监控项同步服务
    /// 负责将插件配置同步到全局 MonitorItems 设置中
    /// </summary>
    public class PluginMonitorSyncService
    {
        private static PluginMonitorSyncService _instance;
        public static PluginMonitorSyncService Instance => _instance ??= new PluginMonitorSyncService();

        private PluginMonitorSyncService() { }

        /// <summary>
        /// 同步指定插件实例的监控项到全局设置
        /// </summary>
        /// <param name="inst">插件实例配置</param>
        /// <param name="tmpl">插件模板</param>
        public void SyncMonitorItem(PluginInstanceConfig inst, PluginTemplate tmpl)
        {
            if (inst == null || tmpl == null) return;

            // 优化 1: 正则预编译 (Static Readonly)
            // 移至类级别定义
            
            var settings = Settings.Load();
            bool changed = false;

            var targets = inst.Targets != null && inst.Targets.Count > 0 ? inst.Targets : new List<Dictionary<string, string>> { new Dictionary<string, string>() };
            var validKeys = new HashSet<string>();

            // 优化 2: 高风险并发崩溃修复 - 加锁
            lock (settings.MonitorItems)
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    // ... (保持原有循环逻辑，但被锁保护)
                    var targetInputs = targets[i];
                    var mergedInputs = new Dictionary<string, string>(inst.InputValues);
                    
                    // Merge Target Inputs
                    foreach (var kv in targetInputs) mergedInputs[kv.Key] = kv.Value;

                    // Apply Defaults
                    if (tmpl.Inputs != null)
                    {
                        foreach (var input in tmpl.Inputs)
                        {
                            if (input.DefaultValue != null && (!mergedInputs.ContainsKey(input.Key) || string.IsNullOrEmpty(mergedInputs[input.Key])))
                            {
                                mergedInputs[input.Key] = input.DefaultValue;
                            }
                        }
                    }

                    string keySuffix = (inst.Targets != null && inst.Targets.Count > 0) ? $".{i}" : "";

                    if (tmpl.Outputs != null)
                    {
                        foreach (var output in tmpl.Outputs)
                        {
                            string itemKey = PluginConstants.DASH_PREFIX + inst.Id + keySuffix + "." + output.Key;
                            validKeys.Add(itemKey);

                            // 注入 Loading 状态 (仅当当前无值时)
                            if (string.IsNullOrEmpty(InfoService.Instance.GetValue(itemKey)))
                            {
                                InfoService.Instance.InjectValue(itemKey, PluginConstants.STATUS_LOADING);
                            }

                            var item = settings.MonitorItems.FirstOrDefault(x => x.Key == itemKey);

                            string labelPattern = output.Label;
                            if (string.IsNullOrEmpty(labelPattern)) labelPattern = (tmpl.Meta.Name) + " " + output.Key;

                            string finalName = PluginProcessor.ResolveTemplate(labelPattern, mergedInputs);
                            string finalShort = PluginProcessor.ResolveTemplate(output.ShortLabel, mergedInputs);

                            if (item == null)
                            {
                                // Create New Item
                                string safeLabel = !string.IsNullOrEmpty(finalName) ? finalName : (tmpl.Meta.Name + " " + output.Key);
                                string safeShort = !string.IsNullOrEmpty(finalShort) ? finalShort : output.Key;

                                // [Optimization] Smart Insert: Follow last sibling based on SortIndex
                                string prefix = PluginConstants.DASH_PREFIX + inst.Id + ".";
                                var siblings = settings.MonitorItems.Where(x => x.Key.StartsWith(prefix)).ToList();
                                
                                var lastSortPeer = siblings.OrderByDescending(x => x.SortIndex).FirstOrDefault();
                                var lastTbSortPeer = siblings.OrderByDescending(x => x.TaskbarSortIndex).FirstOrDefault();
                                
                                int nextSort = (lastSortPeer?.SortIndex ?? (settings.MonitorItems.Any() ? settings.MonitorItems.Max(x => x.SortIndex) : 0)) + 1;
                                int nextTbSort = (lastTbSortPeer?.TaskbarSortIndex ?? (settings.MonitorItems.Any() ? settings.MonitorItems.Max(x => x.TaskbarSortIndex) : 0)) + 1;

                                // Shift others to make room
                                foreach (var m in settings.MonitorItems)
                                {
                                    if (m.SortIndex >= nextSort) m.SortIndex++;
                                    if (m.TaskbarSortIndex >= nextTbSort) m.TaskbarSortIndex++;
                                }

                                item = new MonitorItemConfig
                                {
                                    Key = itemKey,
                                    UserLabel = "", // Auto mode
                                    DynamicLabel = safeLabel,
                                    TaskbarLabel = "", // Auto mode
                                    DynamicTaskbarLabel = safeShort,
                                    UnitPanel = output.Unit,
                                    UnitTaskbar = output.Unit, // [Fix] Sync Unit to Taskbar as well
                                    VisibleInPanel = true,
                                    SortIndex = nextSort,
                                    TaskbarSortIndex = nextTbSort,
                                };
                                settings.MonitorItems.Add(item);
                                changed = true;
                            }
                            else
                            {
                                // Update Existing Item
                                string safeLabel = !string.IsNullOrEmpty(finalName) ? finalName : (tmpl.Meta.Name + " " + output.Key);
                                string safeShort = !string.IsNullOrEmpty(finalShort) ? finalShort : output.Key;

                                if (string.IsNullOrEmpty(item.DynamicLabel)) item.DynamicLabel = safeLabel;
                                if (string.IsNullOrEmpty(item.DynamicTaskbarLabel)) item.DynamicTaskbarLabel = safeShort;

                                if (item.UnitPanel != output.Unit) { item.UnitPanel = output.Unit; changed = true; }
                                if (item.UnitTaskbar != output.Unit) { item.UnitTaskbar = output.Unit; changed = true; } // [Fix] Sync Unit to Taskbar as well
                            }
                        }
                    }
                }

                // Cleanup Orphaned Items for this Instance
                var toRemove = settings.MonitorItems
                    .Where(x => x.Key.StartsWith(PluginConstants.DASH_PREFIX + inst.Id + ".") && !validKeys.Contains(x.Key))
                    .ToList();

                foreach (var item in toRemove)
                {
                    settings.MonitorItems.Remove(item);
                    changed = true;
                }
            } // End Lock

            if (changed) settings.Save();
        }

        /// <summary>
        /// 清理指定插件实例的所有监控项
        /// </summary>
        public void RemoveMonitorItems(string instanceId)
        {
            var settings = Settings.Load();
            string mainKey = PluginConstants.DASH_PREFIX + instanceId;
            var itemsToRemove = settings.MonitorItems.Where(x => x.Key == mainKey || x.Key.StartsWith(mainKey + ".")).ToList();

            if (itemsToRemove.Count > 0)
            {
                foreach (var item in itemsToRemove)
                {
                    settings.MonitorItems.Remove(item);
                }
                settings.Save();
            }
        }

        /// <summary>
        /// 尝试根据 Key 反向推断 Label (UI 辅助方法)
        /// </summary>
        public string TryGetSmartLabel(string itemKey, List<PluginTemplate> templates, string targetField = "label")
        {
            try
            {
                if (string.IsNullOrEmpty(itemKey) || !itemKey.StartsWith(PluginConstants.DASH_PREFIX)) return "";

                var settings = Settings.Load();

                foreach (var inst in settings.PluginInstances)
                {
                    string prefix = PluginConstants.DASH_PREFIX + inst.Id + ".";
                    if (itemKey.StartsWith(prefix) || itemKey == PluginConstants.DASH_PREFIX + inst.Id)
                    {
                        var tmpl = templates.FirstOrDefault(t => t.Id == inst.TemplateId);
                        if (tmpl == null) continue;

                        string suffix = itemKey.Substring(prefix.Length);
                        
                        if (tmpl.Outputs != null)
                        {
                            foreach (var output in tmpl.Outputs)
                            {
                                if (suffix == output.Key || suffix.EndsWith("." + output.Key))
                                {
                                    var mergedInputs = new Dictionary<string, string>(inst.InputValues);
                                    if (tmpl.Inputs != null)
                                    {
                                        foreach (var input in tmpl.Inputs)
                                            if (!mergedInputs.ContainsKey(input.Key))
                                                mergedInputs[input.Key] = input.DefaultValue;
                                    }

                                    string labelPattern;
                                    if (targetField == "short_label")
                                        labelPattern = output.ShortLabel;
                                    else
                                        labelPattern = output.Label;

                                    if (string.IsNullOrEmpty(labelPattern)) labelPattern = tmpl.Meta.Name;

                                    string resolved = PluginProcessor.ResolveTemplate(labelPattern, mergedInputs);

                                    if (string.IsNullOrEmpty(resolved) || resolved.Contains("{{"))
                                        return tmpl.Meta.Name + " " + output.Key;

                                    return resolved;
                                }
                            }
                        }
                        return tmpl.Meta.Name;
                    }
                }
            }
            catch { }
            return "";
        }

        private static readonly Regex _varsRegex = new Regex(@"\{\{(.+?)\}\}", RegexOptions.Compiled);

        private bool CanResolveAllVars(string pattern, Dictionary<string, string> inputs)
        {
            if (string.IsNullOrEmpty(pattern)) return true;
            if (!pattern.Contains("{{")) return true;

            bool success = true;
            var matches = _varsRegex.Matches(pattern);
            foreach (Match m in matches)
            {
                string content = m.Groups[1].Value.Trim();
                bool resolved = false;

                if (content.Contains("??"))
                {
                    var parts = content.Split(new[] { "??" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        string key = part.Trim();
                        if (inputs.TryGetValue(key, out string val) && !string.IsNullOrEmpty(val))
                        {
                            resolved = true;
                            break;
                        }
                    }
                }
                else
                {
                    if (inputs.ContainsKey(content) && !string.IsNullOrEmpty(inputs[content])) resolved = true;
                }

                if (!resolved)
                {
                    success = false;
                    break;
                }
            }
            return success;
        }
    }
}
