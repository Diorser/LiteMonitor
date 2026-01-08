using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class SystemHardwarPage : SettingsPageBase
    {
        private Panel _container;
        private bool _isLoaded = false;
        private string _originalLanguage;

        public SystemHardwarPage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);
            _container = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20) }; 
            this.Controls.Add(_container);
        }

        public override void OnShow()
        {
            base.OnShow();
            if (Config == null || _isLoaded) return;

            _container.SuspendLayout();
            _container.Controls.Clear();
            _originalLanguage = Config.Language;

            CreateSourceCard();
            CreateCalibrationCard();
            CreateSystemCard();

            _container.ResumeLayout();
            _isLoaded = true;
        }

        private void CreateSystemCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.SystemSettings"));

           // 1. 语言选择 (清理了 Auto 逻辑)
            var langs = new System.Collections.Generic.List<string>();
            string langDir = Path.Combine(AppContext.BaseDirectory, "resources/lang");
            if (Directory.Exists(langDir))
            {
                // 获取所有真实存在的语言文件 (如 EN, ZH, JA)
                langs.AddRange(Directory.EnumerateFiles(langDir, "*.json")
                    .Select(f => Path.GetFileNameWithoutExtension(f).ToUpper()));
            }
            
            AddCombo(group, "Menu.Language", langs,
                // Getter: 如果 Config 为空(首次运行), 显示当前实际生效的语言; 否则显示配置值
                () => string.IsNullOrEmpty(Config.Language) 
                        ? LanguageManager.CurrentLang.ToUpper() 
                        : Config.Language.ToUpper(),
                v => Config.Language = v.ToLower()
            );

            // 2. 开机自启
            AddBool(group, "Menu.AutoStart", () => Config.AutoStart, v => Config.AutoStart = v);

            // 3. 隐藏托盘 (带联动逻辑)
            AddBool(group, "Menu.HideTrayIcon", 
                () => Config.HideTrayIcon, 
                v => Config.HideTrayIcon = v,
                chk => chk.CheckedChanged += (s, e) => EnsureSafeVisibility(null, chk, null)
            );

            AddGroupToPage(group);
        }

        private void CreateCalibrationCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.Calibration"));
            string suffix = " (" + LanguageManager.T("Menu.MaxLimits") + ")";

            // 辅助闭包：拼接标题
            void AddCalib(string key, string unit, Func<float> get, Action<float> set)
            {
                // 使用工厂方法 AddNumberDouble
                // 注意：Settings item 的 titleKey 机制是取翻译，这里我们手动拼接了 suffix
                // 所以我们稍微绕过工厂的 titleKey 翻译，或者直接传拼接好的 string 作为一个假 key (因为 LanguageManager 如果找不到 key 会返回 key 本身)
                
                string title = LanguageManager.T(key) + suffix; 
                
                // 同时在 Getter/Setter 中强制转为 int，实现“不显示也不保存小数”的效果
                var input = AddNumberDouble(group, "RAW_TITLE_HACK", unit, 
                    () => (int)get(),        // Getter: float -> int (截断小数，界面显示 "65")
                    v => set((float)(int)v)  // Setter: double -> int -> float (输入 65.5 -> 存为 65.0)
                );
                
                // 修正 Label 的文字 (因为工厂里把它当 Key 去翻译了)
                if(input.Parent.Controls[0] is Label lbl) lbl.Text = title;
            }
            group.AddFullItem(new LiteNote(LanguageManager.T("Menu.CalibrationTip"), 0));
            
            // 功耗与频率
            AddCalib("Items.CPU.Power", "W",   () => Config.RecordedMaxCpuPower, v => Config.RecordedMaxCpuPower = v);
            AddCalib("Items.CPU.Clock", "MHz", () => Config.RecordedMaxCpuClock, v => Config.RecordedMaxCpuClock = v);
            AddCalib("Items.GPU.Power", "W",   () => Config.RecordedMaxGpuPower, v => Config.RecordedMaxGpuPower = v);
            AddCalib("Items.GPU.Clock", "MHz", () => Config.RecordedMaxGpuClock, v => Config.RecordedMaxGpuClock = v);

            // [新增] 风扇转速最大值设置
            AddCalib("Items.CPU.Fan",   "RPM", () => Config.RecordedMaxCpuFan,     v => Config.RecordedMaxCpuFan = v);
            AddCalib("Items.GPU.Fan",   "RPM", () => Config.RecordedMaxGpuFan,     v => Config.RecordedMaxGpuFan = v);
            AddCalib("Items.CASE.Fan",  "RPM", () => Config.RecordedMaxChassisFan, v => Config.RecordedMaxChassisFan = v);

            AddGroupToPage(group);
        }

        private void CreateSourceCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.HardwareSettings"));
            string strAuto = LanguageManager.T("Menu.Auto");

            // 1. 磁盘源
            var disks = HardwareMonitor.ListAllDisks();
            disks.Insert(0, strAuto);
            AddCombo(group, "Menu.DiskSource", disks,
                () => string.IsNullOrEmpty(Config.PreferredDisk) ? strAuto : Config.PreferredDisk,
                v => Config.PreferredDisk = (v == strAuto) ? "" : v
            );

            // 2. 网络源
            var nets = HardwareMonitor.ListAllNetworks();
            nets.Insert(0, strAuto);
            AddCombo(group, "Menu.NetworkSource", nets,
                () => string.IsNullOrEmpty(Config.PreferredNetwork) ? strAuto : Config.PreferredNetwork,
                v => Config.PreferredNetwork = (v == strAuto) ? "" : v
            );

            // [新增] 3. 风扇源
            // 获取系统内所有识别到的风扇名称
            var fans = HardwareMonitor.ListAllFans(); 
            // 这里的 fans 列表会被下拉框直接引用，Insert 0 位置会影响所有引用，但因为每次 Update 都是新的 List 所以没问题
            fans.Insert(0, strAuto);

            // CPU 风扇首选
            // 这里复用了 "Items.CPU.Fan" 作为标题 (即 "CPU风扇")
            AddCombo(group, "Items.CPU.Fan", fans,
                () => string.IsNullOrEmpty(Config.PreferredCpuFan) ? strAuto : Config.PreferredCpuFan,
                v => Config.PreferredCpuFan = (v == strAuto) ? "" : v
            );

            // 机箱风扇首选
            // 复用 "Items.CASE.Fan" 作为标题 (即 "机箱风扇")
            AddCombo(group, "Items.CASE.Fan", fans,
                () => string.IsNullOrEmpty(Config.PreferredCaseFan) ? strAuto : Config.PreferredCaseFan,
                v => Config.PreferredCaseFan = (v == strAuto) ? "" : v
            );

            // 4. 其他设置
            AddBool(group, "Menu.UseSystemCpuLoad", () => Config.UseSystemCpuLoad, v => Config.UseSystemCpuLoad = v);
            
            // 5. 刷新率
            int[] rates = { 100, 200, 300, 500, 600, 700, 800, 1000, 1500, 2000, 3000 };
            AddCombo(group, "Menu.Refresh", rates.Select(r => r + " ms"),
                () => Config.RefreshMs + " ms",
                v => {
                    int val = UIUtils.ParseInt(v);
                    Config.RefreshMs = val < 50 ? 1000 : val;
                }
            );
            group.AddFullItem(new LiteNote(LanguageManager.T("Menu.UseSystemCpuLoadTip"), 0));
            
            AddGroupToPage(group);
        }

        private void AddGroupToPage(LiteSettingsGroup group)
        {
            var wrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 20) };
            wrapper.Controls.Add(group);
            _container.Controls.Add(wrapper);
            _container.Controls.SetChildIndex(wrapper, 0);
        }
    }
}