using System;
using System.Drawing;
using System.IO;
using System.Text.Json;

namespace LiteMonitor
{
    public class Settings
    {
        // ====== 主题 / 行为基础 ======
        public string Skin { get; set; } = "DarkFlat_Classic";
        public bool TopMost { get; set; } = true;
        public bool AutoStart { get; set; } = false;
        public int RefreshMs { get; set; } = 700; //刷新时间
        // ★ 新增字段
        public double AnimationSpeed { get; set; } = 0.35; // 平滑速度：0~1，0.3~0.5推荐

        public Point Position { get; set; } = new Point(-1, -1);

        // ====== 新增：用户偏好（从主题里迁移出来的 & 新功能需要） ======
        public bool HorizontalMode { get; set; } = false;
        public double Opacity { get; set; } = 0.85;   // ← 窗口透明度（原 theme.window.opacity）
        public string Language { get; set; } = "";  // ← 语言：zh / en（对应 zh.json / en.json），空字符串表示首次启动
        public bool ClickThrough { get; set; } = false; // ← 鼠标穿透
        public bool AutoHide { get; set; } = true;     // ← 靠边自动隐藏                     
        public bool ClampToScreen { get; set; } = false; // ★限制窗口不能拖出屏幕边界
        public int PanelWidth { get; set; } = 240;   // ← 用户默认宽度
        public double UIScale { get; set; } = 1.0;  // 用户 UI 缩放，默认 1.00

        public string PreferredNetwork { get; set; } = "";  // 手动指定网卡，""=自动
        // 自动识别到的网卡名称缓存 (避免每次启动都全盘扫描)
        public string LastAutoNetwork { get; set; } = "";

        public string PreferredDisk { get; set; } = "";     // 手动指定磁盘，""=自动
        // [新增] 自动识别到的磁盘名称缓存
        public string LastAutoDisk { get; set; } = "";



        public bool ShowTaskbar { get; set; } = false; //开启任务栏显示
        public bool HideMainForm { get; set; } = false; //是否隐藏主窗口
        public bool HideTrayIcon { get; set; } = false; //是否隐藏托盘图标
        public string ScreenDevice { get; set; } = ""; // 手动指定屏幕设备，""=自动

        // ====== 任务栏字体设置（从主题硬编码迁移出来） ======
        public string TaskbarFontFamily { get; set; } = "Microsoft YaHei UI"; // 任务栏字体名称
        public float TaskbarFontSize { get; set; } = 10f; // 任务栏字体大小
        public bool TaskbarFontBold { get; set; } = true; // 任务栏字体是否加粗

        // [新增] 记录历史最高值，用于自适应颜色判断
        // 给一些保守的默认值，防止初次运行分母为0
       
        public float RecordedMaxCpuPower { get; set; } = 65.0f;   // CPU 功耗：65W (覆盖台式机 i5/R5 非K版，以及主流游戏本)
        public float RecordedMaxCpuClock { get; set; } = 4200.0f; // CPU 频率：4.2GHz (现代主流 CPU 的基础睿频线)
        public float RecordedMaxGpuPower { get; set; } = 100.0f;    // GPU 功耗：100W (精准卡位 RTX 4060/3060 移动版及桌面节能卡)
        public float RecordedMaxGpuClock { get; set; } = 1800.0f;   // GPU 频率：1800MHz (兼容绝大多数独显的 Boost 起跳频率)

        public bool MaxLimitTipShown { get; set; } = false;  // 新增：是否已经弹窗提示过用户 (防止重复打扰)

        // [新增] 高温报警配置
        public bool AlertTempEnabled { get; set; } = true; // 总开关
        public int AlertTempThreshold { get; set; } = 80;   // 统一阈值 (默认80)

        // [新增] 报警阈值设置
        public ThresholdsSet Thresholds { get; set; } = new ThresholdsSet();

        // [新增] 防抖记录 (不存入json)
        [System.Text.Json.Serialization.JsonIgnore] 
        public DateTime LastAlertTime { get; set; } = DateTime.MinValue;

        // [新增] 流量统计 - 本次运行 (Session)
        // 加 JsonIgnore 标签，不保存到 Settings.json，每次启动重置为 0
        [System.Text.Json.Serialization.JsonIgnore] 
        public long SessionUploadBytes { get; set; } = 0;

        [System.Text.Json.Serialization.JsonIgnore]
        public long SessionDownloadBytes { get; set; } = 0;

