using LiteMonitor.src.Core;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LiteMonitor
{
    /// <summary>
    /// 布局模式
    /// Horizontal  → 主面板横版（使用 Theme 字体）
    /// Taskbar     → 任务栏显示（使用硬编码字体）
    /// </summary>
    public enum LayoutMode
    {
        Horizontal,
        Taskbar
    }

    /// <summary>
    /// Horizontal + Taskbar 共用布局器
    ///
    /// 说明：
    /// - 主界面横版布局用 Theme 字体驱动宽度
    /// - 任务栏布局完全独立，使用硬编码字体进行测量（不依赖主题）
    ///
    /// 输出：
    /// - Column.ColumnWidth   → 计算后的列宽
    /// - Column.Bounds        → 每列在最终区域内的坐标、宽度、两行高度
    /// </summary>
    public class HorizontalLayout
    {
        private readonly Theme _t;            // 横版模式下需要用到 Theme 字体
        private readonly LayoutMode _mode;    // 当前布局模式（核心分支点）

        private readonly int _padding;        // 左右 / 上下 padding
        private readonly int _rowH;           // 单行高度
        public int PanelWidth { get; private set; }

        // ---------- 横版模式的最大值模板（保持你原有逻辑） ----------
        private const string MAX_VALUE_NORMAL = "100°C";
        private const string MAX_VALUE_IO = "999KB";
        


        public HorizontalLayout(Theme t, int initialWidth, LayoutMode mode)
        {
            _t = t;
            _mode = mode;

            // ★ 横版：使用主题字体大小决定行高
            _padding = t.Layout.Padding;
            _rowH = Math.Max(t.FontItem.Height, t.FontValue.Height);

            if (mode == LayoutMode.Taskbar){
                // ★ 任务栏：硬编码布局参数（不使用主题 Layout）
                _rowH = 12;        // 单行高度更小（总高度 28）

            }
    

            PanelWidth = initialWidth;
        }


        /// <summary>
        /// 核心函数：计算每列的宽度和 Bounds
        /// </summary>
        public int Build(List<Column> cols)
        {
            if (cols == null || cols.Count == 0)
                return 0;

            int pad = _padding;     // 左右 padding
            int padV = _padding / 2; // 垂直 padding 减半

            if (_mode == LayoutMode.Taskbar){
                padV = 0;//_padding; // 垂直 padding 减半
            }

        // 宽度初始值 = 左右 padding
        int totalWidth = pad * 2;

            using (var g = Graphics.FromHwnd(IntPtr.Zero))
            {
                foreach (var col in cols)
                {
                    // --------------------------
                    // ① 计算 label 宽度
                    // --------------------------
                    string label =
                        col.Top != null ? LanguageManager.T($"Short.{col.Top.Key}") : "";

                    // ★ Taskbar 模式使用硬编码字体
                    Font fontLabel, fontValue;

                    if (_mode == LayoutMode.Taskbar)
                    {
                        fontLabel = _t.FontTaskbar;
                        fontValue = _t.FontTaskbar;
                    }
                    else
                    {
                        fontLabel = _t.FontItem;
                        fontValue = _t.FontValue;
                    }

                    int wLabel = TextRenderer.MeasureText(
                        g, label, fontLabel,
                        new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPadding
                    ).Width;

                    // ★ 任务栏限制 label 宽度（更窄）
                    if (_mode == LayoutMode.Taskbar)
                        wLabel = Math.Min(wLabel, 60);


                    // --------------------------
                    // ② 计算 value “最大可能宽度”
                    // --------------------------
                    string sample = GetMaxValueSample(col);

                    int wValue = TextRenderer.MeasureText(
                        g, sample, fontValue,
                        new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPadding
                    ).Width;


                    // --------------------------
                    // ③ 计算最终列宽
                    // --------------------------
                    int colW = wLabel + wValue + (_rowH);

                    // ★ 任务栏列宽下限
                    if (_mode == LayoutMode.Taskbar)
                        colW = Math.Max(48, colW);

                    col.ColumnWidth = colW;
                    totalWidth += colW;


                    // --------------------------
                    // ④ 任务栏字体对象由我们自己创建 → 需释放
                    // --------------------------
                    if (_mode == LayoutMode.Taskbar)
                    {
                        fontLabel.Dispose();
                        fontValue.Dispose();
                    }
                }
            }

            // --------------------------
            // ⑤ 列间距
            // --------------------------
            int gap = (_mode == LayoutMode.Taskbar) ? 6 : 12;
            totalWidth += (cols.Count - 1) * gap;


            // --------------------------
            // ⑥ 设置 Bounds 坐标
            // --------------------------
            PanelWidth = totalWidth;
            int x = pad;

            foreach (var col in cols)
            {
                col.Bounds = new Rectangle(x, padV, col.ColumnWidth, _rowH * 2);
                x += col.ColumnWidth + gap;
            }

            // --------------------------
            // ⑦ 返回高度（上下 padding + 两行）
            // --------------------------
            return padV * 2 + _rowH * 2;
        }


        /// <summary>
        /// 获取最大值模板：横版 / 任务栏各用不同模板
        /// </summary>
        private string GetMaxValueSample(Column col)
        {
            string key = col.Top?.Key?.ToUpperInvariant() ?? "";

            bool isIO =
                key.Contains("READ") || key.Contains("WRITE") ||
                key.Contains("UP") || key.Contains("DOWN");
            return isIO ? MAX_VALUE_IO : MAX_VALUE_NORMAL;
        }


    }


    /// <summary>
    /// 每一列对应 Top + Bottom 两行项目
    /// 宽度与 Bounds 坐标由 HorizontalLayout 计算
    /// </summary>
    public class Column
    {
        public MetricItem? Top;
        public MetricItem? Bottom;

        public int ColumnWidth;
        public Rectangle Bounds = Rectangle.Empty;
    }
}
