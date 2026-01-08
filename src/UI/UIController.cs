using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace LiteMonitor
{
    public class UIController : IDisposable
    {
        private readonly Settings _cfg;
        private readonly Form _form;
        private readonly HardwareMonitor _mon;
        private readonly System.Windows.Forms.Timer _timer;

        private UILayout _layout;
        private bool _layoutDirty = true;
        private bool _dragging = false;

        private List<GroupLayoutInfo> _groups = new();
        private List<Column> _hxColsHorizontal = new();
        private List<Column> _hxColsTaskbar = new();
        private HorizontalLayout? _hxLayout;
        public MainForm MainForm => (MainForm)_form;

        public List<Column> GetTaskbarColumns() => _hxColsTaskbar;

        public UIController(Settings cfg, Form form)
        {
            _cfg = cfg;
            _form = form;
            _mon = new HardwareMonitor(cfg);
            _mon.OnValuesUpdated += () => _form.Invalidate();

            _layout = new UILayout(ThemeManager.Current);

            _timer = new System.Windows.Forms.Timer { Interval = Math.Max(80, _cfg.RefreshMs) };
            _timer.Tick += (_, __) => Tick();
            _timer.Start();

            ApplyTheme(_cfg.Skin);
        }

        public float GetCurrentDpiScale()
        {
            using (Graphics g = _form.CreateGraphics())
            {
                return g.DpiX / 96f;
            }
        }

        public void ApplyTheme(string name)
        {
            // LanguageManager.Load(_cfg.Language);
            ThemeManager.Load(name);
            UIRenderer.ClearCache();
            var t = ThemeManager.Current;

            float dpiScale = GetCurrentDpiScale();   
            float userScale = (float)_cfg.UIScale;    
            float finalScale = dpiScale * userScale;

            t.Scale(dpiScale, userScale);
            if (!_cfg.HorizontalMode)
            {
                t.Layout.Width = (int)(_cfg.PanelWidth * finalScale);
                _form.Width = t.Layout.Width;
            }

            _form.BackColor = ThemeManager.ParseColor(t.Color.Background);
            TaskbarRenderer.ReloadStyle(_cfg);

            _layout = new UILayout(t);
            _hxLayout = null;

            BuildMetrics();
            _layoutDirty = true;

            BuildHorizontalColumns();

            _timer.Interval = Math.Max(80, _cfg.RefreshMs);
            _form.Invalidate();
            _form.Update();
            UIUtils.ClearBrushCache(); // 确保你有这个静态方法清空字典
        }

        public void RebuildLayout()
        {
            BuildMetrics();
            BuildHorizontalColumns(); 
            _layoutDirty = true;
            _form.Invalidate();
            _form.Update();
            
        }

        public void SetDragging(bool dragging) => _dragging = dragging;

        public void Render(Graphics g)
        {
            var t = ThemeManager.Current;
            _layout ??= new UILayout(t);

            // === 横屏模式 ===
            if (_cfg.HorizontalMode)
            {
                _hxLayout ??= new HorizontalLayout(t, _form.Width, LayoutMode.Horizontal);
                
                if (_layoutDirty)
                {
                    int h = _hxLayout.Build(_hxColsHorizontal);
                    _form.Width = _hxLayout.PanelWidth;
                    _form.Height = h;
                    _layoutDirty = false;
                }
                HorizontalRenderer.Render(g, t, _hxColsHorizontal, _hxLayout.PanelWidth);
                return;
            }

            // === 竖屏模式 ===
            if (_layoutDirty)
            {
                int h = _layout.Build(_groups);
                _form.Height = h;
                _layoutDirty = false;
            }

            UIRenderer.Render(g, _groups, t);
        }

        private bool _busy = false;

        private async void Tick()
        {
            if (_dragging || _busy) return;
            _busy = true;

            try
            {
                await System.Threading.Tasks.Task.Run(() => _mon.UpdateAll());

                // ① 更新竖屏用的 items
                foreach (var g in _groups)
                    foreach (var it in g.Items)
                    {
                        it.Value = _mon.Get(it.Key);
                        it.TickSmooth(_cfg.AnimationSpeed);
                    }

                // ② 同步更新横版 / 任务栏用的列数据
                void UpdateCol(Column col)
                {
                    if (col.Top != null)
                    {
                        col.Top.Value = _mon.Get(col.Top.Key);
                        col.Top.TickSmooth(_cfg.AnimationSpeed);
                    }
                    if (col.Bottom != null)
                    {
                        col.Bottom.Value = _mon.Get(col.Bottom.Key);
                        col.Bottom.TickSmooth(_cfg.AnimationSpeed);
                    }
                }
                foreach (var col in _hxColsHorizontal) UpdateCol(col);
                foreach (var col in _hxColsTaskbar) UpdateCol(col);
 
                CheckTemperatureAlert();
                _form.Invalidate();   
            }
            finally
            {
                _busy = false;
            }
        }

        // ★★★★★ [核心重构] 动态构建竖屏指标 ★★★★★
        // ★★★★★ [核心重构] 动态构建竖屏指标 ★★★★★
        private void BuildMetrics()
        {
            _groups = new List<GroupLayoutInfo>();

            var activeItems = _cfg.MonitorItems
                .Where(x => x.VisibleInPanel)
                .OrderBy(x => x.SortIndex)
                .ToList();

            if (activeItems.Count == 0) return;

            string currentGroupKey = "";
            List<MetricItem> currentGroupList = new List<MetricItem>();

            foreach (var cfgItem in activeItems)
            {
                // ★★★ 修改：直接使用统一的 UIGroup 属性 ★★★
                // 删掉了原本的 Split 和 if 判断
                string groupKey = cfgItem.UIGroup;

                if (groupKey != currentGroupKey && currentGroupList.Count > 0)
                {
                    _groups.Add(new GroupLayoutInfo(currentGroupKey, currentGroupList));
                    currentGroupList = new List<MetricItem>();
                }

                currentGroupKey = groupKey;

                string label = LanguageManager.T("Items." + cfgItem.Key);
                var item = new MetricItem 
                { 
                    Key = cfgItem.Key, 
                    Label = label 
                };
                
                float? val = _mon.Get(item.Key);
                item.Value = val;
                if (val.HasValue) item.DisplayValue = val.Value;

                currentGroupList.Add(item);
            }

            if (currentGroupList.Count > 0)
            {
                _groups.Add(new GroupLayoutInfo(currentGroupKey, currentGroupList));
            }
        }

        // ★★★★★ [核心重构] 动态构建横屏/任务栏列 ★★★★★
        private void BuildHorizontalColumns()
        {
            // 1. 构建主面板横屏列 (基于 VisibleInPanel)
            _hxColsHorizontal = BuildColumnsCore(forTaskbar: false);

            // 2. 构建任务栏列 (基于 VisibleInTaskbar)
            // 实现了"任务栏只看重要项"的需求
            _hxColsTaskbar = BuildColumnsCore(forTaskbar: true);
        }

        private List<Column> BuildColumnsCore(bool forTaskbar)
        {
            var cols = new List<Column>();

            // 1. 筛选 (保持不变)
            var query = _cfg.MonitorItems
                .Where(x => forTaskbar ? x.VisibleInTaskbar : x.VisibleInPanel);

            // 2. 排序 (★ 修改此处 ★)
            // 逻辑：
            // A. 如果正在构建任务栏 (forTaskbar == true) -> 使用 TaskbarSortIndex
            // B. 如果正在构建主界面横条 (forTaskbar == false) 且 开启了跟随 (HorizontalFollowsTaskbar) -> 使用 TaskbarSortIndex
            // C. 其他情况 (普通主界面排序) -> 使用 SortIndex
            
            if (forTaskbar || _cfg.HorizontalFollowsTaskbar)
            {
                query = query.OrderBy(x => x.TaskbarSortIndex);
            }
            else
            {
                query = query.OrderBy(x => x.SortIndex);
            }

            var items = query.ToList();

            // 3. 两两配对 (保持不变)
            bool singleLine = forTaskbar && _cfg.TaskbarSingleLine;
            int step = singleLine ? 1 : 2;

            for (int i = 0; i < items.Count; i += step)
            {
                var col = new Column();
                col.Top = CreateMetric(items[i]);

                if (!singleLine && i + 1 < items.Count)
                {
                    col.Bottom = CreateMetric(items[i + 1]);
                }
                cols.Add(col);
            }

            return cols;
        }

        private MetricItem CreateMetric(MonitorItemConfig cfg)
        {
            var item = new MetricItem 
            { 
                Key = cfg.Key 
                // 横屏模式下 Label 通常不显示或自动缩写，这里主要为了数据绑定
            };
            InitMetricValue(item);
            return item;
        }

        private void InitMetricValue(MetricItem? item)
        {
            if (item == null) return;
            float? val = _mon.Get(item.Key);
            item.Value = val;
            if (val.HasValue) item.DisplayValue = val.Value;
        }
        
        private void CheckTemperatureAlert()
        {
            if (!_cfg.AlertTempEnabled) return;
            // 3分钟冷却时间，避免频繁弹窗
            if ((DateTime.Now - _cfg.LastAlertTime).TotalMinutes < 3) return;

            int globalThreshold = _cfg.AlertTempThreshold; // 默认 80
            // ★ 针对磁盘给一个更灵敏的阈值 (硬盘超过60度通常就需要关注了)
            int diskThreshold = Math.Min(globalThreshold - 20, 60); 

            List<string> alertLines = new List<string>();
            string alertTitle = LanguageManager.T("Menu.AlertTemp"); 

            // 1. CPU
            float? cpuTemp = _mon.Get("CPU.Temp");
            if (cpuTemp.HasValue && cpuTemp.Value >= globalThreshold)
                alertLines.Add($"CPU {alertTitle}: 🔥{cpuTemp:F0}°C");

            // 2. GPU
            float? gpuTemp = _mon.Get("GPU.Temp");
            if (gpuTemp.HasValue && gpuTemp.Value >= globalThreshold)
                alertLines.Add($"GPU {alertTitle}: 🔥{gpuTemp:F0}°C");

            // ★★★ 3. 主板 (MOBO) ★★★
            float? moboTemp = _mon.Get("MOBO.Temp");
            if (moboTemp.HasValue && moboTemp.Value >= globalThreshold)
                alertLines.Add($"MOBO {alertTitle}: 🔥{moboTemp:F0}°C");

            // ★★★ 4. 磁盘 (DISK) - 使用更严格的阈值 ★★★
            float? diskTemp = _mon.Get("DISK.Temp");
            if (diskTemp.HasValue && diskTemp.Value >= diskThreshold)
                alertLines.Add($"DISK {alertTitle}: 🔥{diskTemp:F0}°C (>{diskThreshold}°C)");

            // 触发报警
            if (alertLines.Count > 0)
            {
                // 如果只有磁盘报警，标题显示磁盘的阈值，否则显示全局阈值
                string thresholdText = (alertLines.Count == 1 && alertLines[0].StartsWith("DISK")) 
                    ? $"(>{diskThreshold}°C)" 
                    : $"(>{globalThreshold}°C)";

                alertTitle += $" {thresholdText}";
                string bodyText = string.Join("\n", alertLines);
                
                ((MainForm)_form).ShowNotification(alertTitle, bodyText, ToolTipIcon.Warning);
                _cfg.LastAlertTime = DateTime.Now;
            }
        }
        public void Dispose()
        {
            _timer.Stop();
            _timer.Dispose();
            _mon.Dispose();
        }
    }
}