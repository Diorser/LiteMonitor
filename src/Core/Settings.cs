using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiteMonitor.src.Core;
namespace LiteMonitor
{
    public class Settings
    {
        // ====== 基础设置 ======
        public string Skin { get; set; } = "DarkFlat_Classic";
        public bool TopMost { get; set; } = true;
        public bool AutoStart { get; set; } = false;
        public int RefreshMs { get; set; } = 1000;
        public double AnimationSpeed { get; set; } = 0.35;
        public Point Position { get; set; } = new Point(-1, -1);

        // ====== 界面与行为 ======
        public bool HorizontalMode { get; set; } = false;
        public double Opacity { get; set; } = 0.85;
        public string Language { get; set; } = "";
        public bool ClickThrough { get; set; } = false;
        public bool AutoHide { get; set; } = true;
        public bool ClampToScreen { get; set; } = true;
        public int PanelWidth { get; set; } = 240;
        public double UIScale { get; set; } = 1.0;

        // ====== 硬件相关 ======
        public string PreferredNetwork { get; set; } = "";
        public string LastAutoNetwork { get; set; } = "";
        public string PreferredDisk { get; set; } = "";
        public string LastAutoDisk { get; set; } = "";
        
        // ★★★ [新增] 首选风扇 ★★★
        public string PreferredCpuFan { get; set; } = "";
        public string PreferredCpuPump { get; set; } = ""; // 保存用户选的水冷接口
        public string PreferredCaseFan { get; set; } = "";

        // 主窗体所在的屏幕设备名 (用于记忆上次位置)
        public string ScreenDevice { get; set; } = "";

        // ====== 任务栏 ======
        public bool ShowTaskbar { get; set; } = false;
        // ★★★ 新增：横条模式是否跟随任务栏布局？ ★★★
        public bool HorizontalFollowsTaskbar { get; set; } = false;
        public bool HideMainForm { get; set; } = false;
        public bool HideTrayIcon { get; set; } = false;
        public bool TaskbarAlignLeft { get; set; } = true;
        public string TaskbarFontFamily { get; set; } = "Microsoft YaHei UI";
        public float TaskbarFontSize { get; set; } = 10f;
        public bool TaskbarFontBold { get; set; } = true;
        
        // ★★★ 新增：指定任务栏显示的屏幕设备名 ("" = 自动/主屏) ★★★
        public string TaskbarMonitorDevice { get; set; } = "";

        // 任务栏行为配置
        public bool TaskbarClickThrough { get; set; } = false;     // 鼠标穿透
        public bool TaskbarSingleLine { get; set; } = false;// 单行显示
        public int TaskbarManualOffset { get; set; } = 0;// 手动偏移量 (像素)

        // ====== 任务栏：高级自定义外观 ======
        public bool TaskbarCustomStyle { get; set; } = false; // 总开关
        public string TaskbarColorLabel { get; set; } = "#141414"; // 标签颜色
        public string TaskbarColorSafe { get; set; } = "#008040";  // 正常 (淡绿)
        public string TaskbarColorWarn { get; set; } = "#B57500";  // 警告 (金黄)
        public string TaskbarColorCrit { get; set; } = "#C03030";  // 严重 (橙红)
        public string TaskbarColorBg { get; set; } = "#D2D2D2";    // 防杂边背景色 (透明键)

        // 双击动作配置
        public int MainFormDoubleClickAction { get; set; } = 0;
        public int TaskbarDoubleClickAction { get; set; } = 0;

        // 内存/显存显示模式
        public int MemoryDisplayMode { get; set; } = 0;

        // ★ 2. 运行时缓存：存储探测到的总容量 (GB)
        [JsonIgnore] public static float DetectedRamTotalGB { get; set; } = 0;
        [JsonIgnore] public static float DetectedGpuVramTotalGB { get; set; } = 0;

        public bool UseSystemCpuLoad { get; set; } = false; 
        
        // ====== 记录与报警 ======
        public float RecordedMaxCpuPower { get; set; } = 65.0f;
        public float RecordedMaxCpuClock { get; set; } = 4200.0f;
        public float RecordedMaxGpuPower { get; set; } = 100.0f;
        public float RecordedMaxGpuClock { get; set; } = 1800.0f;
        
