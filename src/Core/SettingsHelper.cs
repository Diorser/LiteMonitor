using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using LiteMonitor.src.Core;

namespace LiteMonitor
{
    public static class SettingsHelper
    {
        // Cache path
        private static readonly string _cachedPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        public static string FilePath => _cachedPath;

        // Global block save lock
        public static bool GlobalBlockSave { get; set; } = false;

        public static Settings Load(bool forceReload = false)
        {
            // Note: The singleton instance management is kept in Settings.Load() facade
            // or handled by the caller. This method strictly loads from disk/creates default.
            
            Settings s = new Settings();
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    s = JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? new Settings();
                }
            }
            catch (Exception ex) 
            { 
                // [Fix] 添加错误日志，帮助诊断配置加载问题
                System.Diagnostics.Debug.WriteLine($"[SettingsHelper] Load failed: {ex.Message}");
            }

            if (s.GroupAliases == null) s.GroupAliases = new Dictionary<string, string>();

            // 1. Check if new install
            if (s.MonitorItems == null || s.MonitorItems.Count == 0)
            {
                s.InitDefaultItems();
                // Ensure TaskbarSortIndex has initial value
                foreach (var item in s.MonitorItems)
                {
                    if (item.TaskbarSortIndex == 0)
                        item.TaskbarSortIndex = item.SortIndex;
                }
            }
            else
            {
                // 2. Version check
                bool isLegacyConfig = s.MonitorItems.All(x => x.TaskbarSortIndex == 0);

                if (isLegacyConfig)
                {
                    s.RebuildAndMigrateSettings();
                }
                else
                {
                    s.CheckAndAppendMissingItems();
                }
            }

            // [Fix] Removed redundant SyncToLanguage call. 
            // Caller (AppActions.ApplyAllSettings) is responsible for syncing language state.
            // s.SyncToLanguage();
            
            // [Migration] 兼容老用户：如果未设置 PresetStyle (-1)，根据旧的 FontSize 推断  
            // 未来1.2.9几个版本后，将TaskbarPresetStyle 默认配置改成1即可
            if (s.TaskbarPresetStyle == -1)
            {
                // 9.0pt => 小字模式(0), 其他 => 大字模式(1)
                s.TaskbarPresetStyle = (Math.Abs(s.TaskbarFontSize - Settings.DEFAULT_TB_SIZE_REGULAR) < 0.1f) ? 0 : 1;
            }

            s.InternAllStrings();
            
