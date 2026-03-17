using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.Text.Json.Serialization;
using LiteMonitor.src.Core;
using LiteMonitor.src.Plugins;
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
        public bool AutoCheckUpdate { get; set; } = true; // ★★★ [新增] 自动检查更新 ★★★
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
        public bool ShowTaskbar { get; set; } = true;
        // ★★★ 新增：横条模式是否跟随任务栏布局？ ★★★
        public bool HorizontalFollowsTaskbar { get; set; } = false;
        public bool HideMainForm { get; set; } = false;
        public bool HideTrayIcon { get; set; } = false;
        public bool TaskbarAlignLeft { get; set; } = true;
        
        // ★★★ 任务栏：预设模式选择 (1=Bold/Big, 0=Regular/Small) ★★★
        // Default = -1 用于检测老用户迁移
        public int TaskbarPresetStyle { get; set; } = -1;

        // ★★★ 任务栏：自定义布局参数 ★★★
        // 开启后，将忽略预设的"粗体/细体"逻辑，强制使用以下参数
        public bool TaskbarCustomLayout { get; set; } = false; 

        public string TaskbarFontFamily { get; set; } = DEFAULT_TB_FONT;
        public float TaskbarFontSize { get; set; } = DEFAULT_TB_SIZE_BOLD;
        public bool TaskbarFontBold { get; set; } = true;
        
        // 间距配置 (单位: px, 会自动随 DPI 缩放)
        public int TaskbarItemSpacing { get; set; } = DEFAULT_TB_GAP;      // 组与组之间的间距
        public int TaskbarInnerSpacing { get; set; } = DEFAULT_TB_INNER_BOLD;     // 标签与数值之间的间距
        public int TaskbarVerticalPadding { get; set; } = DEFAULT_TB_VOFF;  // 垂直方向的微调/行间距
        
        // ★★★ [新增] 横条/桌面模式 间距配置 ★★★
        public int HorizontalItemSpacing { get; set; } = 12;  // 组间距 (默认 12)
        public int HorizontalInnerSpacing { get; set; } = 8;  // 标签与值间距 (默认 8)
        public bool HorizontalSingleLine { get; set; } = false; // [新增] 横条模式单行显示

        // ★★★ 常量定义：用于 GetStyle 中的默认策略 ★★★
        public const string DEFAULT_TB_FONT = "Microsoft YaHei UI"; // 任务栏默认字体（兼容多语言环境，系统自带）
        public const float DEFAULT_TB_SIZE_BOLD = 10f; // 粗字模式默认字号（提升可读性）
        public const float DEFAULT_TB_SIZE_REGULAR = 9f; // 细字模式默认字号（节省空间，更精致）
        public const int DEFAULT_TB_GAP = 6; // 任务栏组间距默认值（不同监控项之间的距离）
        public const int DEFAULT_TB_INNER_BOLD = 8; // 粗字模式标签与值间距默认值（更宽的间距适配粗体）
        public const int DEFAULT_TB_INNER_REGULAR = 6; // 细字模式标签与值间距默认值（紧凑布局）
        public const int DEFAULT_TB_VOFF = 2; // 任务栏垂直内边距默认值（用于垂直居中微调）

        
        // ★★★ 新增：指定任务栏显示的屏幕设备名 ("" = 自动/主屏) ★★★
        public string TaskbarMonitorDevice { get; set; } = "";

        // 任务栏行为配置
        public bool TaskbarClickThrough { get; set; } = false;     // 鼠标穿透
        public bool TaskbarSingleLine { get; set; } = false;// 单行显示
        public bool TaskbarHoverShowAll { get; set; } = true; // [新增] 悬浮显示所有监控项
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
        public int MemoryDisplayMode { get; set; } = 1;

        // ★ 2. 运行时缓存：存储探测到的总容量 (GB)
        [JsonIgnore] public static float DetectedRamTotalGB { get; set; } = 0;
        [JsonIgnore] public static float DetectedGpuVramTotalGB { get; set; } = 0;

        // 开启后：CPU使用率、CPU频率、内存占用、磁盘读写 将优先从 Windows 计数器读取
        public bool UseWinPerCounters { get; set; } = true;

        // ★★★ [新增] 忽略 SMB (局域网文件传输) 流量 ★★★
        public bool IgnoreSmbTraffic { get; set; } = true;
        
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
        public string WebServerPassword { get; set; } = ""; // 访问密码
        
        public ThresholdsSet Thresholds { get; set; } = new ThresholdsSet();

        [JsonIgnore] public DateTime LastAlertTime { get; set; } = DateTime.MinValue;
        [JsonIgnore] public long SessionUploadBytes { get; set; } = 0;
        [JsonIgnore] public long SessionDownloadBytes { get; set; } = 0;
        [JsonIgnore] public DateTime LastAutoSaveTime { get; set; } = DateTime.MinValue;

        public Dictionary<string, string> GroupAliases { get; set; } = new Dictionary<string, string>();
        public List<MonitorItemConfig> MonitorItems { get; set; } = new List<MonitorItemConfig>();
        public List<PluginInstanceConfig> PluginInstances { get; set; } = new List<PluginInstanceConfig>();

        // ★★★ [新增] 极简样式封装（复制到 Settings 类里） ★★★
        public struct TBStyle { 
            public string Font; public float Size; public bool Bold; 
            public int Gap; public int Inner; public int VOff; 
        }

        // ★★★ 核心修复：添加全局保存锁，防止重置时被自动保存覆盖 ★★★
        [JsonIgnore]
        public static bool GlobalBlockSave 
        { 
            get => SettingsHelper.GlobalBlockSave; 
            set => SettingsHelper.GlobalBlockSave = value; 
        }

        // ★★★ 优化 2：全局单例引用 ★★★
        private static Settings _instance;

        // ★★★ 优化 3：改造 Load 方法为单例模式 ★★★
        public static Settings Load(bool forceReload = false)
        {
            // 如果单例已存在且不强制刷新，直接返回内存对象 (0 IO, 0 GC)
            if (_instance != null && !forceReload) return _instance;

            // 委托给 SettingsHelper 加载
            _instance = SettingsHelper.Load(forceReload);
            
            // 兼容旧配置：如果 WebServerPassword 为 null，初始化为空字符串
            if (_instance.WebServerPassword == null)
            {
                _instance.WebServerPassword = "";
            }

            return _instance;
        }

        // Helper method to get taskbar style moved to SettingsHelper

        public Settings DeepClone()
        {
            var json = System.Text.Json.JsonSerializer.Serialize(this);
            return System.Text.Json.JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        }
    }

    public class MonitorItemConfig
    {
        // ★★★ 核心优化：使用字符串驻留池解决内存浪费 ★★★
        private string _key = "";

        // 极简缓存：复用首个实例作为原型 (0 GC)
        private static readonly ConcurrentDictionary<string, MonitorItemConfig> _protoCache = new ConcurrentDictionary<string, MonitorItemConfig>();

        // [Optimization] Cache keys to avoid string concatenation in hot paths
        [JsonIgnore] public string CachedPropLabelKey { get; private set; } = "";
        [JsonIgnore] public string CachedPropShortLabelKey { get; private set; } = "";
        [JsonIgnore] public string CachedUIGroup { get; private set; } = "";
        [JsonIgnore] public string CachedItemsKey { get; private set; } = ""; // Items.Key
        [JsonIgnore] public string CachedGroupsKey { get; private set; } = ""; // Groups.UIGroup
        [JsonIgnore] public string CachedDashColorKey { get; private set; } = "";
        [JsonIgnore] public string CachedDashUnitKey { get; private set; } = "";

        public string Key 
        { 
            get => _key; 
            set 
            {
                string k = UIUtils.Intern(value ?? "");
                if (_key == k) return;
                _key = k;

                // 极简模式：直接从缓存的原型对象拷贝所有属性，无需额外对象分配
                if (_protoCache.TryGetValue(k, out var p))
                {
                    CachedPropLabelKey = p.CachedPropLabelKey;
                    CachedPropShortLabelKey = p.CachedPropShortLabelKey;
                    CachedItemsKey = p.CachedItemsKey;
                    CachedUIGroup = p.CachedUIGroup;
                    CachedGroupsKey = p.CachedGroupsKey;
                    CachedDashColorKey = p.CachedDashColorKey;
                    CachedDashUnitKey = p.CachedDashUnitKey;
                    return;
                }

                // 首次计算
                CachedPropLabelKey = UIUtils.Intern("PROP.Label." + _key);
                CachedPropShortLabelKey = UIUtils.Intern("PROP.ShortLabel." + _key);
                CachedItemsKey = UIUtils.Intern("Items." + _key);

                if (_key == "MEM.Load" || _key == "MOBO.Temp" || _key == "DISK.Temp" || _key == "CASE.Fan"|| _key == "FPS") 
                    CachedUIGroup = "HOST"; 
                else if (_key.StartsWith("DASH."))
                {
                    CachedUIGroup = "DASH";
                    string sub = _key.Substring(5);
                    CachedDashColorKey = UIUtils.Intern(sub + ".Color");
                    CachedDashUnitKey = UIUtils.Intern(sub + ".Unit");
                }
                else
                    CachedUIGroup = UIUtils.Intern(_key.Split('.')[0]); 
                
                CachedGroupsKey = UIUtils.Intern("Groups." + CachedUIGroup);
                
                // 存入缓存
                _protoCache[k] = this;
            }
        }
        // ★★★ [优化] 分离用户配置与系统动态值 ★★★
        // UserLabel: 用户手动设置的名称 (持久化)。为空表示跟随系统。
        public string UserLabel { get; set; } = ""; 
        public string TaskbarLabel { get; set; } = "";

        // DynamicLabel: 插件运行时计算的名称 (不持久化)。
        [JsonIgnore]
        public string DynamicLabel { get; set; } = "";
        
        [JsonIgnore]
        public string DynamicTaskbarLabel { get; set; } = "";

        // DisplayLabel: 最终显示名称 (优先显示用户设置，否则显示动态值)
        [JsonIgnore]
        public string DisplayLabel => !string.IsNullOrEmpty(UserLabel) ? UserLabel : DynamicLabel;
        
        [JsonIgnore]
        public string DisplayTaskbarLabel => !string.IsNullOrEmpty(TaskbarLabel) ? TaskbarLabel : DynamicTaskbarLabel;

        // ★★★ [新增] 自定义单位配置 ★★★
        // null/"Auto" = 自动(默认), "" = 不显示, "{u}/s" = 自定义格式
        public string UnitPanel { get; set; } = null; 
        public string UnitTaskbar { get; set; } = null;

        // ★★★ [新增] 传感器ID强制覆盖 ★★★
        // 允许用户手动指定 "/gpu-nvidia/0/power/0" 等 ID
        public string OverrideSensorId { get; set; } = "";

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
                // [Optimization] Return cached value if available
                if (!string.IsNullOrEmpty(CachedUIGroup)) return CachedUIGroup;

                // Fallback for uninitialized state (should be rare)
                if (Key == "MEM.Load" || 
                    Key == "MOBO.Temp" || 
                    Key == "DISK.Temp" || 
                    Key == "CASE.Fan"|| 
                    Key == "FPS") 
                { 
                    return "HOST"; 
                } 
                
                if (Key.StartsWith("DASH."))
                {
                    return "DASH";
                }

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
        public bool Enabled { get; set; } = false;
        public int CustomInterval { get; set; } = 0; // 自定义刷新频率 (0 = 使用模版默认)
        
        // 全局参数 (Scope="global")
        public Dictionary<string, string> InputValues { get; set; } = new Dictionary<string, string>();
        
        // 目标列表 (Scope="target")
        // 每个元素是一个 Dictionary，存储该目标的所有 target 参数
        public List<Dictionary<string, string>> Targets { get; set; } = new List<Dictionary<string, string>>();

        // [Optimization] Runtime key cache to avoid redundant string generation in PluginExecutor
        // Map: TargetIndex_OutputKey -> KeysBundle
        [JsonIgnore] 
        public ConcurrentDictionary<string, PluginOutputKeys> KeyCache { get; } = new ConcurrentDictionary<string, PluginOutputKeys>();
    }
}