        // ★★★ [新增] 风扇最大值记录 ★★★
        public float RecordedMaxCpuFan { get; set; } = 4000;
        public float RecordedMaxCpuPump { get; set; } = 5000; // 水冷最大转速 (用于百分比计算)
        public float RecordedMaxGpuFan { get; set; } = 3500;
        public float RecordedMaxChassisFan { get; set; } = 3000;

        public bool MaxLimitTipShown { get; set; } = false;
        
        public bool AlertTempEnabled { get; set; } = true;
        public int AlertTempThreshold { get; set; } = 80;
        
        public ThresholdsSet Thresholds { get; set; } = new ThresholdsSet();

        [JsonIgnore] public DateTime LastAlertTime { get; set; } = DateTime.MinValue;
        [JsonIgnore] public long SessionUploadBytes { get; set; } = 0;
        [JsonIgnore] public long SessionDownloadBytes { get; set; } = 0;
        [JsonIgnore] private DateTime _lastAutoSave = DateTime.MinValue;

        public Dictionary<string, string> GroupAliases { get; set; } = new Dictionary<string, string>();
        public List<MonitorItemConfig> MonitorItems { get; set; } = new List<MonitorItemConfig>();

        public bool IsAnyEnabled(string keyPrefix)
        {
            return MonitorItems.Any(x => x.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase) && (x.VisibleInPanel || x.VisibleInTaskbar));
        }

        public void SyncToLanguage()
        {
            LanguageManager.ClearOverrides();
            if (GroupAliases != null)
            {
                foreach (var kv in GroupAliases)
                    LanguageManager.SetOverride("Groups." + kv.Key, kv.Value);
            }
            if (MonitorItems != null)
            {
                foreach (var item in MonitorItems)
                {
                    if (!string.IsNullOrEmpty(item.UserLabel))
                        LanguageManager.SetOverride("Items." + item.Key, item.UserLabel);
                    if (!string.IsNullOrEmpty(item.TaskbarLabel))
                        LanguageManager.SetOverride("Short." + item.Key, item.TaskbarLabel);
                }
            }
        }

        public void UpdateMaxRecord(string key, float val)
        {
            bool changed = false;
            if (val <= 0 || float.IsNaN(val) || float.IsInfinity(val)) return;
            
            if (key.Contains("Clock") && val > 10000) return; 
            if (key.Contains("Power") && val > 1000) return;
            // ★★★ [新增] 风扇转速异常过滤 ★★★
            if ((key.Contains("Fan") || key.Contains("Pump")) && val > 10000) return;

            if (key == "CPU.Power" && val > RecordedMaxCpuPower) { RecordedMaxCpuPower = val; changed = true; }
            else if (key == "CPU.Clock" && val > RecordedMaxCpuClock) { RecordedMaxCpuClock = val; changed = true; }
            else if (key == "GPU.Power" && val > RecordedMaxGpuPower) { RecordedMaxGpuPower = val; changed = true; }
            else if (key == "GPU.Clock" && val > RecordedMaxGpuClock) { RecordedMaxGpuClock = val; changed = true; }
            
            // ★★★ [新增] 自动记录风扇最大值 ★★★
            else if (key == "CPU.Fan" && val > RecordedMaxCpuFan) { RecordedMaxCpuFan = val; changed = true; }
            else if (key == "CPU.Pump" && val > RecordedMaxCpuPump) { RecordedMaxCpuPump = val; changed = true; }
            else if (key == "GPU.Fan" && val > RecordedMaxGpuFan) { RecordedMaxGpuFan = val; changed = true; }
            else if (key == "CASE.Fan" && val > RecordedMaxChassisFan) { RecordedMaxChassisFan = val; changed = true; }
            

            if (changed && (DateTime.Now - _lastAutoSave).TotalSeconds > 30)
            {
                Save();
                _lastAutoSave = DateTime.Now;
            }
        }

        private static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");


