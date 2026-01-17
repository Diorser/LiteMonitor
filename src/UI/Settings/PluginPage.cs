using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor;
using LiteMonitor.src.Core;
using LiteMonitor.src.Core.Plugins;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class PluginPage : SettingsPageBase
    {
        private Panel _container;

        public PluginPage()
        {
            // Strictly follow MainPanelPage layout
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);

            // MainPanelPage uses 20 padding.
            _container = new BufferedPanel 
            { 
                Dock = DockStyle.Fill, 
                AutoScroll = true, 
                Padding = new Padding(20) 
            };
            this.Controls.Add(_container);
        }

        public override void OnShow()
        {
            base.OnShow(); // Execute any base load actions
            RebuildUI();
        }

        private void RebuildUI()
        {
            _container.SuspendLayout();
            _container.Controls.Clear();

            var templates = PluginManager.Instance.GetAllTemplates();
            var instances = Settings.Load().PluginInstances;

            // 1. Hint Note (Standard LiteNote)
            var hint = new LiteNote("如需修改显示名称、单位或排序，请前往 [监控项管理] 页面");
            hint.Dock = DockStyle.Top;
            // Matches MainPanelPage wrapper padding style roughly
            var hintWrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 20) };
            hintWrapper.Controls.Add(hint);
            _container.Controls.Add(hintWrapper);
            

            if (instances == null || instances.Count == 0)
            {
                var lbl = new Label { 
                    Text = "暂无插件实例", 
                    AutoSize = true, 
                    ForeColor = UIColors.TextSub, 
                    Location = new Point(UIUtils.S(20), UIUtils.S(60)) 
                };
                _container.Controls.Add(lbl);
            }
            else
            {
                var grouped = instances.GroupBy(i => i.TemplateId);

                foreach (var grp in grouped)
                {
                    var tmpl = templates.FirstOrDefault(t => t.Id == grp.Key);
                    if (tmpl == null) continue;

                    var list = grp.ToList();
                    for (int i = 0; i < list.Count; i++)
                    {
                        var inst = list[i];
                        bool isDefault = (i == 0); 
                        CreatePluginGroup(inst, tmpl, isDefault);
                    }
                }
            }
            
            _container.ResumeLayout();
        }

        private void CreatePluginGroup(PluginInstanceConfig inst, PluginTemplate tmpl, bool isDefault)
        {
            // Title: Name + Version + Author + ID
            string title = $"{tmpl.Meta.Name} v{tmpl.Meta.Version} (ID: {inst.Id}) by:{tmpl.Meta.Author}";
            var group = new LiteSettingsGroup(title);

            // 1. Description & Actions (Header Panel)
            var headerPanel = new Panel {
                Height = UIUtils.S(30),
                Padding = new Padding(0)
            };

            // Button (Right)
            LiteButton btnAction = null;
            if (isDefault)
            {
                // Copy Button: Primary Style (Blue)
                btnAction = new LiteButton(LanguageManager.T("Menu.Copy") == "Menu.Copy" ? "复制" : LanguageManager.T("Menu.Copy"), true);
                btnAction.Click += (s, e) => CopyInstance(inst);
            }
            else
            {
                // Delete Button: Danger Style (Red)
                btnAction = new LiteButton(LanguageManager.T("Menu.Delete") == "Menu.Delete" ? "删除" : LanguageManager.T("Menu.Delete"), false);
                btnAction.BackColor = Color.IndianRed;
                btnAction.ForeColor = Color.White;
                btnAction.FlatAppearance.BorderColor = Color.IndianRed;
                btnAction.Click += (s, e) => DeleteInstance(inst);
            }
            btnAction.Size = new Size(UIUtils.S(60), UIUtils.S(24));
            btnAction.Dock = DockStyle.Right;

            // Description (Fill)
            var note = new LiteNote(string.IsNullOrEmpty(tmpl.Meta.Description) ? " " : tmpl.Meta.Description);
            note.Dock = DockStyle.Fill;

            headerPanel.Controls.Add(btnAction); // Add Right first
            headerPanel.Controls.Add(note);      // Add Fill second
            btnAction.BringToFront();            // Ensure button is on top/clickable

            group.AddFullItem(headerPanel);

            // 2. Enable Switch (Label = Plugin Name)
            LocalAddBool(group, tmpl.Meta.Name, 
                inst.Enabled, 
                v => {
                    inst.Enabled = v;
                    SaveAndRestart(inst);
                }
            );

            // 3. Refresh Rate (Replaces ID Input)
            int currentInterval = inst.CustomInterval > 0 ? inst.CustomInterval : tmpl.Execution.Interval;
            LocalAddNumber(group, "刷新频率", currentInterval, v => {
                inst.CustomInterval = v;
            }, () => SaveAndRestart(inst), "ms");

            // Split Inputs
            var globalInputs = tmpl.Inputs.Where(x => x.Scope != "target").ToList();
            var targetInputs = tmpl.Inputs.Where(x => x.Scope == "target").ToList();

            // 4. Global Inputs
            foreach (var input in globalInputs)
            {
                var currentVal = inst.InputValues.ContainsKey(input.Key) ? inst.InputValues[input.Key] : input.DefaultValue;
                LocalAddString(group, input.Label, currentVal, v => {
                    inst.InputValues[input.Key] = v;
                }, () => SaveAndRestart(inst));
            }

            // 5. Targets Section (Only if plugin has target inputs)
            if (targetInputs.Count > 0)
            {
                 // Auto-Migrate: If Targets is empty but we have values in InputValues for target inputs
                if ((inst.Targets == null || inst.Targets.Count == 0))
                {
                    var legacyTarget = new Dictionary<string, string>();
                    bool hasLegacy = false;
                    foreach (var tInput in targetInputs)
                    {
                        if (inst.InputValues.ContainsKey(tInput.Key))
                        {
                            legacyTarget[tInput.Key] = inst.InputValues[tInput.Key];
                            inst.InputValues.Remove(tInput.Key); // Remove from global
                            hasLegacy = true;
                        }
                    }
                    
                    if (hasLegacy)
                    {
                        inst.Targets = new List<Dictionary<string, string>> { legacyTarget };
                        Settings.Load().Save(); // Save migration
                    }
                }

                // Header for Targets
                var targetsHeader = new LiteNote("--- 监控目标列表 ---");
                targetsHeader.Padding = new Padding(0, 10, 0, 5);
                group.AddFullItem(targetsHeader);

                if (inst.Targets == null) inst.Targets = new List<Dictionary<string, string>>();
                
                for (int i = 0; i < inst.Targets.Count; i++)
                {
                    int index = i; 
                    var targetVals = inst.Targets[i];
                    
                    // Target Header
                    var headerPanel2 = new Panel { Height = UIUtils.S(30), Dock = DockStyle.Top };
                    var lbl = new Label { Text = $"# 目标 {index + 1}", AutoSize = true, ForeColor = UIColors.Primary, Location = new Point(0, 8) };
                    var btnRem = new LiteButton("移除", false) { Width = UIUtils.S(60), Height=UIUtils.S(24), Dock = DockStyle.Right };
                    btnRem.Click += (s, e) => {
                        inst.Targets.RemoveAt(index);
                        SaveAndRestart(inst);
                        RebuildUI();
                    };
                    headerPanel2.Controls.Add(btnRem);
                    headerPanel2.Controls.Add(lbl);
                    group.AddFullItem(headerPanel2);

                    foreach (var input in targetInputs)
                    {
                        var val = targetVals.ContainsKey(input.Key) ? targetVals[input.Key] : input.DefaultValue;
                        LocalAddString(group, "  " + input.Label, val, v => {
                            targetVals[input.Key] = v;
                        }, () => SaveAndRestart(inst));
                    }
                }

                // Add Target Button
                var btnAdd = new LiteButton("添加目标", true);
                btnAdd.Size = new Size(UIUtils.S(100), UIUtils.S(30));
                btnAdd.Click += (s, e) => {
                    // Pre-fill with default values
                    var newTarget = new Dictionary<string, string>();
                    if (targetInputs != null)
                    {
                        foreach(var input in targetInputs)
                        {
                            newTarget[input.Key] = input.DefaultValue;
                        }
                    }
                    inst.Targets.Add(newTarget);
                    SaveAndRestart(inst);
                    RebuildUI();
                };
                
                var btnPanel = new Panel { Height = UIUtils.S(40), Padding = new Padding(0, 5, 0, 5) };
                btnPanel.Controls.Add(btnAdd);
                btnAdd.Dock = DockStyle.Fill;
                
                group.AddFullItem(btnPanel);
            }

            AddGroupToPage(group);
        }

        private void AddGroupToPage(LiteSettingsGroup group)
        {
            // Copied from MainPanelPage.cs
            var wrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 20) };
            wrapper.Controls.Add(group);
            _container.Controls.Add(wrapper);
            _container.Controls.SetChildIndex(wrapper, 0);
        }

        // =================================================================================
        // Local Helpers (Mimic SettingsPageBase but without persistent _loadActions)
        // =================================================================================

        private void LocalAddBool(LiteSettingsGroup group, string title, bool initialVal, Action<bool> onChange)
        {
            var chk = new LiteCheck(false, LanguageManager.T("Menu.Enable"));
            chk.Checked = initialVal;
            chk.CheckedChanged += (s, e) => onChange(chk.Checked);
            // Use title directly instead of T(titleKey) because title is dynamic (Plugin Name)
            group.AddItem(new LiteSettingsItem(title, chk));
        }

        private void LocalAddString(LiteSettingsGroup group, string label, string initialVal, Action<string> onChange, Action onLeave)
        {
            // Narrower input: 120 width
            var txt = new LiteUnderlineInput(initialVal, "", "", 120);
            // Match SettingsPageBase.AddNumberInt padding style
            txt.Padding = UIUtils.S(new Padding(0, 5, 0, 1));
            
            txt.Inner.TextChanged += (s, e) => onChange(txt.Inner.Text);
            txt.Inner.Leave += (s, e) => onLeave();
            
            group.AddItem(new LiteSettingsItem(label, txt));
        }

        private void LocalAddNumber(LiteSettingsGroup group, string label, int initialVal, Action<int> onChange, Action onLeave, string unit = "")
        {
            // Narrower input: 120 width
            var txt = new LiteUnderlineInput(initialVal.ToString(), unit, "", 120);
            // Match SettingsPageBase.AddNumberInt padding style
            txt.Padding = UIUtils.S(new Padding(0, 5, 0, 1));
            
            txt.Inner.TextChanged += (s, e) => {
                if (int.TryParse(txt.Inner.Text, out int val))
                {
                    onChange(val);
                }
            };
            txt.Inner.Leave += (s, e) => onLeave();
            
            group.AddItem(new LiteSettingsItem(label, txt));
        }

        private void LocalAddReadOnlyString(LiteSettingsGroup group, string label, string val)
        {
            // Narrower input: 120 width
            var txt = new LiteUnderlineInput(val, "", "", 120);
            txt.Padding = UIUtils.S(new Padding(0, 5, 0, 1));
            txt.Inner.ReadOnly = true;
            txt.Inner.ForeColor = UIColors.TextSub; 
            txt.Inner.BackColor = Color.FromArgb(245, 245, 245); // Visibly read-only
            group.AddItem(new LiteSettingsItem(label, txt));
        }

        private void SaveAndRestart(PluginInstanceConfig inst)
        {
            Settings.Load().Save();
            PluginManager.Instance.RestartInstance(inst.Id);
        }

        private void CopyInstance(PluginInstanceConfig source)
        {
            var newInst = new PluginInstanceConfig
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                TemplateId = source.TemplateId,
                Enabled = source.Enabled,
                InputValues = new Dictionary<string, string>(source.InputValues),
                CustomInterval = source.CustomInterval
            };
            
            // Copy Targets
            if (source.Targets != null)
            {
                 foreach(var t in source.Targets)
                 {
                     newInst.Targets.Add(new Dictionary<string, string>(t));
                 }
            }

            Settings.Load().PluginInstances.Add(newInst);
            Settings.Load().Save();
            
            // SyncMonitorItem needs to be called to create the DASH item in Settings.MonitorItems
            PluginManager.Instance.SyncMonitorItem(newInst);
            Settings.Load().Save(); // Ensure the new MonitorItem is saved to disk
            
            // RebuildUI to show the new group
            RebuildUI();
        }

        private void DeleteInstance(PluginInstanceConfig inst)
        {
            if (MessageBox.Show("确定要删除此插件副本吗？", "确认", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                Settings.Load().PluginInstances.Remove(inst);
                Settings.Load().Save();
                PluginManager.Instance.RemoveInstance(inst.Id);
                RebuildUI();
            }
        }
    }
}
