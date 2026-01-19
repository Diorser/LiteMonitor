using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor;
using System.Diagnostics;
using LiteMonitor.src.Core;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class PluginPage : SettingsPageBase
    {
        private Panel _container;
        private Dictionary<string, LiteCheck> _toggles = new Dictionary<string, LiteCheck>();
        // Track modified instances for batch restart on Save
        private HashSet<string> _modifiedInstanceIds = new HashSet<string>();

        public PluginPage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);

            _container = new BufferedPanel 
            { 
                Dock = DockStyle.Fill, 
                AutoScroll = true, 
                Padding = new Padding(20) // 增加内间距
            };
            this.Controls.Add(_container);
        }

        public override void Save()
        {
            base.Save();
            
            // Restart only modified plugins
            if (_modifiedInstanceIds.Count > 0)
            {
                // Use Config property to access the latest in-memory state
                var instances = Config?.PluginInstances ?? Settings.Load().PluginInstances;

                foreach (var id in _modifiedInstanceIds)
                {
                    var inst = instances.FirstOrDefault(x => x.Id == id);
                    // Pass the in-memory instance to avoid reading stale config from disk
                    PluginManager.Instance.RestartInstance(id, inst);
                }
                _modifiedInstanceIds.Clear();
            }
        }

        public override void OnShow()
        {
            base.OnShow();
            if (!_isLoaded)
            {
                RebuildUI();
                _isLoaded = true;
            }
        }

        private bool _isLoaded = false;

        private void RebuildUI()
        {
            // ★★★ Fix: Save Scroll Position to prevent jumping to top ★★★
            int savedScroll = _container.VerticalScroll.Value;

            // ★★★ Fix: Save Checkbox States to prevent collapsing on rebuild ★★★
            var savedStates = new Dictionary<string, bool>();
            foreach (var kvp in _toggles)
            {
                if (kvp.Value != null && !kvp.Value.IsDisposed)
                {
                    savedStates[kvp.Key] = kvp.Value.Checked;
                }
            }
            _toggles.Clear();

            _container.SuspendLayout();
            ClearAndDispose(_container.Controls);
            
            var templates = PluginManager.Instance.GetAllTemplates();
            // Use Config instead of Settings.Load() to ensure consistency with SettingsForm context
            var instances = Config?.PluginInstances ?? Settings.Load().PluginInstances;

            // 1. Hint Note with Link
            var linkDoc = new LiteLink(LanguageManager.T("Menu.PluginDevGuide"), () => {
                try { 
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/Diorser/LiteMonitor/blob/master/resources/plugins/PLUGIN_DEV_GUIDE.md") { UseShellExecute = true }); 
                } catch { }
            });
            var hintRow = new LiteActionRow(LanguageManager.T("Menu.PluginHint"), linkDoc);

            hintRow.Dock = DockStyle.Top;
            var hintWrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 20) };
            hintWrapper.Controls.Add(hintRow);
            _container.Controls.Add(hintWrapper);
            
            if (instances == null || instances.Count == 0)
            {
                var lbl = new Label { 
                    Text = LanguageManager.T("Menu.PluginNoInstances"), 
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
                        bool? savedState = savedStates.ContainsKey(inst.Id) ? savedStates[inst.Id] : (bool?)null;
                        CreatePluginGroup(inst, tmpl, isDefault, savedState);
                    }
                }
            }
            
            // ★★★ Fix: Add a transparent spacer to force bottom padding in AutoScroll ★★★
            var spacer = new Panel 
            { 
                Dock = DockStyle.Top, 
                Height = 80, 
                BackColor = Color.Transparent 
            };
            _container.Controls.Add(spacer);
            _container.Controls.SetChildIndex(spacer, 0); // Force to be the last docked item (Bottom)

            _container.ResumeLayout();

            // ★★★ Fix: Restore Scroll Position ★★★
            if (savedScroll > 0)
            {
                _container.PerformLayout(); // Ensure scroll range is updated
                _container.AutoScrollPosition = new Point(0, savedScroll);
            }
        }

        private void CreatePluginGroup(PluginInstanceConfig inst, PluginTemplate tmpl, bool isDefault, bool? savedState = null)
        {
            string title = $"{tmpl.Meta.Name} v{tmpl.Meta.Version} (ID: {inst.Id}) by: {tmpl.Meta.Author}";
            var group = new LiteSettingsGroup(title);

            // 1. Header Actions
            if (isDefault)
            {
                var btnCopy = new LiteHeaderBtn(LanguageManager.T("Menu.PluginCreateCopy"));
                btnCopy.SetColor(UIColors.Primary);
                btnCopy.Click += (s, e) => CopyInstance(inst);
                group.AddHeaderAction(btnCopy);
            }
            else
            {
                var btnDel = new LiteHeaderBtn(LanguageManager.T("Menu.PluginDeleteCopy"));
                btnDel.SetColor(Color.IndianRed);
                btnDel.Click += (s, e) => DeleteInstance(inst);
                group.AddHeaderAction(btnDel);
            }

            if (!string.IsNullOrEmpty(tmpl.Meta.Description))
            {
                 group.AddHint(tmpl.Meta.Description);
            }

            // 2. Enable Switch
            // Defined here to be used in toggle logic
            var targetVisibles = new List<Control>();

            // Handle state memory: Use savedState for first render, but inst.Enabled for subsequent refreshes
            bool usedCache = false;
            Func<bool> getVal = () => 
            {
                if (!usedCache && savedState.HasValue) 
                {
                    usedCache = true;
                    return savedState.Value;
                }
                return inst.Enabled;
            };

            var chk = group.AddToggle(this, tmpl.Meta.Name, 
                getVal, 
                v => {
                    if (inst.Enabled != v) {
                        inst.Enabled = v;
                        _modifiedInstanceIds.Add(inst.Id);
                    }
                }
            );
            _toggles[inst.Id] = chk;

            // Real-time visibility toggle
            chk.CheckedChanged += (s, e) => {
                foreach (var c in targetVisibles) c.Visible = chk.Checked;
            };

            // 3. Refresh Rate
            group.AddInt(this, LanguageManager.T("Menu.Refresh"), "s", 
                () => inst.CustomInterval > 0 ? inst.CustomInterval : tmpl.Execution.Interval,
                v => {
                    if (inst.CustomInterval != v) {
                        inst.CustomInterval = v;
                        _modifiedInstanceIds.Add(inst.Id);
                    }
                }
            );

            // Split Inputs
            var globalInputs = tmpl.Inputs.Where(x => x.Scope != "target").ToList();
            var targetInputs = tmpl.Inputs.Where(x => x.Scope == "target").ToList();

            // 4. Global Inputs
            foreach (var input in globalInputs)
            {
                var inputCtrl = new LiteUnderlineInput(
                    inst.InputValues.ContainsKey(input.Key) ? inst.InputValues[input.Key] : input.DefaultValue, 
                    "", "", 100, null, HorizontalAlignment.Left
                );
                if (!string.IsNullOrEmpty(input.Placeholder)) inputCtrl.Placeholder = input.Placeholder;

                // Direct Binding: Update Model Immediately on Change (Memory Only)
                inputCtrl.Inner.TextChanged += (s, e) => {
                    inst.InputValues[input.Key] = inputCtrl.Inner.Text;
                    _modifiedInstanceIds.Add(inst.Id);
                };
                // Initial refresh for display (if needed, but constructor sets initial value)
                // this.RegisterRefresh(() => inputCtrl.Inner.Text = inst.InputValues.ContainsKey(input.Key) ? inst.InputValues[input.Key] : input.DefaultValue);

                var item = new LiteSettingsItem(input.Label, inputCtrl);
                group.AddItem(item);
                targetVisibles.Add(item);
            }

            // 5. Targets Section
            if (targetInputs.Count > 0)
            {
                if (inst.Targets == null) inst.Targets = new List<Dictionary<string, string>>();
                
                // Ensure at least one target exists for UI display
                if (inst.Targets.Count == 0)
                {
                    var defaultTarget = new Dictionary<string, string>();
                    foreach(var input in targetInputs)
                    {
                        defaultTarget[input.Key] = input.DefaultValue;
                    }
                    inst.Targets.Add(defaultTarget);
                }
                
                for (int i = 0; i < inst.Targets.Count; i++)
                {
                    int index = i; 
                    var targetVals = inst.Targets[i];
                    
                    // Remove Action
                    var linkRem = new LiteLink(LanguageManager.T("Menu.PluginRemoveTarget"), () => {
                        inst.Targets.RemoveAt(index);
                        // SaveAndRestart(inst); // <-- Removed auto-save on delete
                        _modifiedInstanceIds.Add(inst.Id);
                        RebuildUI();
                    });
                    linkRem.SetColor(Color.IndianRed, Color.Red);

                    if (inst.Targets.Count <= 1)
                    {
                        linkRem.Enabled = false;
                    }

                    var headerItem = new LiteSettingsItem(LanguageManager.T("Menu.PluginTargetTitle") + " " + (index + 1), linkRem);
                    headerItem.Label.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
                    headerItem.Label.ForeColor = UIColors.Primary;
                    
                    group.AddFullItem(headerItem);
                    targetVisibles.Add(headerItem);

                    foreach (var input in targetInputs)
                    {
                        var val = targetVals.ContainsKey(input.Key) ? targetVals[input.Key] : input.DefaultValue;
                        
                        if (input.Type == "select" && input.Options != null)
                        {
                            // Manual AddComboPair to capture item
                            var cmb = new LiteComboBox();
                            foreach (var opt in input.Options)
                            {
                                string label = "";
                                string vOpt = "";
                                
                                // Reflection to get Label/Value (assuming dynamic/object)
                                Type t = opt.GetType();
                                var pLabel = t.GetProperty("Label");
                                var pValue = t.GetProperty("Value");
                                
                                if (pLabel != null) label = pLabel.GetValue(opt)?.ToString();
                                if (pValue != null) vOpt = pValue.GetValue(opt)?.ToString();
                                
                                cmb.AddItem(label, vOpt);
                            }

                            cmb.SelectValue(targetVals.ContainsKey(input.Key) ? targetVals[input.Key] : input.DefaultValue);
                            
                            // Direct Binding: Update Model Immediately on Change (Memory Only)
                            cmb.Inner.SelectedIndexChanged += (s, e) => {
                                targetVals[input.Key] = cmb.SelectedValue;
                                _modifiedInstanceIds.Add(inst.Id);
                            };
                            
                            // AttachAutoWidth logic inline
                            cmb.Inner.DropDown += (s, e) => {
                                var box = (ComboBox)s;
                                int maxWidth = box.Width;
                                foreach (var item in box.Items) {
                                    if (item == null) continue;
                                    int w = TextRenderer.MeasureText(item.ToString(), box.Font).Width + SystemInformation.VerticalScrollBarWidth + 10;
                                    if (w > maxWidth) maxWidth = w;
                                }
                                box.DropDownWidth = maxWidth;
                            };

                            var item = new LiteSettingsItem("  " + input.Label, cmb);
                            group.AddItem(item);
                            targetVisibles.Add(item);
                        }
                        else
                        {
                            // Manual AddInput to capture item
                            var inputCtrl = new LiteUnderlineInput(
                                targetVals.ContainsKey(input.Key) ? targetVals[input.Key] : input.DefaultValue, 
                                "", "", 120, null, HorizontalAlignment.Center
                            );
                            if (!string.IsNullOrEmpty(input.Placeholder)) inputCtrl.Placeholder = input.Placeholder;

                            // Direct Binding: Update Model Immediately on Change (Memory Only)
                            inputCtrl.Inner.TextChanged += (s, e) => {
                                targetVals[input.Key] = inputCtrl.Inner.Text;
                                _modifiedInstanceIds.Add(inst.Id);
                            };

                            var item = new LiteSettingsItem("  " + input.Label, inputCtrl);
                            group.AddItem(item);
                            targetVisibles.Add(item);
                        }
                    }
                }

                // Add Target Button
                var btnAdd = new LiteButton(LanguageManager.T("Menu.PluginAddTarget"), false, true); 
                btnAdd.Click += (s, e) => {
                    var newTarget = new Dictionary<string, string>();
                    if (targetInputs != null)
                    {
                        foreach(var input in targetInputs)
                        {
                            // [Requirement 1] Empty for new text targets, but keep DefaultValue for selects
                            if (input.Type == "select")
                            {
                                newTarget[input.Key] = input.DefaultValue;
                            }
                            else
                            {
                                newTarget[input.Key] = ""; 
                            }
                        }
                    }
                    inst.Targets.Add(newTarget);
                    _modifiedInstanceIds.Add(inst.Id);
                    RebuildUI();
                };
                
                group.AddFullItem(btnAdd);
                btnAdd.Margin = UIUtils.S(new Padding(0, 15, 0, 0));
                targetVisibles.Add(btnAdd);
            }

            // Initial visibility state
            foreach (var c in targetVisibles) c.Visible = chk.Checked;

            AddGroupToPage(group);
        }

        private void AddGroupToPage(LiteSettingsGroup group)
        {
            var wrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 20) };
            wrapper.Controls.Add(group);
            
            // ★★★ Fix for "Cannot set Win32 parent" Crash ★★★
            // The crash occurs because we are adding controls to _container while its parent (SettingsForm._pnlContent) 
            // has layout suspended (WM_SETREDRAW=false). When adding deep hierarchies (PluginPage -> Panel -> LiteSettingsGroup -> Panel -> Controls),
            // WinForms sometimes fails to resolve the HWND chain correctly if the root is not painting.
            
            // By adding to _container directly, we ensure the control tree is built. 
            // The parent handle should be valid because SettingsForm adds PluginPage BEFORE calling OnShow.
            
            _container.Controls.Add(wrapper);
            _container.Controls.SetChildIndex(wrapper, 0);
        }

        private void SaveAndRestart(PluginInstanceConfig inst)
        {
            if (Config != null) Config.Save();
            else Settings.Load().Save();

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
            
            if (source.Targets != null)
            {
                 foreach(var t in source.Targets)
                 {
                     newInst.Targets.Add(new Dictionary<string, string>(t));
                 }
            }

            if (Config != null)
            {
                Config.PluginInstances.Add(newInst);
                Config.Save();
            }
            else
            {
                Settings.Load().PluginInstances.Add(newInst);
                Settings.Load().Save();
            }
            
            PluginManager.Instance.RestartInstance(newInst.Id);
            
            RebuildUI();
        }

        private void DeleteInstance(PluginInstanceConfig inst)
        {
            if (MessageBox.Show(LanguageManager.T("Menu.PluginDeleteConfirm"), LanguageManager.T("Menu.OK"), MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                if (Config != null)
                {
                    Config.PluginInstances.Remove(inst);
                    Config.Save();
                }
                else
                {
                    Settings.Load().PluginInstances.Remove(inst);
                    Settings.Load().Save();
                }

                PluginManager.Instance.RemoveInstance(inst.Id);
                RebuildUI();
            }
        }
    }
}
