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

            if (_mode == LayoutMode.Taskbar)
            {
                // 任务栏上下没有额外 padding
                padV = 0;

                // === 任务栏行高 = taskbarHeight / 2（你选择的方案 A）===
                _rowH = taskbarHeight / 2;
            }

            // ==== 宽度初始值 ====
            int totalWidth = pad * 2;

            float dpi = _dpiScale;

            using (var g = Graphics.FromHwnd(IntPtr.Zero))
            {
                foreach (var col in cols)
                {
                    // ===== label（Top/Bottom 按最大宽度） =====
                    string labelTop = col.Top != null ? LanguageManager.T($"Short.{col.Top.Key}") : "";
                    string labelBottom = col.Bottom != null ? LanguageManager.T($"Short.{col.Bottom.Key}") : "";

                    Font labelFont, valueFont;

                    if (_mode == LayoutMode.Taskbar)
                    {
                        var fontStyle = _settings.TaskbarFontBold ? FontStyle.Bold : FontStyle.Regular;
                        var f = new Font(_settings.TaskbarFontFamily, _settings.TaskbarFontSize, fontStyle);
                        labelFont = f;
                        valueFont = f;
                    }
                    else
                    {
                        labelFont = _t.FontItem;
                        valueFont = _t.FontValue;
                    }

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
                    int paddingX = _rowH;
                    if (_mode == LayoutMode.Taskbar)
                    {
                        // 任务栏模式：紧凑固定左/右内间距
                        paddingX = (int)Math.Round(10 * dpi);
                    }
                    // ====== 列宽（不再限制最大/最小宽度）======
                    col.ColumnWidth = wLabel + wValue + paddingX;
                    totalWidth += col.ColumnWidth;

                    if (_mode == LayoutMode.Taskbar)
                    {
                        labelFont.Dispose();
                        valueFont.Dispose();
                    }
                }
            }

            // ===== gap 随 DPI =====
            int gapBase = (_mode == LayoutMode.Taskbar) ? 6 : 12;
            int gap = (int)Math.Round(gapBase * dpi);

            if (cols.Count > 1)
                totalWidth += (cols.Count - 1) * gap;

            PanelWidth = totalWidth;

            // ===== 设置列 Bounds =====
            int x = pad;

            foreach (var col in cols)
            {
                col.Bounds = new Rectangle(x, padV, col.ColumnWidth, _rowH * 2);

                if (_mode == LayoutMode.Taskbar)
                {
                    // ====== 任务栏模式：计算上下两行 Bounds ======
                    col.BoundsTop = new Rectangle(
                        col.Bounds.X,
                        col.Bounds.Y + 2,     // === 保留你选择的 A ±2 像素偏移 ===
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
                else
                {
                    // 横屏模式也生成上下行 Bounds
                    col.BoundsTop = new Rectangle(
                        col.Bounds.X,
                        col.Bounds.Y,
                        col.Bounds.Width,
                        _rowH
                    );

                    col.BoundsBottom = new Rectangle(
                        col.Bounds.X,
                        col.Bounds.Y + _rowH,
                        col.Bounds.Width,
                        _rowH
                    );
                }

                x += col.ColumnWidth + gap;
            }

            return padV * 2 + _rowH * 2;
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
