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
        public string Language { get; set; } = "zh";  // ← 语言：zh / en（对应 zh.json / en.json）
        public bool ClickThrough { get; set; } = false; // ← 鼠标穿透
        public bool AutoHide { get; set; } = true;     // ← 靠边自动隐藏                     
        public bool ClampToScreen { get; set; } = false; // ★限制窗口不能拖出屏幕边界
        public int PanelWidth { get; set; } = 240;   // ← 用户默认宽度
        public double UIScale { get; set; } = 1.0;  // 用户 UI 缩放，默认 1.00

        public string PreferredNetwork { get; set; } = "";  // 手动指定网卡，""=自动
        public string PreferredDisk { get; set; } = "";     // 手动指定磁盘，""=自动

        public bool ShowTaskbar { get; set; } = false; //开启任务栏显示
        public bool HideMainForm { get; set; } = false; //是否隐藏主窗口



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

    public class EnabledSet
    {
        public bool CpuLoad { get; set; } = true;
        public bool CpuTemp { get; set; } = true;

        public bool GpuLoad { get; set; } = true;
        public bool GpuTemp { get; set; } = true;
        public bool GpuVram { get; set; } = true;

        public bool MemLoad { get; set; } = true;

        // ★ 磁盘与网络：用于“整组隐藏”判断（DISK / NET）
        public bool DiskRead { get; set; } = true;
        public bool DiskWrite { get; set; } = true;

        public bool NetUp { get; set; } = true;
        public bool NetDown { get; set; } = true;
    }
}