            return s;
        }

        public static void Save(this Settings settings)
        {
            if (GlobalBlockSave) return;

            try
            {
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex) 
            { 
                // [Fix] 添加错误日志，帮助诊断配置保存问题
                System.Diagnostics.Debug.WriteLine($"[SettingsHelper] Save failed: {ex.Message}");
            }
        }

        public static void InitDefaultItems(this Settings settings)
        {
            settings.MonitorItems = new List<MonitorItemConfig>
            {
                // [1xx] Dashboard Items
                // [Taskbar] DASH items should appear at the END by default (SortIndex > 800)
                new MonitorItemConfig { Key = "DASH.HOST", SortIndex = 101, TaskbarSortIndex = 1100, VisibleInPanel = false, TaskbarLabel = " " },
                new MonitorItemConfig { Key = "DASH.Time", SortIndex = 102, TaskbarSortIndex = 1200, VisibleInPanel = false, TaskbarLabel = " " },
                new MonitorItemConfig { Key = "DASH.Uptime", SortIndex = 103, TaskbarSortIndex = 1300, VisibleInPanel = true, TaskbarLabel = " " },
                new MonitorItemConfig { Key = "DASH.IP",   SortIndex = 104, TaskbarSortIndex = 1400, VisibleInPanel = false, TaskbarLabel = " " },
               
                // [2xx] CPU
                new MonitorItemConfig { Key = "CPU.Load",  SortIndex = 201, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "CPU.Temp",  SortIndex = 202, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "CPU.Clock", SortIndex = 203, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CPU.Power", SortIndex = 204, VisibleInPanel = false },
                // [New] CPU Voltage
                new MonitorItemConfig { Key = "CPU.Voltage", SortIndex = 205, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CPU.Fan",   SortIndex = 206, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CPU.Pump",  SortIndex = 207, VisibleInPanel = false },

                // [3xx] GPU
                new MonitorItemConfig { Key = "GPU.Load",  SortIndex = 301, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "GPU.Temp",  SortIndex = 302, VisibleInPanel = true },
                new MonitorItemConfig { Key = "GPU.Clock", SortIndex = 303, VisibleInPanel = false },
                new MonitorItemConfig { Key = "GPU.Power", SortIndex = 304, VisibleInPanel = false },
                new MonitorItemConfig { Key = "GPU.Fan",   SortIndex = 305, VisibleInPanel = false },
                new MonitorItemConfig { Key = "GPU.VRAM",  SortIndex = 306, VisibleInPanel = true },

                // [4xx] HOST (MEM, FPS, MOBO, DISK Temp, CASE Fan)
                new MonitorItemConfig { Key = "MEM.Load",  SortIndex = 401, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "FPS",       SortIndex = 402, VisibleInPanel = false },
                new MonitorItemConfig { Key = "MOBO.Temp", SortIndex = 403, VisibleInPanel = false },
                new MonitorItemConfig { Key = "DISK.Temp", SortIndex = 404, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CASE.Fan",  SortIndex = 405, VisibleInPanel = false },
                
                // [5xx] BATTERY (New Group)
                new MonitorItemConfig { Key = "BAT.Percent", SortIndex = 501, VisibleInPanel = false, VisibleInTaskbar = false },
                new MonitorItemConfig { Key = "BAT.Power",   SortIndex = 502, VisibleInPanel = false },
                new MonitorItemConfig { Key = "BAT.Voltage", SortIndex = 503, VisibleInPanel = false },
                new MonitorItemConfig { Key = "BAT.Current", SortIndex = 504, VisibleInPanel = false },

                // [6xx] DISK IO
                new MonitorItemConfig { Key = "DISK.Read", SortIndex = 601, VisibleInPanel = true },
                new MonitorItemConfig { Key = "DISK.Write",SortIndex = 602, VisibleInPanel = true },

                // [7xx] NET
                new MonitorItemConfig { Key = "NET.Up",    SortIndex = 701, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "NET.Down",  SortIndex = 702, VisibleInPanel = true, VisibleInTaskbar = true },

                // [8xx] DATA
                new MonitorItemConfig { Key = "DATA.DayUp",  SortIndex = 801, VisibleInPanel = true },
                new MonitorItemConfig { Key = "DATA.DayDown",SortIndex = 802, VisibleInPanel = true },
            };
        }
        // [Sync] 同步到语言设置
        // 作用：将配置中的组别名和监控项标签同步到语言管理器
        // 注意：这会清除所有当前的语言覆盖
        public static void SyncToLanguage(this Settings settings)
        {
            LanguageManager.ClearOverrides();
            if (settings.GroupAliases != null)
            {
                foreach (var kv in settings.GroupAliases)
                    LanguageManager.SetOverride(UIUtils.Intern("Groups." + kv.Key), kv.Value);
            }
            if (settings.MonitorItems != null)
            {
                foreach (var item in settings.MonitorItems)
                {
                    if (!string.IsNullOrEmpty(item.UserLabel))
                        LanguageManager.SetOverride(UIUtils.Intern("Items." + item.Key), item.UserLabel);
                    if (!string.IsNullOrEmpty(item.TaskbarLabel))
                        LanguageManager.SetOverride(UIUtils.Intern("Short." + item.Key), item.TaskbarLabel);
                }
            }
        }

        // Cache for standard keys to avoid repeated allocations
        private static readonly Lazy<HashSet<string>> _standardKeys = new Lazy<HashSet<string>>(() => 
        {
            var s = new Settings();
            s.InitDefaultItems();
            return new HashSet<string>(s.MonitorItems.Select(x => x.Key), StringComparer.OrdinalIgnoreCase);
        });

        public static HashSet<string> GetStandardKeys() => _standardKeys.Value;

        public static void InternAllStrings(this Settings settings)
        {
            if (settings.MonitorItems != null)
            {
                // [Cleanup] Remove Orphaned Items
                // Optimization: Use StandardKeys whitelist to avoid heavy allocations
                var whitelist = GetStandardKeys();
                var keysToRemove = new List<MonitorItemConfig>();

                foreach (var item in settings.MonitorItems)
                {
                    if (item == null) continue;
                    
                    item.Key = UIUtils.Intern(item.Key);
                    
                    // 1. Is it a standard item?
                    if (whitelist.Contains(item.Key)) continue;

                    // 2. Is it a valid Plugin item? 
                    // [Safety] 只要是 DASH. 开头，一律保留，防止误删用户自定义的简单插件
                    if (item.Key.StartsWith("DASH.", StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // 保留所有 DASH.*
                    }
                    keysToRemove.Add(item);
                }
                
                foreach (var orphan in keysToRemove)
                {
                    settings.MonitorItems.Remove(orphan);
                }
            }

            settings.PreferredDisk = UIUtils.Intern(settings.PreferredDisk);
            settings.LastAutoDisk = UIUtils.Intern(settings.LastAutoDisk);
            settings.PreferredNetwork = UIUtils.Intern(settings.PreferredNetwork);
            settings.LastAutoNetwork = UIUtils.Intern(settings.LastAutoNetwork);
            
            settings.PreferredCpuFan = UIUtils.Intern(settings.PreferredCpuFan);
            settings.PreferredCpuPump = UIUtils.Intern(settings.PreferredCpuPump);
            settings.PreferredCaseFan = UIUtils.Intern(settings.PreferredCaseFan);
            settings.PreferredMoboTemp = UIUtils.Intern(settings.PreferredMoboTemp);
            
            settings.TaskbarFontFamily = UIUtils.Intern(settings.TaskbarFontFamily);
        }

        public static void RebuildAndMigrateSettings(this Settings settings)
        {
            // 1. 获取最新版本的标准清单 (确保包含新版本增加的监控项)
            var temp = new Settings();
            temp.InitDefaultItems();
            var migratedList = temp.MonitorItems;

            // 2. 遍历新标准清单，回填用户的个性化设置
            // 同时构建标准键集合，用于后续过滤
            var standardKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var newItem in migratedList)
            {
                standardKeys.Add(newItem.Key);

                var oldItem = settings.MonitorItems.FirstOrDefault(x => x.Key.Equals(newItem.Key, StringComparison.OrdinalIgnoreCase));
                if (oldItem != null)
                {
                    newItem.VisibleInPanel = oldItem.VisibleInPanel;
                    newItem.VisibleInTaskbar = oldItem.VisibleInTaskbar;
                    newItem.UserLabel = oldItem.UserLabel;
                    newItem.TaskbarLabel = oldItem.TaskbarLabel;
                    newItem.UnitPanel = oldItem.UnitPanel;
                    newItem.UnitTaskbar = oldItem.UnitTaskbar;
                }
                // 数据修复：老版本没有 TaskbarSortIndex，默认初始化
                if (newItem.TaskbarSortIndex == 0) newItem.TaskbarSortIndex = newItem.SortIndex;
            }

            // 3. [Fix] 找回插件项：追加 DASH 开头且不在标准列表中的配置
            // 必须过滤掉 DASH.HOST/DASH.Time 等标准项，否则会重复！
            migratedList.AddRange(settings.MonitorItems
                .Where(x => x.Key.StartsWith("DASH.", StringComparison.OrdinalIgnoreCase) && !standardKeys.Contains(x.Key))
                .Select(x => { 
                    if (x.TaskbarSortIndex == 0) x.TaskbarSortIndex = x.SortIndex; 
                    return x; 
                }));

            settings.MonitorItems = migratedList;
        }

        public static void CheckAndAppendMissingItems(this Settings settings)
        {
            var temp = new Settings();
            temp.InitDefaultItems();
            var defaultList = temp.MonitorItems;
            
            // 找出缺失项 (保持 DefaultList 的相对顺序)
            var newItems = defaultList
                .Where(std => !settings.MonitorItems.Any(usr => usr.Key.Equals(std.Key, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (newItems.Count == 0) return;

            bool listChanged = false;

            foreach (var newItem in newItems)
            {
                // [智能锚点逻辑]
                // 不使用硬编码的 SortIndex，而是寻找该项在 DefaultList 中的"前一个邻居" (Anchor)
                // 如果用户把邻居移到了别处，新项会紧随其后。
                
                MonitorItemConfig? anchor = null;
                int myDefIdx = defaultList.FindIndex(x => x.Key == newItem.Key);
                
                // 往前回溯寻找最近的有效锚点
                for (int k = myDefIdx - 1; k >= 0; k--)
                {
                    var prevKey = defaultList[k].Key;
                    var existing = settings.MonitorItems.FirstOrDefault(x => x.Key.Equals(prevKey, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        anchor = existing;
                        break;
                    }
                }

                // === A. Panel SortIndex ===
                int targetIndex;
                if (anchor != null)
                {
                    targetIndex = anchor.SortIndex + 1;
                }
                else
                {
                    // 没锚点 (说明是队首)，插在当前最小值前面
                    int min = settings.MonitorItems.Count > 0 ? settings.MonitorItems.Min(x => x.SortIndex) : 0;
                    targetIndex = min - 1;
                }

                // 挤开后续项
                foreach (var item in settings.MonitorItems.Where(x => x.SortIndex >= targetIndex))
                    item.SortIndex++;
                
                newItem.SortIndex = targetIndex;

                // === B. Taskbar SortIndex ===
                int targetTbIndex;
                if (anchor != null)
                {
                    targetTbIndex = anchor.TaskbarSortIndex + 1;
                }
                else
                {
                    // [Fix] 没锚点 (如 DASH 这种排在队首的) 或锚点未开启任务栏
                    // 策略：看该项在 Default 中的原始意图。
                    // 如果 Default 中 TaskbarSortIndex 很大 (>1000)，说明意图是放后面 -> 插在 Max + 1
                    // 如果 Default 中 TaskbarSortIndex 很小 (<1000)，说明意图是放前面 -> 插在 Min - 1
                    
                    var validItems = settings.MonitorItems.Where(x => x.TaskbarSortIndex != 0).ToList();
                    
                    if (newItem.TaskbarSortIndex > 1000)
                    {
                        // 意图：放后面
                        int max = validItems.Count > 0 ? validItems.Max(x => x.TaskbarSortIndex) : 0;
                        targetTbIndex = max + 1;
                    }
                    else
                    {
                        // 意图：放前面
                        int min = validItems.Count > 0 ? validItems.Min(x => x.TaskbarSortIndex) : 1;
                        targetTbIndex = min - 1;
                        if (targetTbIndex == 0) targetTbIndex = -1; // 避开 0
                    }
                }

                foreach (var item in settings.MonitorItems.Where(x => x.TaskbarSortIndex >= targetTbIndex))
                    item.TaskbarSortIndex++;

                newItem.TaskbarSortIndex = targetTbIndex;

                settings.MonitorItems.Add(newItem);
                listChanged = true;
            }

            if (listChanged)
            {
                settings.MonitorItems = settings.MonitorItems.OrderBy(x => x.SortIndex).ToList();
            }
        }

        public static Settings.TBStyle GetStyle(this Settings settings)
        {
            // 如果开启了自定义布局，完全使用自定义参数
            if (settings.TaskbarCustomLayout) return new Settings.TBStyle {
                Font = settings.TaskbarFontFamily, Size = settings.TaskbarFontSize, Bold = settings.TaskbarFontBold,
                Gap = settings.TaskbarItemSpacing, Inner = settings.TaskbarInnerSpacing, VOff = settings.TaskbarVerticalPadding
            };

            // 如果未开启自定义布局，使用标准布局参数
            bool isSmall = settings.TaskbarPresetStyle == 0; // 0 = Regular/Small, 1 = Bold/Big

            return new Settings.TBStyle {
                Font = Settings.DEFAULT_TB_FONT, 
                Size = isSmall ? Settings.DEFAULT_TB_SIZE_REGULAR : Settings.DEFAULT_TB_SIZE_BOLD,
                Bold = !isSmall,
                Gap = Settings.DEFAULT_TB_GAP, 
                Inner = isSmall ? Settings.DEFAULT_TB_INNER_REGULAR : Settings.DEFAULT_TB_INNER_BOLD, 
                VOff = Settings.DEFAULT_TB_VOFF 
            };
        }

        public static bool IsAnyEnabled(this Settings settings, string keyPrefix)
        {
            return settings.MonitorItems.Any(x => x.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase) && (x.VisibleInPanel || x.VisibleInTaskbar));
        }

        public static void UpdateMaxRecord(this Settings settings, string key, float val)
        {
            bool changed = false;
            if (val <= 0 || float.IsNaN(val) || float.IsInfinity(val)) return;
            
            if (key.Contains("Clock") && val > 10000) return; 
            if (key.Contains("Power") && val > 1000) return;
            if ((key.Contains("Fan") || key.Contains("Pump")) && val > 10000) return;

            if (key == "CPU.Power" && val > settings.RecordedMaxCpuPower) { settings.RecordedMaxCpuPower = val; changed = true; }
            else if (key == "CPU.Clock" && val > settings.RecordedMaxCpuClock) { settings.RecordedMaxCpuClock = val; changed = true; }
            else if (key == "GPU.Power" && val > settings.RecordedMaxGpuPower) { settings.RecordedMaxGpuPower = val; changed = true; }
            else if (key == "GPU.Clock" && val > settings.RecordedMaxGpuClock) { settings.RecordedMaxGpuClock = val; changed = true; }
            
            else if (key == "CPU.Fan" && val > settings.RecordedMaxCpuFan) { settings.RecordedMaxCpuFan = val; changed = true; }
            else if (key == "CPU.Pump" && val > settings.RecordedMaxCpuPump) { settings.RecordedMaxCpuPump = val; changed = true; }
            else if (key == "GPU.Fan" && val > settings.RecordedMaxGpuFan) { settings.RecordedMaxGpuFan = val; changed = true; }
            else if (key == "CASE.Fan" && val > settings.RecordedMaxChassisFan) { settings.RecordedMaxChassisFan = val; changed = true; }
            
            if (changed && (DateTime.Now - settings.LastAutoSaveTime).TotalSeconds > 30)
            {
                settings.Save();
                settings.LastAutoSaveTime = DateTime.Now;
            }
        }
    }
}
