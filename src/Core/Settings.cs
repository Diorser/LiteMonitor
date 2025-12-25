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
        public string ScreenDevice { get; set; } = "";

        // ====== 任务栏 ======
        public bool ShowTaskbar { get; set; } = false;
        public bool HideMainForm { get; set; } = false;
        public bool HideTrayIcon { get; set; } = false;
        public bool TaskbarAlignLeft { get; set; } = true;
        public string TaskbarFontFamily { get; set; } = "Microsoft YaHei UI";
        public float TaskbarFontSize { get; set; } = 10f;
        public bool TaskbarFontBold { get; set; } = true;
        
        // 任务栏行为配置
        public bool TaskbarClickThrough { get; set; } = false;     // 鼠标穿透

        // ====== 任务栏：高级自定义外观 ======
        public bool TaskbarCustomStyle { get; set; } = false; // 总开关

        // 颜色配置 (Hex格式)
        public string TaskbarColorLabel { get; set; } = "#141414"; // 标签颜色
        public string TaskbarColorSafe { get; set; } = "#008040";  // 正常 (淡绿)
        public string TaskbarColorWarn { get; set; } = "#B57500";  // 警告 (金黄)
        public string TaskbarColorCrit { get; set; } = "#C03030";  // 严重 (橙红)
        public string TaskbarColorBg { get; set; } = "#D2D2D2";    // 防杂边背景色 (透明键)

        

        // ====== 记录与报警 ======
        public float RecordedMaxCpuPower { get; set; } = 65.0f;
        public float RecordedMaxCpuClock { get; set; } = 4200.0f;
        public float RecordedMaxGpuPower { get; set; } = 100.0f;
        public float RecordedMaxGpuClock { get; set; } = 1800.0f;
        public bool MaxLimitTipShown { get; set; } = false;
        
        public bool AlertTempEnabled { get; set; } = true;
        public int AlertTempThreshold { get; set; } = 80;
        
        public ThresholdsSet Thresholds { get; set; } = new ThresholdsSet();

        [JsonIgnore] 
        public DateTime LastAlertTime { get; set; } = DateTime.MinValue;
        [JsonIgnore] 
        public long SessionUploadBytes { get; set; } = 0;
        [JsonIgnore]
        public long SessionDownloadBytes { get; set; } = 0;
        [JsonIgnore] 
        private DateTime _lastAutoSave = DateTime.MinValue;

        // ★★★★★ [核心重构] 监控项列表 ★★★★★
        // 替代了原来的 EnabledSet，支持排序、自定义名称、前后端分离
        public Dictionary<string, string> GroupAliases { get; set; } = new Dictionary<string, string>();
        public List<MonitorItemConfig> MonitorItems { get; set; } = new List<MonitorItemConfig>();

        // 辅助方法：判断某类监控是否开启 (用于 HardwareMonitor)
        // 例如 IsAnyEnabled("CPU") 会检查 CPU.Load, CPU.Temp 等是否有任意一个开启
        public bool IsAnyEnabled(string keyPrefix)
        {
            return MonitorItems.Any(x => x.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase) && (x.VisibleInPanel || x.VisibleInTaskbar));
        }

        // ★★★ 核心新增：同步自定义名称到翻译器 ★★★
        public void SyncToLanguage()
        {
            LanguageManager.ClearOverrides();

            // 1. 同步组名
            if (GroupAliases != null)
            {
                foreach (var kv in GroupAliases)
                {
                    LanguageManager.SetOverride("Groups." + kv.Key, kv.Value);
                }
            }

            // 2. 同步监控项名
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
            
            // 简单防错
            if (key.Contains("Clock") && val > 10000) return; 
            if (key.Contains("Power") && val > 1000) return;

            if (key == "CPU.Power" && val > RecordedMaxCpuPower) { RecordedMaxCpuPower = val; changed = true; }
            else if (key == "CPU.Clock" && val > RecordedMaxCpuClock) { RecordedMaxCpuClock = val; changed = true; }
            else if (key == "GPU.Power" && val > RecordedMaxGpuPower) { RecordedMaxGpuPower = val; changed = true; }
            else if (key == "GPU.Clock" && val > RecordedMaxGpuClock) { RecordedMaxGpuClock = val; changed = true; }

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
            // ★★★ 核心修复：确保集合不为 null，防止 OnShow 崩溃 ★★★
            if (s.GroupAliases == null) s.GroupAliases = new Dictionary<string, string>();
            
            if (s.MonitorItems == null || s.MonitorItems.Count == 0)
            {
                s.InitDefaultItems();
            }
            
            // ★★★ 核心修复：同步自定义名称到翻译器 ★★★
            s.SyncToLanguage();
            
            return s;
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

        // 初始化默认监控项 (迁移逻辑)
        private void InitDefaultItems()
        {
            MonitorItems = new List<MonitorItemConfig>
            {
                // Key 必须对应 HardwareMonitor.Logic.cs 中的解析键值
                // SortIndex 决定默认排序
                new MonitorItemConfig { Key = "CPU.Load",  SortIndex = 0, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "CPU.Temp",  SortIndex = 1, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "CPU.Clock", SortIndex = 2, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CPU.Power", SortIndex = 3, VisibleInPanel = false },

                new MonitorItemConfig { Key = "GPU.Load",  SortIndex = 10, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "GPU.Temp",  SortIndex = 11, VisibleInPanel = true },
                new MonitorItemConfig { Key = "GPU.VRAM",  SortIndex = 12, VisibleInPanel = true },
                new MonitorItemConfig { Key = "GPU.Clock", SortIndex = 13, VisibleInPanel = false },
                new MonitorItemConfig { Key = "GPU.Power", SortIndex = 14, VisibleInPanel = false },

                new MonitorItemConfig { Key = "MEM.Load",  SortIndex = 20, VisibleInPanel = true, VisibleInTaskbar = true },

                new MonitorItemConfig { Key = "DISK.Read", SortIndex = 30, VisibleInPanel = true },
                new MonitorItemConfig { Key = "DISK.Write",SortIndex = 31, VisibleInPanel = true },

                new MonitorItemConfig { Key = "NET.Up",    SortIndex = 40, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "NET.Down",  SortIndex = 41, VisibleInPanel = true, VisibleInTaskbar = true },
                
                new MonitorItemConfig { Key = "DATA.DayUp",  SortIndex = 50, VisibleInPanel = true },
                new MonitorItemConfig { Key = "DATA.DayDown",SortIndex = 51, VisibleInPanel = true }
            };
        }
    }

    // ★★★ [新类] 单个监控项配置 ★★★
    public class MonitorItemConfig
    {
        public string Key { get; set; } = "";
        
        // 用户自定义名称 (如果为空，UI层会使用 LanguageManager 的默认翻译)
        public string UserLabel { get; set; } = ""; 
        // ★新增：任务栏显示的简短名称 (如 "U:20%")
        public string TaskbarLabel { get; set; } = "";
        
        // 主面板开关
        public bool VisibleInPanel { get; set; } = true;
        
        // 任务栏开关 (实现你的需求：任务栏只看重要项)
        public bool VisibleInTaskbar { get; set; } = false;
        
        // 排序索引 (未来拖拽排序用)
        public int SortIndex { get; set; } = 0;
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