using System.Linq;
using LiteMonitor.src.Core;
using System.Reflection;
using System.Collections.Generic;
namespace LiteMonitor.src.Core.Actions
{
    /// <summary>
    /// 封装所有修改 Settings 对象的逻辑。
    /// 支持草稿/提交 (Draft/Commit) 架构。
    /// 使用反射的简化版本。
    /// </summary>
    public static class SettingsChanger
    {
        /// <summary>
        /// 使用反射将草稿设置 (Draft) 合并到实时设置 (Live) 中。
        /// 保留在黑名单中定义的仅运行时属性。
        /// </summary>
        public static void Merge(Settings live, Settings draft)
        {
            if (live == null || draft == null) return;

            // 不应被草稿覆盖的属性黑名单 (运行时数据)
            //这些值由后台逻辑自动更新。如果不在黑名单中，当你打开设置窗口时（Draft 状态）它们可能已经发生了变化
            var runtimeProps = new HashSet<string>
            {
                
                // 其他运行时状态
                "LastAutoNetwork", "LastAutoDisk",
                "ScreenDevice", "MaxLimitTipShown",
                
                // 流量统计 (累加值)
                "TotalUpload", "TotalDownload",
                "SessionUploadBytes", "SessionDownloadBytes",
                
                // 时间戳
                "LastAutoSaveTime", "LastAlertTime"
            };

            var props = typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var p in props)
            {
                if (!p.CanWrite || !p.CanRead) continue;
                
                // 1. 跳过黑名单中的运行时属性
                if (runtimeProps.Contains(p.Name)) continue;
                
                // 2. 特殊处理集合类型
                if (p.Name == "MonitorItems")
                {
                    UpdateMonitorList(live, draft.MonitorItems, draft.HorizontalFollowsTaskbar);
                    continue;
                }
                if (p.Name == "PluginInstances")
                {
                    // 使用 JSON 序列化进行深拷贝，断开引用
                    var json = System.Text.Json.JsonSerializer.Serialize(draft.PluginInstances);
                    live.PluginInstances = System.Text.Json.JsonSerializer.Deserialize<List<PluginInstanceConfig>>(json) ?? new List<PluginInstanceConfig>();
                    continue;
                }
                if (p.Name == "Thresholds")
                {
                    // 阈值是 Class 类型 (ThresholdsSet)，必须深拷贝
                    var json = System.Text.Json.JsonSerializer.Serialize(draft.Thresholds);
                    live.Thresholds = System.Text.Json.JsonSerializer.Deserialize<ThresholdsSet>(json) ?? new ThresholdsSet();
                    continue;
                }
                // 3. 特殊处理字典类型
                 if (p.Name == "GroupAliases")
                {
                     live.GroupAliases = new Dictionary<string, string>(draft.GroupAliases);
                     continue;
                }

                // 3. 默认：直接复制
                // 处理所有 bool, int, string, enum 等类型
                var val = p.GetValue(draft);
                p.SetValue(live, val);
            }
        }