        public static Settings Load()
        {
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
            catch { }

            if (s.GroupAliases == null) s.GroupAliases = new Dictionary<string, string>();

            // 1. 检查是否是全新安装
            if (s.MonitorItems == null || s.MonitorItems.Count == 0)
            {
                s.InitDefaultItems();
                // 确保 TaskbarSortIndex 有初始值
                foreach (var item in s.MonitorItems) item.TaskbarSortIndex = item.SortIndex;
            }
            else
            {
                // 2. 智能版本判断
                // 如果所有项的 TaskbarSortIndex 都是 0，说明这是老版本的配置文件
                bool isLegacyConfig = s.MonitorItems.All(x => x.TaskbarSortIndex == 0);

                if (isLegacyConfig)
                {
                    // ★★★ 方案 A：旧版升级（重构式迁移） ★★★
                    // 用户希望：不要乱追加，直接按新版默认顺序重新整理，但保留我的开关设置
                    s.RebuildAndMigrateSettings();
                }
                else
                {
                    // ★★★ 方案 B：日常更新（温和补全） ★★★
                    // 说明用户已经是新版本（已有任务栏排序），可能只是我们又加了一个小功能
                    // 这时不要重置用户的排序，而是追加到最后
                    s.CheckAndAppendMissingItems();
                }
            }

            s.SyncToLanguage();
            return s;
        }

        /// <summary>
        /// [核心重构] 以新版默认列表为蓝本，回填用户的旧设置
        /// 效果：排序会被重置为新版逻辑（整洁），但用户的“显示/隐藏”和“自定义命名”会被保留
        /// </summary>
        private void RebuildAndMigrateSettings()
        {
            // 1. 获取新版本的标准模板（顺序是最完美的逻辑分组）
            var temp = new Settings();
            temp.InitDefaultItems();
            var standardItems = temp.MonitorItems;

            var migratedList = new List<MonitorItemConfig>();

            foreach (var stdItem in standardItems)
            {
                // 2. 在用户旧配置中查找对应的项
                var userOldItem = MonitorItems.FirstOrDefault(x => x.Key.Equals(stdItem.Key, StringComparison.OrdinalIgnoreCase));

                if (userOldItem != null)
                {
                    // 3. 【关键】保留用户的个性化设置
                    stdItem.VisibleInPanel = userOldItem.VisibleInPanel;
                    stdItem.VisibleInTaskbar = userOldItem.VisibleInTaskbar;
                    stdItem.UserLabel = userOldItem.UserLabel;
                    stdItem.TaskbarLabel = userOldItem.TaskbarLabel;
                    
                    // 注意：这里我们故意【不继承】userOldItem.SortIndex
                    // 这样就能强行纠正旧版本的排序，让 CPU.Fan 自动插到 CPU 组里，而不是排到最后
                }

                // 4. 确保新项的任务栏排序有默认值 (默认为 SortIndex)
                if (stdItem.TaskbarSortIndex == 0) 
                {
                    stdItem.TaskbarSortIndex = stdItem.SortIndex;
                }

                migratedList.Add(stdItem);
            }

            // 5. 替换生效
            MonitorItems = migratedList;
            
            // 可选：保存一下，让迁移立即固化到磁盘
            // Save(); 
        }

