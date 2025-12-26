using System.Reflection; // 引用反射命名空间
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public interface ISettingsPage
    {
        void Save();
        void OnShow();
    }

    public class SettingsPageBase : UserControl, ISettingsPage
    {
        protected Settings Config;
        protected MainForm MainForm;
        protected UIController UI;
        
        // ★ 新增：加载队列（用于每次 OnShow 时刷新 UI）
        private List<Action> _loadActions = new List<Action>();
        // 保存队列
        private List<Action> _saveActions = new List<Action>();

        public static readonly Color GlobalBackColor = Color.FromArgb(249, 249, 249); 

        public SettingsPageBase() 
        {
            this.BackColor = GlobalBackColor; 
            this.Dock = DockStyle.Fill;
            this.DoubleBuffered = true;
        }

        public void SetContext(Settings cfg, MainForm form, UIController ui)
        {
            Config = cfg;
            MainForm = form;
            UI = ui;
        }

        // =============================================================
        //  UI 工厂方法 (Factory Methods)
        // =============================================================

        /// <summary>
        /// 快速添加：开关 (CheckBox)
        /// </summary>
        protected LiteCheck AddBool(LiteSettingsGroup group, string titleKey, Func<bool> get, Action<bool> set, Action<LiteCheck> onCreated = null)
        {
            var chk = new LiteCheck(false, LanguageManager.T("Menu.Enable"));
            BindCheck(chk, get, set);
            onCreated?.Invoke(chk);
            group.AddItem(new LiteSettingsItem(LanguageManager.T(titleKey), chk));
            return chk;
        }

        /// <summary>
        /// 快速添加：下拉框 (ComboBox) - 字符串列表
        /// </summary>
        protected LiteComboBox AddCombo(LiteSettingsGroup group, string titleKey, IEnumerable<string> items, Func<string> get, Action<string> set)
        {
            var cmb = new LiteComboBox();
            foreach (var i in items) cmb.Items.Add(i);
            BindCombo(cmb, get, set);
            group.AddItem(new LiteSettingsItem(LanguageManager.T(titleKey), cmb));
            return cmb;
        }

        /// <summary>
        /// 快速添加：下拉框 (ComboBox) - 索引绑定
        /// </summary>
        protected LiteComboBox AddComboIndex(LiteSettingsGroup group, string titleKey, IEnumerable<string> items, Func<int> get, Action<int> set)
        {
            var cmb = new LiteComboBox();
            foreach (var i in items) cmb.Items.Add(i);
            BindComboIndex(cmb, get, set);
            group.AddItem(new LiteSettingsItem(LanguageManager.T(titleKey), cmb));
            return cmb;
        }

        /// <summary>
        /// 快速添加：数字输入 (Int)
        /// </summary>
        protected LiteNumberInput AddNumberInt(LiteSettingsGroup group, string titleKey, string unit, Func<int> get, Action<int> set, int width = 60, Color? color = null)
        {
            var input = new LiteNumberInput("0", unit, "", width, color);
            // ★★★ 修复：手动调整 Padding 让文字垂直居中 (Top 增加) ★★★
            input.Padding = UIUtils.S(new Padding(0, 5, 0, 1)); 
            
            BindInt(input, get, set);
            group.AddItem(new LiteSettingsItem(LanguageManager.T(titleKey), input));
            return input;
        }

        /// <summary>
        /// 快速添加：数字输入 (Double/Float)
        /// </summary>
        protected LiteNumberInput AddNumberDouble(LiteSettingsGroup group, string titleKey, string unit, Func<double> get, Action<double> set, int width = 70)
        {
            var input = new LiteNumberInput("0", unit, "", width);
            // ★★★ 修复：手动调整 Padding ★★★
            input.Padding = UIUtils.S(new Padding(0, 5, 0, 1));
            
            BindDouble(input, get, set);
            group.AddItem(new LiteSettingsItem(LanguageManager.T(titleKey), input));
            return input;
        }

        /// <summary>
        /// 快速添加：颜色选择器
        /// </summary>
        protected LiteColorInput AddColor(LiteSettingsGroup group, string titleKey, Func<string> get, Action<string> set, bool enabled = true)
        {
            var input = new LiteColorInput(get());
            // ★★★ 修复：调整颜色输入框内部的 Padding ★★★
            input.Input.Padding = UIUtils.S(new Padding(0, 5, 0, 1));
            
            input.Enabled = enabled;
            BindColor(input, get, set);
            group.AddItem(new LiteSettingsItem(LanguageManager.T(titleKey), input));
            return input;
        }


        // =============================================================
        //  强力绑定 2.0：同时注册“加载”和“保存”逻辑
        // =============================================================

        protected void BindCheck(LiteCheck chk, Func<bool> getter, Action<bool> setter)
        {
            // 1. 立即赋值（用于首次创建）
            chk.Checked = getter();
            // 2. 注册重载逻辑（用于切换页面回来时刷新）
            _loadActions.Add(() => chk.Checked = getter());
            // 3. 注册保存逻辑
            _saveActions.Add(() => setter(chk.Checked));
        }

        protected void BindCombo(LiteComboBox cmb, Func<string> getter, Action<string> setter)
        {
            // 定义通用刷新逻辑
            void Refresh() {
                string val = getter();
                if (!cmb.Items.Contains(val)) cmb.Items.Insert(0, val);
                cmb.SelectedItem = val;
            }

            Refresh(); // 立即执行
            _loadActions.Add(Refresh); // 注册重载
            
            _saveActions.Add(() => {
                if (cmb.SelectedItem != null) setter(cmb.SelectedItem.ToString());
            });
        }
        
        protected void BindComboIndex(LiteComboBox cmb, Func<int> getter, Action<int> setter)
        {
            void Refresh() {
                int idx = getter();
                if (idx >= 0 && idx < cmb.Items.Count) cmb.SelectedIndex = idx;
            }
            Refresh();
            _loadActions.Add(Refresh);
            _saveActions.Add(() => setter(cmb.SelectedIndex));
        }

        protected void BindInt(LiteUnderlineInput input, Func<int> getter, Action<int> setter)
        {
            void Refresh() => input.Inner.Text = getter().ToString();
            Refresh();
            _loadActions.Add(Refresh);
            _saveActions.Add(() => setter(UIUtils.ParseInt(input.Inner.Text)));
        }

        protected void BindDouble(LiteUnderlineInput input, Func<double> getter, Action<double> setter)
        {
            void Refresh() => input.Inner.Text = getter().ToString(); // 可加格式化
            Refresh();
            _loadActions.Add(Refresh);
            _saveActions.Add(() => setter(UIUtils.ParseDouble(input.Inner.Text)));
        }
        
        protected void BindColor(LiteColorInput input, Func<string> getter, Action<string> setter)
        {
            void Refresh() => input.HexValue = getter();
            Refresh();
            _loadActions.Add(Refresh);
            _saveActions.Add(() => setter(input.HexValue));
        }

        protected void EnsureSafeVisibility(LiteCheck chkHideMain, LiteCheck chkHideTray, LiteCheck chkShowTaskbar)
        {
            bool hideMain = chkHideMain != null ? chkHideMain.Checked : Config.HideMainForm;
            bool hideTray = chkHideTray != null ? chkHideTray.Checked : Config.HideTrayIcon;
            bool showBar  = chkShowTaskbar != null ? chkShowTaskbar.Checked : Config.ShowTaskbar;

            if (hideMain && hideTray && !showBar)
            {
                MessageBox.Show("为了防止程序无法唤出，不能同时隐藏 [主界面]、[托盘图标] 和 [任务栏]。", 
                                "安全警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                
                if (chkHideMain != null) chkHideMain.Checked = false;
                if (chkHideTray != null) chkHideTray.Checked = false;
                if (chkShowTaskbar != null) chkShowTaskbar.Checked = true;
            }
        }

        public virtual void Save() 
        {
            foreach (var action in _saveActions) action();
        }

        public virtual void OnShow() 
        {
            // ★ 核心修复：每次显示页面时，重新从 Config 加载最新值到 UI
            // 这样能防止“旧页面覆盖新配置”的问题
            foreach (var action in _loadActions) action();
        }
    }
}