        /// <summary>
        /// 基于 UI 的工作列表更新目标 Settings 对象中的 MonitorItems 列表。
        /// 处理合并逻辑以保留动态属性 (如 DynamicLabel)。
        /// </summary>
        public static void UpdateMonitorList(Settings target, List<MonitorItemConfig> workingList, bool horizontalFollowsTaskbar)
        {
            if (target == null || workingList == null) return;

            target.HorizontalFollowsTaskbar = horizontalFollowsTaskbar;

            // ★★★ 1. 深拷贝 Draft 列表，断开引用 ★★★
            // 防止修改 Draft 属性时直接影响 Live 对象
            var json = System.Text.Json.JsonSerializer.Serialize(workingList);
            var safeWorkingList = System.Text.Json.JsonSerializer.Deserialize<List<MonitorItemConfig>>(json) 
                                  ?? new List<MonitorItemConfig>();

            // ★★★ 2. 恢复运行时状态 (Dynamic Labels) ★★★
            // 因为 JSON 序列化丢失了 [JsonIgnore] 的属性，需要从 Live 对象中恢复它们
            var liveMap = target.MonitorItems.ToDictionary(x => x.Key);
            foreach (var item in safeWorkingList)
            {
                if (liveMap.TryGetValue(item.Key, out var liveItem))
                {
                    item.DynamicLabel = liveItem.DynamicLabel;
                    item.DynamicTaskbarLabel = liveItem.DynamicTaskbarLabel;
                }
            }

            // 合并逻辑
            var activeKeys = new HashSet<string>(target.MonitorItems.Select(x => x.Key));
            
            // 3. 获取配置中存在的项 (保留 UI 排序/更改)
            var mergedList = safeWorkingList.Where(x => activeKeys.Contains(x.Key)).ToList();

            // 4. 添加配置中出现但工作列表中缺失的新项 
            var workingKeys = new HashSet<string>(safeWorkingList.Select(x => x.Key));
            var newItems = target.MonitorItems.Where(x => !workingKeys.Contains(x.Key)).ToList();
            
            if (newItems.Count > 0)
            {
                mergedList.AddRange(newItems);
            }
            
            target.MonitorItems = mergedList;
        }

        /// <summary>
        /// 在应用设置后，将 Live 环境的最新监控项（包含插件生成的新项）回写同步到 Draft。
        /// 必须处理深拷贝和动态属性的恢复。
        /// </summary>
        public static void RebaseDraftMonitorItems(Settings live, Settings draft)
        {
            if (live?.MonitorItems == null || draft == null) return;

            // 运行时可能会添加/删除插件监控项，或更新动态标签；但用户在 Draft
            // 中编辑的可见性、名称、单位和排序必须优先保留，不能被 Live 整体覆盖。
            var liveMap = new Dictionary<string, MonitorItemConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var liveItem in live.MonitorItems)
            {
                if (liveItem != null && !string.IsNullOrEmpty(liveItem.Key) && !liveMap.ContainsKey(liveItem.Key))
                    liveMap[liveItem.Key] = liveItem;
            }

            var mergedItems = new List<MonitorItemConfig>();
            var retainedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 按 Draft 原有顺序保留仍然存在的用户配置。
            foreach (var draftItem in draft.MonitorItems ?? new List<MonitorItemConfig>())
            {
                if (draftItem == null || string.IsNullOrEmpty(draftItem.Key) ||
                    !liveMap.TryGetValue(draftItem.Key, out var liveItem) ||
                    !retainedKeys.Add(draftItem.Key))
                    continue;

                draftItem.DynamicLabel = liveItem.DynamicLabel;
                draftItem.DynamicTaskbarLabel = liveItem.DynamicTaskbarLabel;
                mergedItems.Add(draftItem);
            }

            // 将应用期间由插件生成的新项追加到 Draft；JsonIgnore 的动态字段单独恢复。
            foreach (var liveItem in live.MonitorItems)
            {
                if (liveItem == null || string.IsNullOrEmpty(liveItem.Key) || retainedKeys.Contains(liveItem.Key))
                    continue;

                var json = System.Text.Json.JsonSerializer.Serialize(liveItem);
                var clonedItem = System.Text.Json.JsonSerializer.Deserialize<MonitorItemConfig>(json);
                if (clonedItem == null) continue;

                clonedItem.DynamicLabel = liveItem.DynamicLabel;
                clonedItem.DynamicTaskbarLabel = liveItem.DynamicTaskbarLabel;
                mergedItems.Add(clonedItem);
                retainedKeys.Add(liveItem.Key);
            }

            draft.MonitorItems = mergedItems;
        }
        /// <summary>
        /// 向设置中添加新的插件实例。
        /// </summary>
        public static void AddPlugin(Settings target, PluginInstanceConfig plugin)
        {
            if (target == null || plugin == null) return;
            target.PluginInstances.Add(plugin);
        }

        /// <summary>
        /// 从设置中移除插件实例。
        /// </summary>
        public static void RemovePlugin(Settings target, PluginInstanceConfig plugin)
        {
            if (target == null || plugin == null) return;
            target.PluginInstances.Remove(plugin);
        }
    }
}