        // 辅助：普通补全逻辑（用于已经是新版后的后续小更新）
        private void CheckAndAppendMissingItems()
        {
            var temp = new Settings();
            temp.InitDefaultItems();
            
            // 计算追加的起始 ID（防止冲突）
            int maxSort = MonitorItems.Count > 0 ? MonitorItems.Max(x => x.SortIndex) : 0;
            int maxTaskbarSort = MonitorItems.Count > 0 ? MonitorItems.Max(x => x.TaskbarSortIndex) : 0;

            foreach (var stdItem in temp.MonitorItems)
            {
                if (!MonitorItems.Any(x => x.Key.Equals(stdItem.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    stdItem.SortIndex = ++maxSort;
                    stdItem.TaskbarSortIndex = ++maxTaskbarSort;
                    MonitorItems.Add(stdItem);
                }
            }
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }

        private void InitDefaultItems()
        {
            MonitorItems = new List<MonitorItemConfig>
            {
                new MonitorItemConfig { Key = "CPU.Load",  SortIndex = 0, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "CPU.Temp",  SortIndex = 1, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "CPU.Clock", SortIndex = 2, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CPU.Power", SortIndex = 3, VisibleInPanel = false },
                // ★★★ [新增] CPU Fan ★★★
                new MonitorItemConfig { Key = "CPU.Fan",   SortIndex = 4, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CPU.Pump",  SortIndex = 5, VisibleInPanel = false },

                new MonitorItemConfig { Key = "GPU.Load",  SortIndex = 10, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "GPU.Temp",  SortIndex = 11, VisibleInPanel = true },
                new MonitorItemConfig { Key = "GPU.Clock", SortIndex = 12, VisibleInPanel = false },
                new MonitorItemConfig { Key = "GPU.Power", SortIndex = 13, VisibleInPanel = false },
                // ★★★ [新增] GPU Fan ★★★
                new MonitorItemConfig { Key = "GPU.Fan",   SortIndex = 14, VisibleInPanel = false },
                new MonitorItemConfig { Key = "GPU.VRAM",  SortIndex = 15, VisibleInPanel = true },

                new MonitorItemConfig { Key = "MEM.Load",  SortIndex = 20, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "DISK.Temp", SortIndex = 21, VisibleInPanel = false },
                new MonitorItemConfig { Key = "MOBO.Temp", SortIndex = 22, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CASE.Fan",  SortIndex = 23, VisibleInPanel = false },

                new MonitorItemConfig { Key = "DISK.Read", SortIndex = 30, VisibleInPanel = true },
                new MonitorItemConfig { Key = "DISK.Write",SortIndex = 31, VisibleInPanel = true },

                new MonitorItemConfig { Key = "NET.Up",    SortIndex = 40, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "NET.Down",  SortIndex = 41, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "DATA.DayUp",  SortIndex = 50, VisibleInPanel = true },
                new MonitorItemConfig { Key = "DATA.DayDown",SortIndex = 51, VisibleInPanel = true },
            };
        }
    }

    public class MonitorItemConfig
    {
        public string Key { get; set; } = "";
        public string UserLabel { get; set; } = ""; 
        public string TaskbarLabel { get; set; } = "";
        public bool VisibleInPanel { get; set; } = true;
        public bool VisibleInTaskbar { get; set; } = false;
        public int SortIndex { get; set; } = 0;
        // ★★★ 新增：任务栏独立排序索引 ★★★
        public int TaskbarSortIndex { get; set; } = 0;
        // ★★★ 新增：统一的分组属性 ★★★
        // 所有界面（主界面、设置页、菜单）都统一调用这个属性来决定它属于哪个组
        // 从而避免了在 UI 代码里到处写 if else
        [JsonIgnore]
        public string UIGroup 
        {
            get 
            {
                // 定义哪些 Key 属于 HOST 组
                if (Key == "MEM.Load" || 
                    Key == "MOBO.Temp" || 
                    Key == "DISK.Temp" || 
                    Key == "CASE.Fan") 
                {
                    return "HOST";
                }
                
                // 默认逻辑：取前缀 (例如 CPU.Load -> CPU)
                return Key.Split('.')[0];
            }
        }
    }

    public class ThresholdsSet
    {
        public ValueRange Load { get; set; } = new ValueRange { Warn = 60, Crit = 85 };
        public ValueRange Temp { get; set; } = new ValueRange { Warn = 50, Crit = 70 };
        public ValueRange DiskIOMB { get; set; } = new ValueRange { Warn = 2, Crit = 8 };
        public ValueRange NetUpMB { get; set; } = new ValueRange { Warn = 1, Crit = 2 };
        public ValueRange NetDownMB { get; set; } = new ValueRange { Warn = 2, Crit = 8 };
        public ValueRange DataUpMB { get; set; } = new ValueRange { Warn = 512, Crit = 1024 };
        public ValueRange DataDownMB { get; set; } = new ValueRange { Warn = 2048, Crit = 5096 };
    }

    public class ValueRange
    {
        public double Warn { get; set; } = 0;
        public double Crit { get; set; } = 0;
    }
}