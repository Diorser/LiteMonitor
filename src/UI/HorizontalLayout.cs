using LiteMonitor.src.Core;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LiteMonitor
{
    public enum LayoutMode
    {
        Horizontal,
        Taskbar
    }

    public class HorizontalLayout
    {
        private readonly Theme _t;
        private readonly LayoutMode _mode;
        private readonly Settings _settings;

        private readonly int _padding;
        private int _rowH;

        // DPI
        private readonly float _dpiScale;

        public int PanelWidth { get; private set; }

        // ====== 保留你原始最大宽度模板（横屏模式用） ======
        private const string MAX_VALUE_NORMAL = "100°C";
        private const string MAX_VALUE_IO = "999KB";
        private const string MAX_VALUE_CLOCK = "99GHz"; 
        private const string MAX_VALUE_POWER = "999W";

        public HorizontalLayout(Theme t, int initialWidth, LayoutMode mode, Settings? settings = null)
        {
            _t = t;
            _mode = mode;
            _settings = settings ?? Settings.Load();

            using (var g = Graphics.FromHwnd(IntPtr.Zero))
            {
                _dpiScale = g.DpiX / 96f;
            }

            _padding = t.Layout.Padding;

            if (mode == LayoutMode.Horizontal)
                _rowH = Math.Max(t.FontItem.Height, t.FontValue.Height);
            else
                _rowH = 0; // 任务栏模式稍后根据 taskbarHeight 决定

            PanelWidth = initialWidth;
        }

        /// <summary>
        /// Build：横屏/任务栏共用布局
        /// </summary>
        public int Build(List<Column> cols, int taskbarHeight = 32)
        {
            if (cols == null || cols.Count == 0)
                return 0;

            int pad = _padding;
            int padV = _padding / 2;

            // ★ 定义单行模式变量
            bool isTaskbarSingle = (_mode == LayoutMode.Taskbar && _settings.TaskbarSingleLine);

            if (_mode == LayoutMode.Taskbar)
            {
                // 任务栏上下没有额外 padding
                padV = 0;

                // ★ 如果是单行模式，行高=全高；否则=半高
                _rowH = isTaskbarSingle ? taskbarHeight : taskbarHeight / 2;
                
                // 【测试完后请删除下面这行】
                //_rowH = 14; 
            }

            // ==== 宽度初始值 ====
            int totalWidth = pad * 2;

            float dpi = _dpiScale;

            Font labelFont, valueFont;
            // 标记是否需要保留字体对象（如果生成了修正字体传给Renderer，就不在此处Dispose）
            bool keepFontAlive = false; 

            using (var g = Graphics.FromHwnd(IntPtr.Zero))
            {
                if (_mode == LayoutMode.Taskbar)
                {
                    var fontStyle = _settings.TaskbarFontBold ? FontStyle.Bold : FontStyle.Regular;
                    
                    // 1. 默认创建标准字体 (保持原始逻辑)
                    var f = new Font(_settings.TaskbarFontFamily, _settings.TaskbarFontSize, fontStyle);
                    
                    // ★★★ 2. 核心修复：检测是否出现 DPI 双倍缩放 BUG ★★★
                    // 测量 "0" 的高度
                    int realTextHeight = TextRenderer.MeasureText(g, "0", f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Height;

                    // 如果实际文字高度 > 行高 (说明被放大了，装不下)，则触发修正
                    if (realTextHeight > _rowH)
                    {
                        f.Dispose(); // 销毁过大的字体

                        // 创建修正字体：强制使用 Pixel 单位
                        // ★★★ 修正点：扣除上下硬编码偏移占用的 4px 空间 ★★★
                        // 原始代码 Top Y+2, Bottom Y+_rowH-2，中间可用空间是 _rowH - 4
                        float availableHeight = Math.Max(1, _rowH - 4); 
                        float fixSize = availableHeight * 0.9f; 
                        
                        if (fixSize < 5) fixSize = 5;
                        
                        var fixFont = new Font(_settings.TaskbarFontFamily, fixSize, fontStyle, GraphicsUnit.Pixel);
                        
                        // 将修正字体注入渲染器
                        TaskbarRenderer.SetCorrectionFont(fixFont);
                        keepFontAlive = true; // Renderer 现在持有它，不要在这里 Dispose

                        labelFont = fixFont;
                        valueFont = fixFont;
                    }
                    else
                    {
                        // 正常情况：清除之前的修正，完全按原始逻辑走
                        TaskbarRenderer.SetCorrectionFont(null);
                        labelFont = f;
                        valueFont = f;
                    }
                }
                else
                {
                    labelFont = _t.FontItem;
                    valueFont = _t.FontValue;
                }

                foreach (var col in cols)
                {
                    // ===== label（Top/Bottom 按最大宽度） =====
                    string labelTop = col.Top != null ? LanguageManager.T($"Short.{col.Top.Key}") : "";
                    string labelBottom = col.Bottom != null ? LanguageManager.T($"Short.{col.Bottom.Key}") : "";

                    int wLabelTop = TextRenderer.MeasureText(
                        g, labelTop, labelFont,
                        new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPadding
                    ).Width;

                    int wLabelBottom = TextRenderer.MeasureText(
                        g, labelBottom, labelFont,
                        new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPadding
                    ).Width;

                    int wLabel = Math.Max(wLabelTop, wLabelBottom);

                    // ========== value 最大宽度 ==========
                    string sampleTop = GetMaxValueSample(col, true);
                    string sampleBottom = GetMaxValueSample(col, false);

                    int wValueTop = TextRenderer.MeasureText(
                        g, sampleTop, valueFont,
                        new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPadding
                    ).Width;

                    int wValueBottom = TextRenderer.MeasureText(
                        g, sampleBottom, valueFont,
                        new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPadding
                    ).Width;

                    int wValue = Math.Max(wValueTop, wValueBottom);
                    int paddingX = (int)Math.Round(_rowH * 0.8f);
                    if (_mode == LayoutMode.Taskbar)
                    {
                        // 任务栏模式：紧凑固定左/右内间距 (保持原始硬编码 8)
                        paddingX = (int)Math.Round(8 * dpi);
                    }
                    // ====== 列宽（不再限制最大/最小宽度）======
                    col.ColumnWidth = wLabel + wValue + paddingX;
                    totalWidth += col.ColumnWidth;
                }
            }
            
            // 只有在没有生成修正字体的情况下，才Dispose（因为修正字体被 Renderer 引用了）
            if (_mode == LayoutMode.Taskbar && !keepFontAlive)
            {
                labelFont.Dispose();
                valueFont.Dispose();
            }

            // ===== gap 随 DPI =====
            // 保持原始硬编码 6 / 12
            int gapBase = (_mode == LayoutMode.Taskbar) ? 6 : 12;
            int gap = (int)Math.Round(gapBase * dpi);

            if (cols.Count > 1)
                totalWidth += (cols.Count - 1) * gap;

            PanelWidth = totalWidth;

            // ===== 设置列 Bounds =====
            int x = pad;

            foreach (var col in cols)
            {
                // ★ 整个列的高度
                int colHeight = isTaskbarSingle ? _rowH : _rowH * 2;
                col.Bounds = new Rectangle(x, padV, col.ColumnWidth, colHeight);

                if (_mode == LayoutMode.Taskbar)
                {
                    if (isTaskbarSingle)
                    {
                        // ★★★ 单行模式：Top 占满全高，Bottom 为空 ★★★
                        col.BoundsTop = col.Bounds; 
                        col.BoundsBottom = Rectangle.Empty;
                    }
                    else
                    {
                        // ★★★ 原有双行模式：恢复最原始的 +2/-2 偏移 ★★★
                        col.BoundsTop = new Rectangle(
                            col.Bounds.X,
                            col.Bounds.Y + 2,
                            col.Bounds.Width,
                            _rowH - 2
                        );

                        col.BoundsBottom = new Rectangle(
                            col.Bounds.X,
                            col.Bounds.Y + _rowH - 2,
                            col.Bounds.Width,
                            _rowH
                        );
                    }
                }
                else
                {
                    // 横屏模式 (保持不变)
                    col.BoundsTop = new Rectangle(col.Bounds.X, col.Bounds.Y, col.Bounds.Width, _rowH);
                    col.BoundsBottom = new Rectangle(col.Bounds.X, col.Bounds.Y + _rowH, col.Bounds.Width, _rowH);
                }

                x += col.ColumnWidth + gap;
            }

            // ★ 返回总高度
            return padV * 2 + (isTaskbarSingle ? _rowH : _rowH * 2);
        }

        private string GetMaxValueSample(Column col, bool isTop)
        {
            string key = (isTop ? col.Top?.Key : col.Bottom?.Key)?.ToUpperInvariant() ??
                         (isTop ? col.Bottom?.Key : col.Top?.Key)?.ToUpperInvariant() ?? "";

            // ★★★ 简单匹配，返回常量 ★★★
            if (key.Contains("CLOCK")) return MAX_VALUE_CLOCK;
            if (key.Contains("POWER")) return MAX_VALUE_POWER;

            bool isIO =
                key.Contains("READ") || key.Contains("WRITE") ||
                key.Contains("UP") || key.Contains("DOWN") ||
                key.Contains("DAYUP") || key.Contains("DAYDOWN");

            return isIO ? MAX_VALUE_IO : MAX_VALUE_NORMAL;
        }
    }

    public class Column
    {
        public MetricItem? Top;
        public MetricItem? Bottom;

        public int ColumnWidth;
        public Rectangle Bounds = Rectangle.Empty;

        // ★★ B 方案新增：上下行布局由 Layout 计算，不再由 Renderer 处理
        public Rectangle BoundsTop = Rectangle.Empty;
        public Rectangle BoundsBottom = Rectangle.Empty;
    }
}