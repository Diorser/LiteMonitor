using System;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class SystemHardwarPage : SettingsPageBase
    {
        private Panel _container;
        
        // ★★★ 修复：类型更正为 LiteComboBox ★★★
        private LiteComboBox _cbDisk, _cbNet, _cbGpu, _cbCpuTemp, _cbMobo;
        private LiteComboBox _cbFanCpu, _cbFanPump, _cbFanCase;

        public SystemHardwarPage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);
            _container = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20) }; 
            this.Controls.Add(_container);

            InitializeUI();
        }

        private void InitializeUI()
        {
            CreateSourceCard();
            CreateCalibrationCard();
            CreateSystemCard();
        }

        public override void OnShow()
        {
            base.OnShow();
            if (Config == null) return;
            PopulateAsyncData();
        }

        // 将 PopulateAsyncData 改为“批量处理”模式
        private async void PopulateAsyncData()
        {
            try 
            {
                string strAuto = LanguageManager.T("Menu.Auto");

                // 1. 并行等待所有数据返回 (使用 HardwareScanner)
                var taskDisks = Task.Run(() => HardwareScanner.ListAllDisks(HardwareMonitor.Instance.ComputerInstance));
                var taskNets  = Task.Run(() => HardwareScanner.ListAllNetworks(HardwareMonitor.Instance.ComputerInstance));
                var taskGpus  = Task.Run(() => HardwareMonitor.ListAllGpuOptions());
                var taskCpuTemps = Task.Run(() => HardwareScanner.ListAllCpuTempOptions(HardwareMonitor.Instance.ComputerInstance, HardwareMonitor.Instance.SyncLock));
                var taskFans  = Task.Run(() => HardwareScanner.ListAllFans(HardwareMonitor.Instance.ComputerInstance, HardwareMonitor.Instance.SyncLock));
                var taskMobo  = Task.Run(() => HardwareScanner.ListAllMoboTemps(HardwareMonitor.Instance.ComputerInstance, HardwareMonitor.Instance.SyncLock));

                await Task.WhenAll(taskDisks, taskNets, taskGpus, taskCpuTemps, taskFans, taskMobo);

                // 2. ★★★ 锁定全局布局 (防止每填一个框就重绘一次) ★★★
                this.SuspendLayout();
                
            
                // 定义一个同步填充的 Action，避免重复代码
                void FillSync(LiteComboBox combo, List<string> data, string currentVal)
                {
                    if (combo == null || combo.Inner.Items.Count > 2) return;

                    var fullList = new List<string>(data);
                    fullList.Insert(0, strAuto);

                    combo.Inner.BeginUpdate(); // 锁定 ComboBox 自身
                    combo.Inner.Items.Clear();
                    foreach (var item in fullList) combo.Inner.Items.Add(item);

                    if (!string.IsNullOrEmpty(currentVal) && fullList.Contains(currentVal))
                        combo.Inner.SelectedItem = currentVal;
                    else
                        combo.Inner.SelectedIndex = 0;
                    
                    combo.Inner.EndUpdate(); // 解锁 ComboBox
                }

                void FillGpuSync(LiteComboBox combo, List<HardwareScanner.GpuOption> data, string currentVal)
                {
                    if (combo == null || combo.Inner.Items.Count > 2) return;

                    HardwareScanner.GpuOption? ResolveCurrent()
                    {
                        if (string.IsNullOrWhiteSpace(currentVal)) return null;

                        var byValue = data.FirstOrDefault(x =>
                            string.Equals(x.Value, currentVal, StringComparison.OrdinalIgnoreCase));
                        if (byValue != null) return byValue;

                        var byName = data.Where(x =>
                            string.Equals(x.Name, currentVal, StringComparison.OrdinalIgnoreCase)).ToList();
                        return byName.Count == 1 ? byName[0] : null;
                    }

                    combo.Inner.BeginUpdate();
                    combo.Inner.Items.Clear();
                    combo.AddItem(strAuto, "");
                    foreach (var item in data) combo.AddItem(item.Label, item.Value);

                    var selected = ResolveCurrent();
                    combo.SelectValue(selected?.Value ?? "");
                    combo.Inner.EndUpdate();
                }

                void FillSensorSync(LiteComboBox combo, List<HardwareScanner.SensorOption> data, string currentVal)
                {
                    if (combo == null || combo.Inner.Items.Count > 2) return;

                    combo.Inner.BeginUpdate();
                    combo.Inner.Items.Clear();
                    combo.AddItem(strAuto, "");
                    foreach (var item in data) combo.AddItem(item.Label, item.Value);

                    combo.SelectValue(currentVal ?? "");
                    combo.Inner.EndUpdate();
                }

                // 3. 瞬间填入所有数据 (因为布局被挂起，用户看不见中间过程)
                FillSync(_cbDisk, taskDisks.Result, Config.PreferredDisk);
                FillSync(_cbNet, taskNets.Result, Config.PreferredNetwork);
                FillGpuSync(_cbGpu, taskGpus.Result, Config.PreferredGpu);
                FillSensorSync(_cbCpuTemp, taskCpuTemps.Result, Config.PreferredCpuTemp);
                FillSync(_cbMobo, taskMobo.Result, Config.PreferredMoboTemp);
                
                // Fan 的数据是复用的
                FillSync(_cbFanCpu, taskFans.Result, Config.PreferredCpuFan);
                FillSync(_cbFanPump, taskFans.Result, Config.PreferredCpuPump);
                FillSync(_cbFanCase, taskFans.Result, Config.PreferredCaseFan);
            }
            catch (Exception ex)
            {
                // 记录日志，或者只是简单地忽略（硬件读取失败不应该影响用户进入设置）
                Console.WriteLine("硬件列表加载失败: " + ex.Message);
            }
            finally
            {
                // 4. 恢复布局 (此时所有控件已就绪，一次性渲染)
                this.ResumeLayout(true);
            }
        }

        private void CreateSourceCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.HardwareSettings"));
            string strAuto = LanguageManager.T("Menu.Auto");
            
            group.AddToggle(this, "Menu.UseWinPerCounters", () => Config?.UseWinPerCounters ?? false, v => { if(Config!=null) Config.UseWinPerCounters = v; });
            
            // ★★★ [新增] 忽略 SMB 流量开关 ★★★
            // 直接使用中文作为 Key，如果 LanguageManager 找不到 Key 会原样返回
            group.AddToggle(this, LanguageManager.T("Menu.IgnoreSMBTraffic"),
                () => Config?.IgnoreSmbTraffic ?? false, 
                v => { if(Config!=null) Config.IgnoreSmbTraffic = v; }
            );

            // 内存/显存显示模式 (从主界面设置移来)
            string[] memOptions = { LanguageManager.T("Menu.Percent"), LanguageManager.T("Menu.UsedSize") };
            group.AddComboIndex(this, "Menu.MemoryDisplayMode", memOptions,
                () => Config?.MemoryDisplayMode ?? 0,
                idx => { if (Config != null) Config.MemoryDisplayMode = idx; }
            );

            int[] rates = { 100, 200, 300, 500, 600, 700, 800, 1000, 1500, 2000, 3000 };
            group.AddCombo(this, "Menu.Refresh", rates.Select(r => r + " ms"),
                () => (Config?.RefreshMs ?? 1000) + " ms",
                v => { if (Config != null) Config.RefreshMs = MetricUtils.ParseInt(v); }
            );

            // ★★★ 修复：强制转换为 LiteComboBox ★★★
            _cbDisk = (LiteComboBox)group.AddCombo(this, "Menu.DiskSource", new List<string> { strAuto }, 
                () => Config?.PreferredDisk ?? strAuto, 
                v => { if(Config!=null) Config.PreferredDisk = (v == strAuto ? "" : v); });

            _cbNet = (LiteComboBox)group.AddCombo(this, "Menu.NetworkSource", new List<string> { strAuto },
                () => Config?.PreferredNetwork ?? strAuto,
                v => { if (Config != null) Config.PreferredNetwork = (v == strAuto ? "" : v); });

            _cbGpu = (LiteComboBox)group.AddComboPair(this, LanguageManager.T("Menu.GpuSource"),
                new[] { new { Label = strAuto, Value = "" } },
                () => Config?.PreferredGpu ?? "",
                v => { if (Config != null) Config.PreferredGpu = v ?? ""; });

            _cbCpuTemp = (LiteComboBox)group.AddComboPair(this, LanguageManager.T("Menu.CpuTempSource"),
                new[] { new { Label = strAuto, Value = "" } },
                () => Config?.PreferredCpuTemp ?? "",
                v => { if (Config != null) Config.PreferredCpuTemp = v ?? ""; });

            _cbMobo = (LiteComboBox)group.AddCombo(this, "Items.MOBO.Temp", new List<string> { strAuto },
                () => Config?.PreferredMoboTemp ?? strAuto, v => { if (Config != null) Config.PreferredMoboTemp = (v == strAuto ? "" : v); });

            _cbFanCpu = (LiteComboBox)group.AddCombo(this, "Items.CPU.Fan", new List<string> { strAuto },
                () => Config?.PreferredCpuFan ?? strAuto, v => { if (Config != null) Config.PreferredCpuFan = (v == strAuto ? "" : v); });
            
            _cbFanPump = (LiteComboBox)group.AddCombo(this, "Items.CPU.Pump", new List<string> { strAuto },
                () => Config?.PreferredCpuPump ?? strAuto, v => { if (Config != null) Config.PreferredCpuPump = (v == strAuto ? "" : v); });

            _cbFanCase = (LiteComboBox)group.AddCombo(this, "Items.CASE.Fan", new List<string> { strAuto },
                () => Config?.PreferredCaseFan ?? strAuto, v => { if (Config != null) Config.PreferredCaseFan = (v == strAuto ? "" : v); });

            AddGroupToPage(group);
        }

        private void CreateCalibrationCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.Calibration"));
            string suffix = " (" + LanguageManager.T("Menu.MaxLimits") + ")";

            void AddCalib(string key, string unit, Func<float> get, Action<float> set)
            {
                var input = group.AddDouble(this, "RAW_TITLE_HACK", unit, 
                    () => (int)(get?.Invoke() ?? 0),        
                    v => set?.Invoke((float)(int)v)
                );
                if(input.Parent.Controls[0] is Label lbl) lbl.Text = LanguageManager.T(key) + suffix; 
            }
            
            group.AddHint(LanguageManager.T("Menu.CalibrationTip"));
            AddCalib("Items.CPU.Power", "W",   () => Config?.RecordedMaxCpuPower ?? 100, v => { if(Config!=null) Config.RecordedMaxCpuPower = v; });
            AddCalib("Items.CPU.Clock", "MHz", () => Config?.RecordedMaxCpuClock ?? 5000, v => { if(Config!=null) Config.RecordedMaxCpuClock = v; });
            AddCalib("Items.GPU.Power", "W",   () => Config?.RecordedMaxGpuPower ?? 300, v => { if(Config!=null) Config.RecordedMaxGpuPower = v; });
            AddCalib("Items.GPU.Clock", "MHz", () => Config?.RecordedMaxGpuClock ?? 2000, v => { if(Config!=null) Config.RecordedMaxGpuClock = v; });
            AddCalib("Items.CPU.Fan",   "RPM", () => Config?.RecordedMaxCpuFan ?? 2000, v => { if(Config!=null) Config.RecordedMaxCpuFan = v; });
            AddCalib("Items.CPU.Pump",  "RPM", () => Config?.RecordedMaxCpuPump ?? 2000, v => { if(Config!=null) Config.RecordedMaxCpuPump = v; });
            AddCalib("Items.GPU.Fan",   "RPM", () => Config?.RecordedMaxGpuFan ?? 2000, v => { if(Config!=null) Config.RecordedMaxGpuFan = v; });
            AddCalib("Items.CASE.Fan",  "RPM", () => Config?.RecordedMaxChassisFan ?? 2000, v => { if(Config!=null) Config.RecordedMaxChassisFan = v; });

            AddGroupToPage(group);
        }

        private void CreateSystemCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.SystemSettings"));
            
            var langs = new List<string>();
            string langDir = Path.Combine(AppContext.BaseDirectory, "resources/lang");
            if (Directory.Exists(langDir))
                langs.AddRange(Directory.EnumerateFiles(langDir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f).ToUpper()));
            
            group.AddCombo(this, "Menu.Language", langs,
                () => string.IsNullOrEmpty(Config?.Language) ? LanguageManager.CurrentLang.ToUpper() : Config.Language.ToUpper(),
                v => { if(Config!=null) Config.Language = v.ToLower(); }
            );

            group.AddToggle(this, "Menu.AutoStart", () => Config?.AutoStart ?? false, v => { if(Config!=null) Config.AutoStart = v; });
            group.AddToggle(this, "Menu.AutoCheckUpdate", () => Config?.AutoCheckUpdate ?? true, v => { if(Config!=null) Config.AutoCheckUpdate = v; });

            var chkTray = group.AddToggle(this, "Menu.HideTrayIcon", 
                () => Config?.HideTrayIcon ?? false, 
                v => { if(Config!=null) Config.HideTrayIcon = v; });
            chkTray.CheckedChanged += (s, e) => { if(Config!=null) EnsureSafeVisibility(null, chkTray, null); };

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
