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
            if (cols == null || cols.Count == 0) return 0;
            
            var s = _settings.GetStyle();
            int pad = _padding;
            int padV = _padding / 2;
            bool isTaskbarSingle = (_mode == LayoutMode.Taskbar && _settings.TaskbarSingleLine);

            if (_mode == LayoutMode.Taskbar)
            {
                padV = 0;
                _rowH = isTaskbarSingle ? taskbarHeight : taskbarHeight / 2;
            }

            int totalWidth = pad * 2;
            float dpi = _dpiScale;

            using (var g = Graphics.FromHwnd(IntPtr.Zero))
            {
                foreach (var col in cols)
                {
                    // 分别计算 Top 和 Bottom 的所需宽度，然后取最大值
                    int widthTop = 0;
                    int widthBottom = 0;

                    // 定义一个局部函数来测量单个 Item 的宽度
                    int MeasureItem(MetricItem item, bool isTop)
                    {
                        if (item == null) return 0;

                        // [通用逻辑] 如果隐藏标签 (ShortLabel==" ")，则只计算文本宽
                        if (item.ShortLabel == " ")
                        {
                            // 对于 Dashboard/IP 类，直接使用当前文本作为测量依据
                            // 注意：这里不再用 MaxValueSample，因为这种文本长度是不固定的
                            string valText = item.TextValue ?? item.GetFormattedText(true);
                            if (string.IsNullOrEmpty(valText)) return 0;

                            Font valFont = (_mode == LayoutMode.Taskbar) 
                                ? new Font(s.Font, s.Size, s.Bold ? FontStyle.Bold : FontStyle.Regular) 
                                : _t.FontValue;

                            int w = TextRenderer.MeasureText(g, valText, valFont,
                                new Size(int.MaxValue, int.MaxValue),
                                TextFormatFlags.NoPadding).Width;

                            if (_mode == LayoutMode.Taskbar) valFont.Dispose();
                            
                            // 纯文本项建议稍微加一点点左右 padding，防止紧贴
                            return w + 4; 
                        }
                        else
                        {
                            // [普通逻辑] 标签 + 数值 + 间距
                            // 1. Label
                            string label = item.ShortLabel;
                            Font labelFont, valueFont;
                            if (_mode == LayoutMode.Taskbar)
                            {
                                var fs = s.Bold ? FontStyle.Bold : FontStyle.Regular;
                                var f = new Font(s.Font, s.Size, fs);
                                labelFont = f; valueFont = f;
                            }
                            else
                            {
                                labelFont = _t.FontItem;
                                valueFont = _t.FontValue;
                            }
                            
                            int wLabel = TextRenderer.MeasureText(g, label, labelFont, 
                                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;

                            // 2. Value (使用样本值估算 或 真实值)
                            // ★★★ 修复：如果是 DASH/IP 等纯文本类 (TextValue有值)，直接测量真实文本 ★★★
                            // 否则对于 IP 这种长文本，使用默认样本(100°C)会导致列宽过窄
                            string sample;
                            if (!string.IsNullOrEmpty(item.TextValue))
                            {
                                sample = item.TextValue;
                            }
                            else
                            {
                                sample = GetMaxValueSample(col, isTop); // 注意：这里样本可能不准，但普通项差异不大
                            }

                            int wValue = TextRenderer.MeasureText(g, sample, valueFont, 
                                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;

                            // 3. Padding
                            int paddingX = _rowH;
                            if (_mode == LayoutMode.Taskbar) paddingX = (int)Math.Round(s.Inner * dpi);

                            if (_mode == LayoutMode.Taskbar)
                            {
                                labelFont.Dispose();
                                valueFont.Dispose();
                            }

                            return wLabel + wValue + paddingX;
                        }
                    }

                    // 执行测量
                    widthTop = MeasureItem(col.Top, true);
                    widthBottom = MeasureItem(col.Bottom, false);

                    // ★★★ 核心修复：列宽取上下两者的最大值 ★★★
                    // 这样即使 IP 在下面，列宽也会被 IP 撑大；
                    // 同时上面的普通项也能利用这个宽度正常显示（虽然左右会有空余，但不会重叠）
                    col.ColumnWidth = Math.Max(widthTop, widthBottom);
                    
                    totalWidth += col.ColumnWidth;
                }
            }


             // 组间距逻辑
            int gapBase = (_mode == LayoutMode.Taskbar) ? s.Gap : 12; 
            int gap = (int)Math.Round(gapBase * dpi); 

            if (cols.Count > 1) totalWidth += (cols.Count - 1) * gap;
            PanelWidth = totalWidth;
            
            // ===== 设置列 Bounds =====
            int x = pad;

            foreach (var col in cols)
            {
                int colHeight = isTaskbarSingle ? _rowH : _rowH * 2;
                col.Bounds = new Rectangle(x, padV, col.ColumnWidth, colHeight);

                if (_mode == LayoutMode.Taskbar)
                {
                    int fixOffset = 1; 
                    
                    if (isTaskbarSingle) {
                        col.BoundsTop = new Rectangle(x, col.Bounds.Y + fixOffset, col.ColumnWidth, colHeight);
                        col.BoundsBottom = Rectangle.Empty;
                    } else {
                        // 双行模式
                        col.BoundsTop = new Rectangle(x, col.Bounds.Y + s.VOff + fixOffset, col.ColumnWidth, _rowH - s.VOff);
                        col.BoundsBottom = new Rectangle(x, col.Bounds.Y + _rowH - s.VOff + fixOffset, col.ColumnWidth, _rowH);
                    }
                }
                else
                {
                    // 横屏模式
                    col.BoundsTop = new Rectangle(col.Bounds.X, col.Bounds.Y, col.Bounds.Width, _rowH);
                    col.BoundsBottom = new Rectangle(col.Bounds.X, col.Bounds.Y + _rowH, col.Bounds.Width, _rowH);
                }
                
                // [补充修正] 如果是 NET.IP 混合列，我们需要告诉 Renderer 不要画 Label 区域，而是全宽显示
                // 但由于 Renderer 是根据 (LabelRect, ValueRect) 绘图的，而 HorizontalLayout 不负责计算具体的 LabelRect
                // 所以我们依赖 TaskbarRenderer 的逻辑：它会看 Label 是否为空。
                // 只要列宽足够（ColumnWidth 够大），TaskbarRenderer 右对齐 Value 时就不会出问题。

                x += col.ColumnWidth + gap;
            }

            return padV * 2 + (isTaskbarSingle ? _rowH : _rowH * 2);
        }

        private string GetMaxValueSample(Column col, bool isTop)
        {
            // ★★★ 优化：移除 ToUpperInvariant() 分配，改用忽略大小写的比较 ★★★
            string key = (isTop ? col.Top?.Key : col.Bottom?.Key) ??
                         (isTop ? col.Bottom?.Key : col.Top?.Key) ?? "";

            // ★★★ 简单匹配，使用 IndexOf 替换 Contains ★★★
            if (key.IndexOf("CLOCK", StringComparison.OrdinalIgnoreCase) >= 0) return MAX_VALUE_CLOCK;
            if (key.IndexOf("POWER", StringComparison.OrdinalIgnoreCase) >= 0) return MAX_VALUE_POWER;

            bool isIO =
                key.IndexOf("READ", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("WRITE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                key.IndexOf("UP", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("DOWN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                key.IndexOf("DAYUP", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("DAYDOWN", StringComparison.OrdinalIgnoreCase) >= 0;

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
