using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq; 
using System.Windows.Forms;
using System.Threading.Tasks;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class TaskbarPage : SettingsPageBase
    {
        private Panel _container;
        private List<Control> _customColorInputs = new List<Control>();
        private List<Control> _customLayoutInputs = new List<Control>();
        private Control _styleCombo;
        private CheckBox _chkCustomLayout;
        
        // 类型名称修正为 LiteComboBox
        private LiteComboBox _cbFont;
        private Task<List<string>> _taskFonts;

        public TaskbarPage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);
            _container = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20) }; 
            this.Controls.Add(_container);

            // 1. 启动后台任务 (字体扫描)
            _taskFonts = Task.Run(() => {
                try {
                    return FontFamily.Families.Select(f => f.Name).ToList();
                } catch {
                    return new List<string> { Settings.DEFAULT_TB_FONT };
                }
            });

            // 2. 立即构建 UI (此时 Config 为 null，必须安全访问)
            InitializeUI();
        }

        private void InitializeUI()
        {
            CreateGeneralGroup(); 
            CreateLayoutGroup();
            CreateColorGroup();   
        }

        public override void OnShow()
        {
            base.OnShow();
            if (Config == null) return;
            
            // 3. 异步填充字体
            PopulateFonts();
        }

        private async void PopulateFonts()
        {
            if (_cbFont == null || _cbFont.Inner.Items.Count > 5) return; 

            var fonts = await _taskFonts;
            
            // ★★★ 锁定布局 ★★★
            this.SuspendLayout();
            try
            {
                string current = Config.TaskbarFontFamily ?? Settings.DEFAULT_TB_FONT;
                if (!fonts.Contains(current)) fonts.Insert(0, current);

                _cbFont.Inner.BeginUpdate();
                _cbFont.Inner.Items.Clear();
                // 优化：使用 AddRange (如果你的 Inner 是标准 ComboBox)
                _cbFont.Inner.Items.AddRange(fonts.ToArray());
                
                if (fonts.Contains(current)) _cbFont.Inner.SelectedItem = current;
                else if (_cbFont.Inner.Items.Count > 0) _cbFont.Inner.SelectedIndex = 0;
                _cbFont.Inner.EndUpdate();
            }
            finally
            {
                this.ResumeLayout(true);
                // 强制刷新一次布局，解决异步加载字体导致底部留白未被正确计算的问题
                _container.PerformLayout();
            }
        }

        public override void Save()
        {
            base.Save();
            if(Config != null) TaskbarRenderer.ReloadStyle(Config);
        }

        private void CreateGeneralGroup()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.TaskbarSettings"));

            // ★★★ 修复：增加 Config? 判空和默认值 ★★★
            var chkShow = group.AddToggle(this, "Menu.TaskbarShow", 
                () => Config?.ShowTaskbar ?? true, 
                v => { if(Config!=null) Config.ShowTaskbar = v; });
            chkShow.CheckedChanged += (s, e) => { if(Config!=null) EnsureSafeVisibility(null, null, chkShow); };

            // Style Combo
            var combo = group.AddComboIndex(this, "Menu.TaskbarStyle",
                new[] { LanguageManager.T("Menu.TaskbarStyleBold"), LanguageManager.T("Menu.TaskbarStyleRegular") },
                // UI: 0=Bold, 1=Regular | Config: 1=Bold, 0=Regular
                () => (Config?.TaskbarPresetStyle ?? 1) == 1 ? 0 : 1,
                idx => { if (Config != null) Config.TaskbarPresetStyle = (idx == 0) ? 1 : 0; }
            );
            _styleCombo = combo; 
            _styleCombo.Enabled = !(Config?.TaskbarCustomLayout ?? false);

            group.AddToggle(this, "Menu.TaskbarSingleLine", () => Config?.TaskbarSingleLine ?? false, v => { if(Config!=null) Config.TaskbarSingleLine = v; });
            group.AddToggle(this, "Menu.TaskbarHoverShowAll", () => Config?.TaskbarHoverShowAll ?? false, v => { if (Config != null) Config.TaskbarHoverShowAll = v; });
            group.AddToggle(this, "Menu.ClickThrough", () => Config?.TaskbarClickThrough ?? false, v => { if(Config!=null) Config.TaskbarClickThrough = v; });
           
            // Monitor Selection
            var screens = Screen.AllScreens;
            var screenNames = screens.Select((s, i) => $"{i + 1}: {s.DeviceName.Replace(@"\\.\DISPLAY", "Display ")}{(s.Primary ? " [Main]" : "")}").ToList();
            screenNames.Insert(0, LanguageManager.T("Menu.Auto"));
            
            group.AddComboIndex(this, "Menu.TaskbarMonitor", screenNames.ToArray(), 
                () => {
                    if (string.IsNullOrEmpty(Config?.TaskbarMonitorDevice)) return 0;
                    var idx = Array.FindIndex(screens, s => s.DeviceName == Config.TaskbarMonitorDevice);
                    return idx >= 0 ? idx + 1 : 0;
                },
                idx => {
                    if (Config == null) return;
                    if (idx == 0) Config.TaskbarMonitorDevice = ""; 
                    else Config.TaskbarMonitorDevice = screens[idx - 1].DeviceName;
                }
            );

            // Double Click Action
            string[] actions = { 
                LanguageManager.T("Menu.ActionToggleVisible"),
                LanguageManager.T("Menu.ActionTaskMgr"), 
                LanguageManager.T("Menu.ActionSettings"),
                LanguageManager.T("Menu.ActionTrafficHistory"),
                LanguageManager.T("Menu.CleanMemory"),
                LanguageManager.T("Menu.OpenWeb")
            };
            group.AddComboIndex(this, "Menu.DoubleClickAction", actions,
                () => Config?.TaskbarDoubleClickAction ?? 0,
                idx => { if(Config!=null) Config.TaskbarDoubleClickAction = idx; }
            );

            group.AddComboIndex(this, "Menu.TaskbarAlign",
                new[] { LanguageManager.T("Menu.TaskbarAlignRight"), LanguageManager.T("Menu.TaskbarAlignLeft") },
                () => (Config?.TaskbarAlignLeft ?? false) ? 1 : 0,
                idx => { if(Config!=null) Config.TaskbarAlignLeft = (idx == 1); }
            );

            group.AddInt(this, "Menu.TaskbarOffset", "px", 
                () => Config?.TaskbarManualOffset ?? 0, 
                v => { if(Config!=null) Config.TaskbarManualOffset = v; });

            // [新增] Win11 高度修正
            if (Environment.OSVersion.Version.Major == 10 && Environment.OSVersion.Version.Build >= 22000)
            {
                group.AddInt(this, "Win11 任务栏高度", "px", 
                    () => Config?.Win11TaskbarHeight ?? 48, 
                    v => { if(Config!=null) Config.Win11TaskbarHeight = v; });
            }

            group.AddHint(LanguageManager.T("Menu.TaskbarAlignTip"));
            AddGroupToPage(group);
        }

        private void CreateLayoutGroup()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.TaskbarCustomLayout")); 
            _customLayoutInputs.Clear();

            var chk = group.AddToggle(this, "Menu.TaskbarCustomLayout", 
                () => Config?.TaskbarCustomLayout ?? false, 
                v => { if(Config!=null) Config.TaskbarCustomLayout = v; });
            _chkCustomLayout = chk;
            chk.CheckedChanged += (s, e) => {
                foreach(var c in _customLayoutInputs) c.Enabled = chk.Checked;
                if (_styleCombo != null) 
                {
                    _styleCombo.Enabled = !chk.Checked;
                }
            };

            void AddL(Control ctrl) {
                _customLayoutInputs.Add(ctrl);
                ctrl.Enabled = Config?.TaskbarCustomLayout ?? false;
            }

            // ★★★ 字体 ComboBox (LiteComboBox) ★★★
            var initialFonts = new List<string> { Settings.DEFAULT_TB_FONT };
            _cbFont = (LiteComboBox)group.AddCombo(this, "Menu.TaskbarFont", initialFonts, 
                () => Config?.TaskbarFontFamily ?? Settings.DEFAULT_TB_FONT, 
                v => { if(Config!=null && Config.TaskbarCustomLayout) Config.TaskbarFontFamily = v; }
            );
            AddL(_cbFont);

            group.AddHint(LanguageManager.T("Menu.TaskbarCustomLayoutTip"));

            AddL(group.AddDouble(this, "Menu.TaskbarFontSize", "pt", 
                () => Config?.TaskbarFontSize ?? Settings.DEFAULT_TB_SIZE_REGULAR, 
                v => { if(Config!=null && Config.TaskbarCustomLayout) Config.TaskbarFontSize = (float)v; }));
            
            AddL(group.AddToggle(this, "Menu.TaskbarFontBold", 
                () => Config?.TaskbarFontBold ?? false, 
                v => { if (Config != null && Config.TaskbarCustomLayout) Config.TaskbarFontBold = v; }));


            AddL(group.AddInt(this, "Menu.TaskbarItemSpacing", "px", 
                () => Config?.TaskbarItemSpacing ?? Settings.DEFAULT_TB_GAP, 
                v => { if(Config!=null && Config.TaskbarCustomLayout) Config.TaskbarItemSpacing = v; }));
            
            AddL(group.AddInt(this, "Menu.TaskbarInnerSpacing", "px", 
                () => Config?.TaskbarInnerSpacing ?? Settings.DEFAULT_TB_INNER_REGULAR, 
                v => { if(Config!=null && Config.TaskbarCustomLayout) Config.TaskbarInnerSpacing = v; }));
            
            AddL(group.AddInt(this, "Menu.TaskbarVerticalPadding", "px", 
                () => Config?.TaskbarVerticalPadding ?? Settings.DEFAULT_TB_VOFF, 
                v => { if(Config!=null && Config.TaskbarCustomLayout) Config.TaskbarVerticalPadding = v; }));

            AddGroupToPage(group);
        }

        private void CreateColorGroup()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.TaskbarCustomColors"));
            _customColorInputs.Clear();

            var chkColor = group.AddToggle(this, "Menu.TaskbarCustomColors", 
                () => Config?.TaskbarCustomStyle ?? false, 
                v => { if(Config!=null) Config.TaskbarCustomStyle = v; });
            
            chkColor.CheckedChanged += (s, e) => {
                foreach(var c in _customColorInputs) c.Enabled = chkColor.Checked;
            };

            // Color Picker Tool
            var tbResult = new LiteUnderlineInput("#000000", "", "", 65, null, HorizontalAlignment.Center);
            tbResult.Padding = UIUtils.S(new Padding(0, 5, 0, 1)); 
            tbResult.Inner.ReadOnly = true; 
            var btnPick = new LiteSortBtn("🖌"); 
            btnPick.Location = new Point(UIUtils.S(70), UIUtils.S(1));
            btnPick.Click += (s, e) => {
                using (Form f = new Form { FormBorderStyle = FormBorderStyle.None, WindowState = FormWindowState.Maximized, TopMost = true, Cursor = Cursors.Cross })
                {
                    Bitmap bmp = new Bitmap(Screen.PrimaryScreen.Bounds.Width, Screen.PrimaryScreen.Bounds.Height);
                    using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(0, 0, 0, 0, bmp.Size);
                    f.BackgroundImage = bmp;
                    f.MouseClick += (ms, me) => {
                        Color c = bmp.GetPixel(me.X, me.Y);
                        string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                        tbResult.Inner.Text = hex;
                        f.Close();
                        if (MessageBox.Show(string.Format("{0} {1}?", LanguageManager.T("Menu.ScreenColorPickerTip"), hex), "LiteMonitor", MessageBoxButtons.YesNo) == DialogResult.Yes)
                        {
                            if (Config != null) Config.TaskbarColorBg = hex;
                            foreach (var control in _customColorInputs)
                                if (control is LiteColorInput ci && ci.Input.Inner.Tag?.ToString() == "Menu.BackgroundColor") { ci.HexValue = hex; break; }
                        }
                    };
                    f.ShowDialog();
                }
            };
            Panel toolCtrl = new Panel { Size = new Size(UIUtils.S(96), UIUtils.S(26)) };
            toolCtrl.Controls.Add(tbResult); toolCtrl.Controls.Add(btnPick);
            group.AddItem(new LiteSettingsItem(LanguageManager.T("Menu.ScreenColorPicker"), toolCtrl));
            group.AddHint(LanguageManager.T("Menu.TaskbarCustomTip"));

            void AddC(string key, Func<string> get, Action<string> set)
            {
                var c = group.AddColor(this, key, get, set);
                c.Input.Inner.Tag = key; 
                c.Enabled = Config?.TaskbarCustomStyle ?? false;
                _customColorInputs.Add(c);
            }

            AddC("Menu.BackgroundColor", () => Config?.TaskbarColorBg ?? "#000000", v => { if(Config!=null) Config.TaskbarColorBg = v; });
            AddC("Menu.LabelColor", () => Config?.TaskbarColorLabel ?? "#FFFFFF", v => { if(Config!=null) Config.TaskbarColorLabel = v; });
            AddC("Menu.ValueSafeColor", () => Config?.TaskbarColorSafe ?? "#00FF00", v => { if(Config!=null) Config.TaskbarColorSafe = v; });
            AddC("Menu.ValueWarnColor", () => Config?.TaskbarColorWarn ?? "#FFFF00", v => { if(Config!=null) Config.TaskbarColorWarn = v; });
            AddC("Menu.ValueCritColor", () => Config?.TaskbarColorCrit ?? "#FF0000", v => { if(Config!=null) Config.TaskbarColorCrit = v; });

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