        // [新增] 记录上次保存时间
        [System.Text.Json.Serialization.JsonIgnore] // 别把这个字段存进 json 文件里
        private DateTime _lastAutoSave = DateTime.MinValue;
        // [新增] 辅助方法：更新最大值并自动保存
        public void UpdateMaxRecord(string key, float val)
        {
            bool changed = false;
            // 只有当新值比旧记录大，且不是异常值（比如读取错误变成0或无穷大）时才更新
            if (val <= 0 || float.IsNaN(val) || float.IsInfinity(val)) return;
            // 2. ★★★ 新增：防止传感器抽风产生的离谱数值 ★★★
            // CPU/GPU 频率通常不会超过 10GHz (10000MHz)
            if (key.Contains("Clock") && val > 10000) return; 
            // CPU/GPU 功耗通常不会超过 1000W (除非是工业级，但作为防错阈值够了)
            if (key.Contains("Power") && val > 1000) return;

            if (key == "CPU.Power" && val > RecordedMaxCpuPower) { RecordedMaxCpuPower = val; changed = true; }
            else if (key == "CPU.Clock" && val > RecordedMaxCpuClock) { RecordedMaxCpuClock = val; changed = true; }
            else if (key == "GPU.Power" && val > RecordedMaxGpuPower) { RecordedMaxGpuPower = val; changed = true; }
            else if (key == "GPU.Clock" && val > RecordedMaxGpuClock) { RecordedMaxGpuClock = val; changed = true; }

            if (changed)
    {
            // 策略：如果距离上次保存超过 30 秒，则立即保存
            if ((DateTime.Now - _lastAutoSave).TotalSeconds > 30)
            {
                Save();
                _lastAutoSave = DateTime.Now;
            }
        }
        }

        // ====== 显示项（整组/子项开关）======
        public EnabledSet Enabled { get; set; } = new();

        private static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var s = JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    return s ?? new Settings();
                }
            }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }
    }

     // ★★★ [新增] 阈值结构定义 ★★★
    public class ThresholdsSet
    {
        // 1. 通用负载/内存/显存/频率/功耗 (CPU Load, Temp, Mem Load, etc.)
        public ValueRange Load { get; set; } = new ValueRange { Warn = 60, Crit = 85 };
        public ValueRange Temp { get; set; } = new ValueRange { Warn = 60, Crit = 85 }; // 报警温度 80°C
        //public ValueRange Adaptive { get; set; } = new ValueRange { Warn = 60, Crit = 85 }; //智能计算

        // 2. 磁盘 (Disk R/W 共享阈值，单位 MB/s)
        public ValueRange DiskIOMB { get; set; } = new ValueRange { Warn = 2, Crit = 8 }; // 10MB/s and 50MB/s

        // 3. 网络速率 (NET Up/Down 分离，单位 MB/s)
        public ValueRange NetUpMB { get; set; } = new ValueRange { Warn = 1, Crit = 2 };     // 上传：1MB/s and 2MB/s
        public ValueRange NetDownMB { get; set; } = new ValueRange { Warn = 2, Crit = 8 };   // 下载：2MB/s and 8MB/s

        // 4. 流量总量 (Data Up/Down 分离，单位 MB)
        public ValueRange DataUpMB { get; set; } = new ValueRange { Warn = 512, Crit = 1024 };    // 上传总量：0.5GB and 2GB
        public ValueRange DataDownMB { get; set; } = new ValueRange { Warn = 2048, Crit = 5096 };  // 下载总量：5GB and 15GB

        
    }

    public class ValueRange
    {
        public double Warn { get; set; } = 0;
        public double Crit { get; set; } = 0;
    }
    /// <summary>
    /// 显示项（整组/子项开关）
    /// </summary>
    public class EnabledSet
    {
        public bool CpuLoad { get; set; } = true;
        public bool CpuTemp { get; set; } = true;
        public bool CpuClock { get; set; } = false;
        public bool CpuPower { get; set; } = false;
        public bool GpuLoad { get; set; } = true;
        public bool GpuTemp { get; set; } = true;
        public bool GpuVram { get; set; } = true;

        public bool GpuClock { get; set; } = false;
        public bool GpuPower { get; set; } = false;

        public bool MemLoad { get; set; } = true;

        // ★ 磁盘与网络：用于“整组隐藏”判断（DISK / NET）
        public bool DiskRead { get; set; } = true;
        public bool DiskWrite { get; set; } = true;

        public bool NetUp { get; set; } = true;
        public bool NetDown { get; set; } = true;
        // [新增] 今日流量统计开关
        public bool TrafficDay { get; set; } = true;
    }
}
