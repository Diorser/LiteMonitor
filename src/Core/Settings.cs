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
        public string PreferredMoboTemp { get; set; } = "";

        // 主窗体所在的屏幕设备名 (用于记忆上次位置)
        public string ScreenDevice { get; set; } = "";

        // ====== 任务栏 ======
        public bool ShowTaskbar { get; set; } = false;
        // ★★★ 新增：横条模式是否跟随任务栏布局？ ★★★
        public bool HorizontalFollowsTaskbar { get; set; } = false;
        public bool HideMainForm { get; set; } = false;
        public bool HideTrayIcon { get; set; } = false;
        public bool TaskbarAlignLeft { get; set; } = true;
        
        // ★★★ 任务栏：自定义布局参数 ★★★
        // 开启后，将忽略预设的"粗体/细体"逻辑，强制使用以下参数
        public bool TaskbarCustomLayout { get; set; } = false; 

        public string TaskbarFontFamily { get; set; } = "Microsoft YaHei UI";
        public float TaskbarFontSize { get; set; } = 10f;
        public bool TaskbarFontBold { get; set; } = true;
        
        // 间距配置 (单位: px, 会自动随 DPI 缩放)
        public int TaskbarItemSpacing { get; set; } = 6;      // 组与组之间的间距
        public int TaskbarInnerSpacing { get; set; } = 8;     // 标签与数值之间的间距
        public int TaskbarVerticalPadding { get; set; } = 3;  // 垂直方向的微调/行间距
        
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

        // 开启后：CPU使用率、CPU频率、内存占用、磁盘读写 将优先从 Windows 计数器读取
        public bool UseWinPerCounters { get; set; } = true;
        
        // ====== 记录与报警 ======
        public float RecordedMaxCpuPower { get; set; } = 65.0f;
        public float RecordedMaxCpuClock { get; set; } = 4200.0f;
        public float RecordedMaxGpuPower { get; set; } = 100.0f;
        public float RecordedMaxGpuClock { get; set; } = 1800.0f;
        
        // ★★★ [新增] FPS 固定最大值 (用于进度条上限，推荐 144) ★★★
        public float RecordedMaxFps { get; set; } = 144.0f;

        // ★★★ [新增] 风扇最大值记录 ★★★
        public float RecordedMaxCpuFan { get; set; } = 4000;
        public float RecordedMaxCpuPump { get; set; } = 5000; // 水冷最大转速 (用于百分比计算)
        public float RecordedMaxGpuFan { get; set; } = 3500;
        public float RecordedMaxChassisFan { get; set; } = 3000;

        public bool MaxLimitTipShown { get; set; } = false;
        
        public bool AlertTempEnabled { get; set; } = true;
        public int AlertTempThreshold { get; set; } = 80;

        public bool WebServerEnabled { get; set; } = false;
        public int WebServerPort { get; set; } = 5000; // 默认端口
        
        public ThresholdsSet Thresholds { get; set; } = new ThresholdsSet();

        [JsonIgnore] public DateTime LastAlertTime { get; set; } = DateTime.MinValue;
        [JsonIgnore] public long SessionUploadBytes { get; set; } = 0;
        [JsonIgnore] public long SessionDownloadBytes { get; set; } = 0;
        [JsonIgnore] private DateTime _lastAutoSave = DateTime.MinValue;

        public Dictionary<string, string> GroupAliases { get; set; } = new Dictionary<string, string>();
        public List<MonitorItemConfig> MonitorItems { get; set; } = new List<MonitorItemConfig>();
        public List<PluginInstanceConfig> PluginInstances { get; set; } = new List<PluginInstanceConfig>();

        /// <param name="keyPrefix">监控项类别前缀（如 "CPU", "GPU"）</param>
        /// <returns>如果有任何匹配的启用项则返回true，否则返回false</returns>
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
                    // ★★★ 优化：Intern 动态生成的 Key，防止 duplicate strings 堆积 ★★★
                    LanguageManager.SetOverride(UIUtils.Intern("Groups." + kv.Key), kv.Value);
            }
            if (MonitorItems != null)
            {
                foreach (var item in MonitorItems)
                {
                    if (!string.IsNullOrEmpty(item.UserLabel))
                        LanguageManager.SetOverride(UIUtils.Intern("Items." + item.Key), item.UserLabel);
                    if (!string.IsNullOrEmpty(item.TaskbarLabel))
                        LanguageManager.SetOverride(UIUtils.Intern("Short." + item.Key), item.TaskbarLabel);
                }
            }
        }

        // ★★★ [新增] 极简样式封装（复制到 Settings 类里） ★★★
        public struct TBStyle { 
            public string Font; public float Size; public bool Bold; 
            public int Gap; public int Inner; public int VOff; 
        }

        public TBStyle GetStyle() {
            // 1. 如果开启自定义，返回配置值
            if (TaskbarCustomLayout) return new TBStyle {
                Font = TaskbarFontFamily, Size = TaskbarFontSize, Bold = TaskbarFontBold,
                Gap = TaskbarItemSpacing, Inner = TaskbarInnerSpacing, VOff = TaskbarVerticalPadding
            };
            // 2. 否则强制返回【标准默认值】（彻底解决样式残留问题）
            return new TBStyle {
                Font = "Microsoft YaHei UI", Size = TaskbarFontBold ? 10f : 9f, Bold = TaskbarFontBold,
                Gap = 6, Inner = TaskbarFontBold ? 10 : 8, VOff = 2 // 这里的 2 就是默认的垂直微调
            };
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

        // ★★★ 优化 1：缓存路径，避免重复 Combine ★★★
        private static readonly string _cachedPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        private static string FilePath => _cachedPath;

        // ★★★ 优化 2：全局单例引用 ★★★
        private static Settings _instance;

        // ★★★ 优化 3：改造 Load 方法为单例模式 ★★★
        public static Settings Load(bool forceReload = false)
        {
            // 如果单例已存在且不强制刷新，直接返回内存对象 (0 IO, 0 GC)
            if (_instance != null && !forceReload) return _instance;

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
                foreach (var item in s.MonitorItems)
                {
                    // ★★★ 修复：只有当 TaskbarSortIndex 未设置(0)时才使用 SortIndex 填充 ★★★
                    // 避免覆盖 DASH.HOST 等已手动指定 TaskbarSortIndex=100 的项
                    if (item.TaskbarSortIndex == 0)
                        item.TaskbarSortIndex = item.SortIndex;
                }
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
            
            // ★★★ 新增：深度字符串去重 (Deep Intern) ★★★
            s.InternAllStrings();
            
            // 赋值单例
            _instance = s;
            return s;
        }

        // ★★★ 新增：辅助方法，清理配置中的重复字符串 ★★★
        private void InternAllStrings()
        {
            // 1. 清理监控项 Keys
            if (MonitorItems != null)
            {
                foreach (var item in MonitorItems)
                {
                    if (item != null)
                    {
                        item.Key = UIUtils.Intern(item.Key);
                        // 如果 MonitorItemConfig 有其他 string 字段(如 Name)，也一并 Intern
                    }
                }
            }

            // 2. 清理硬件标识符 (解决 \\?\storage... 重复问题)
            PreferredDisk = UIUtils.Intern(PreferredDisk);
            LastAutoDisk = UIUtils.Intern(LastAutoDisk);
            PreferredNetwork = UIUtils.Intern(PreferredNetwork);
            LastAutoNetwork = UIUtils.Intern(LastAutoNetwork);
            
            // 3. 清理风扇配置
            PreferredCpuFan = UIUtils.Intern(PreferredCpuFan);
            PreferredCpuPump = UIUtils.Intern(PreferredCpuPump);
            PreferredCaseFan = UIUtils.Intern(PreferredCaseFan);
            PreferredMoboTemp = UIUtils.Intern(PreferredMoboTemp);
            
            TaskbarFontFamily = UIUtils.Intern(TaskbarFontFamily);
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
                    // ★★★ 修复：同步 Unit 配置 ★★★
                    stdItem.UnitPanel = userOldItem.UnitPanel;
                    stdItem.UnitTaskbar = userOldItem.UnitTaskbar;
                    
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

        // ★★★ 优化方案 v2：智能跟随邻居插队 (Smart Neighbor Follow) ★★★
        private void CheckAndAppendMissingItems()
        {
            var temp = new Settings();
            temp.InitDefaultItems();
            
            // 1. 找出用户缺失的新项
            var newItems = temp.MonitorItems
                .Where(std => !MonitorItems.Any(usr => usr.Key.Equals(std.Key, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(std => std.SortIndex) // 按官方顺序一个个处理
                .ToList();

            if (newItems.Count == 0) return;

            bool listChanged = false;

            foreach (var newItem in newItems)
            {
                // ==================================================
                // 步骤 A：处理主面板 (SortIndex) —— 腾位置
                // ==================================================
                // 凡是位置被占了（或在它后面的），统统后移一位
                var conflictingPanelItems = MonitorItems.Where(x => x.SortIndex >= newItem.SortIndex).ToList();
                foreach (var item in conflictingPanelItems) item.SortIndex++;

                // ==================================================
                // 步骤 B：处理任务栏 (TaskbarSortIndex) —— 找大哥
                // ==================================================
                int targetTaskbarIndex = 0;

                // ★★★ 优先使用预设的 TaskbarSortIndex (如果有明确指定) ★★★
                if (newItem.TaskbarSortIndex != 0)
                {
                    targetTaskbarIndex = newItem.TaskbarSortIndex;
                }
                else
                {
                    // 1. 尝试找到这位新人在主面板里的“大哥”（前一个邻居）
                    // 比如 FPS(21) 的大哥可能是 MEM.Load(20)
                    var predecessor = MonitorItems
                        .Where(x => x.SortIndex < newItem.SortIndex) // 找排在它前面的
                        .OrderByDescending(x => x.SortIndex)         // 离它最近的那个
                        .FirstOrDefault();

                    if (predecessor != null)
                    {
                        // 2. 如果找到了大哥，就插在大哥后面
                        targetTaskbarIndex = predecessor.TaskbarSortIndex + 1;
                    }
                    else
                    {
                        // 3. 如果没大哥（说明它是新列表的老大），那就插在最前面 (0)
                        targetTaskbarIndex = 0;
                    }
                }

                // 4. 让任务栏里挡路的人也统统后移
                var conflictingTaskbarItems = MonitorItems.Where(x => x.TaskbarSortIndex >= targetTaskbarIndex).ToList();
                foreach (var item in conflictingTaskbarItems) item.TaskbarSortIndex++;

                // 5. 落座
                newItem.TaskbarSortIndex = targetTaskbarIndex;

                MonitorItems.Add(newItem);
                listChanged = true;
            }

            // 收尾：重新整理一下 List 的物理顺序
            if (listChanged)
            {
                MonitorItems = MonitorItems.OrderBy(x => x.SortIndex).ToList();
            }
        }

        // ★★★ 核心修复：添加全局保存锁，防止重置时被自动保存覆盖 ★★★
        [JsonIgnore]
        public static bool GlobalBlockSave { get; set; } = false;

        public void Save()
        {
            // ★★★ 如果开启了保存锁，直接返回，什么都不做 ★★★
            if (GlobalBlockSave) return;

            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }

        public void InitDefaultItems()
        {
            MonitorItems = new List<MonitorItemConfig>
            {
                // ★★★ [新增] Dashboard Items (默认排在最前) ★★★
                // 默认将 TaskbarLabel 设为空格，以隐藏标签
                new MonitorItemConfig { Key = "DASH.HOST", SortIndex = -10, TaskbarSortIndex = 100, VisibleInPanel = true, TaskbarLabel = " " },
                new MonitorItemConfig { Key = "DASH.Time", SortIndex = -9, TaskbarSortIndex = 200, VisibleInPanel = true, TaskbarLabel = " " },
                new MonitorItemConfig { Key = "DASH.IP",   SortIndex = -8, TaskbarSortIndex = 300, VisibleInPanel = true, TaskbarLabel = " " },
                new MonitorItemConfig { Key = "DASH.Uptime", SortIndex = -7, TaskbarSortIndex = 310, VisibleInPanel = true, TaskbarLabel = " " },
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
                new MonitorItemConfig { Key = "FPS",       SortIndex = 21, VisibleInPanel = false },
                new MonitorItemConfig { Key = "MOBO.Temp", SortIndex = 22, VisibleInPanel = false },
                new MonitorItemConfig { Key = "DISK.Temp", SortIndex = 23, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CASE.Fan",  SortIndex = 24, VisibleInPanel = false },

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
        // ★★★ 核心优化：使用字符串驻留池解决内存浪费 ★★★
       private string _key = "";
        public string Key 
        { 
            get => _key; 
            // ★★★ 修改：使用可回收的 UIUtils.Intern ★★★
            set => _key = UIUtils.Intern(value ?? "");   // 新代码
        }
        public string UserLabel { get; set; } = ""; 
        public string TaskbarLabel { get; set; } = "";

        // ★★★ [新增] 自定义单位配置 ★★★
        // null/"Auto" = 自动(默认), "" = 不显示, "{u}/s" = 自定义格式
        public string UnitPanel { get; set; } = null; 
        public string UnitTaskbar { get; set; } = null;
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
                     Key == "CASE.Fan"|| 
                     Key == "FPS") 
                 { 
                     return "HOST"; 
                 } 
                 
                 // 特殊处理 DASH 组：所有以 DASH. 开头的 Key 都属于 DASH 组
                 if (Key.StartsWith("DASH."))
                 {
                     return "DASH";
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

    public class PluginInstanceConfig
    {
        public string Id { get; set; } = "";
        public string TemplateId { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public int CustomInterval { get; set; } = 0; // 自定义刷新频率 (0 = 使用模版默认)
        
        // 全局参数 (Scope="global")
        public Dictionary<string, string> InputValues { get; set; } = new Dictionary<string, string>();
        
        // 目标列表 (Scope="target")
        // 每个元素是一个 Dictionary，存储该目标的所有 target 参数
        public List<Dictionary<string, string>> Targets { get; set; } = new List<Dictionary<string, string>>();
    }
}