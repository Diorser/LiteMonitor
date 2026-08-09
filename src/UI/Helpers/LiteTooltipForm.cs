using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 一个轻量级、无闪烁、高性能的自定义悬浮提示窗体
    /// 支持 Grid 布局、主题颜色渲染和 Emoji 文本绘制
    /// </summary>
    public class LiteTooltipForm : Form
    {
        // 布局常量
        private const int PADDING_X = 10;
        private const int PADDING_Y = 8;
        private const int ROW_HEIGHT = 22;
        private const int GROUP_GAP = 4;     // 组间距
        private const int COLUMN_GAP = 8;    // 标签和值之间的最小间距
        
        // 缓存数据
        private List<GroupLayoutInfo>? _groups;
        private Theme? _theme;
        
        // 预计算的布局信息
        private int _totalWidth = 0;
        private int _totalHeight = 0;

        private float _scale = 1.0f;
        private bool _isBold = false;

        // 样式配置
        private Color _bgColor = Color.FromArgb(43, 45, 49); 
        private Color _borderColor = Color.FromArgb(60, 60, 60);
        private Color _separatorColor = Color.FromArgb(80, 80, 80);

        public LiteTooltipForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = Color.Black; 
            
            // 启用高质量绘制
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        protected override bool ShowWithoutActivation => true; // 不抢焦点

        /// <summary>
        /// 设置数据并触发重绘（仅当内容变化时）
        /// </summary>
        public void SetData(List<GroupLayoutInfo> groups, Theme theme, double opacity, int fixedWidth, float scale, bool isBold)
        {
            _groups = groups;
            _theme = theme;
            _scale = scale;
            _isBold = isBold;
            this.Opacity = Math.Clamp(opacity, 0.1, 1.0);

            // 同步主题颜色
            if (_theme != null)
            {
                // 智能计算背景色：
                // 主界面的组背景是叠加在窗口背景之上的。如果组背景有透明度，
                // 直接使用组背景会导致颜色与主界面不一致（看起来更黑，因为叠加在黑色底色上）。
                // 所以我们需要模拟主界面的渲染：将 组背景 混合到 窗口背景 上。
                Color windowBg = ThemeManager.ParseColor(_theme.Color.Background);
                Color groupBg = ThemeManager.ParseColor(_theme.Color.GroupBackground);
                _bgColor = BlendColor(windowBg, groupBg);

                // 边框色可以用 BarBackground 或自定义稍微亮一点的
                _borderColor = ThemeManager.ParseColor(_theme.Color.BarBackground);
                _separatorColor = ThemeManager.ParseColor(_theme.Color.BarBackground);
            }

            CalculateLayout(fixedWidth);
            
            // 优化：仅当内容变化且当前可见时才触发重绘
            if (Visible)
            {
                Invalidate();
            }
        }

        /// <summary>
        /// 混合两种颜色 (Foreground over Background)
        /// </summary>
        private Color BlendColor(Color bg, Color fg)
        {
            // Alpha 混合算法: Out = Src * Alpha + Dst * (1 - Alpha)
            float alpha = fg.A / 255f;
            float invAlpha = 1.0f - alpha;

            int r = (int)(fg.R * alpha + bg.R * invAlpha);
            int g = (int)(fg.G * alpha + bg.G * invAlpha);
            int b = (int)(fg.B * alpha + bg.B * invAlpha);

            // 确保结果不透明 (防止与窗体底色 Black 再次混合)
            return Color.FromArgb(255, Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
        }

        private int S(int val) => (int)(val * _scale);

        private Font GetTextFont()
        {
            return UIUtils.GetFont(
                _theme!.FontItem.FontFamily.Name,
                Math.Max(8f, _theme.FontItem.Size - 0.5f),
                _isBold);
        }

        private int GetRowHeight(Font font)
        {
            // 缩放较小时字体仍有最小字号，行高不能继续缩到字体实际高度以下。
            return Math.Max(S(ROW_HEIGHT), font.Height + Math.Max(2, S(2)));
        }

        private static int MeasureTextWidth(string text, Font font)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            return TextRenderer.MeasureText(
                text,
                font,
                Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
        }

        private void CalculateLayout(int fixedWidth)
        {
            if (_groups == null || _groups.Count == 0 || _theme == null)
            {
                Size = new Size(0, 0);
                return;
            }

            var smallFont = GetTextFont();

            // 1. 固定宽度作为基准，但不能小于实际标签和值所需的宽度。
            //    低缩放下字体有最小字号，单纯按比例缩小会导致两列文字重叠。
            int paddingX = Math.Max(1, S(PADDING_X));
            int textGap = Math.Max(4, S(COLUMN_GAP));
            int minContentWidth = 0;

            foreach (var group in _groups)
            {
                foreach (var item in group.Items)
                {
                    string label = item.Label;
                    if (string.IsNullOrEmpty(label)) label = item.Key;

                    string value = item.GetFormattedText(false);
                    int lineWidth = MeasureTextWidth(label, smallFont)
                        + textGap
                        + MeasureTextWidth(value, smallFont);
                    minContentWidth = Math.Max(minContentWidth, lineWidth);
                }
            }

            _totalWidth = Math.Max(fixedWidth, paddingX * 2 + minContentWidth);

            // 2. 计算总高度
            int paddingY = Math.Max(1, S(PADDING_Y));
            int groupGap = Math.Max(1, S(GROUP_GAP));
            int rowHeight = GetRowHeight(smallFont);

            // 优化：预先计算总行数，避免循环累加
            int totalLines = _groups.Sum(g => g.Items.Count);
            int totalGaps = Math.Max(0, _groups.Count - 1);
            
            // 总高度 = 上下Padding + (行数 * 行高) + (间隔数 * (间隔高度 * 2 + 1))
            int contentHeight = (totalLines * rowHeight) + (totalGaps * (groupGap * 2 + 1));
            _totalHeight = paddingY * 2 + contentHeight;

            // 3. 更新窗体尺寸
            if (Width != _totalWidth || Height != _totalHeight)
            {
                Size = new Size(_totalWidth, _totalHeight);
            }
            // 移除圆角设置以避免锯齿
            Region = null; 
        }

        public void UpdatePosition(Rectangle targetRect, Point cursorPosition)
        {
            var screen = Screen.FromRectangle(targetRect);
            Rectangle workArea = screen.WorkingArea;

            // 缓存宽高属性，减少底层消息调用 (微优化)
            int w = Width;
            int h = Height;

            // ★★★ 极简修复：根据任务栏形状自动判断方向 ★★★
            bool isVerticalTaskbar = targetRect.Height > targetRect.Width;

            int x, y;

            if (isVerticalTaskbar)
            {
                // ====== 垂直模式 ======
                // Y轴：跟随鼠标垂直居中
                y = cursorPosition.Y - (h / 2);
                
                // X轴：贴合任务栏边缘
                // 判断任务栏在屏幕哪一侧 (使用工作区中心点判断更准确)
                bool isRightSide = (targetRect.Left + targetRect.Width / 2) > (workArea.Left + workArea.Width / 2);

                if (isRightSide)
                {
                    // 任务栏在右 -> 窗口显示在左侧
                    x = targetRect.Left - w - S(4);
                    // 防止超出屏幕左边缘
                    if (x < workArea.Left) x = workArea.Left + 4;
                }
                else
                {
                    // 任务栏在左 -> 窗口显示在右侧
                    x = targetRect.Right + S(4);
                    // 防止超出屏幕右边缘
                    if (x + w > workArea.Right) x = workArea.Right - w - 4;
                }

                // Y轴边界检查 (防止上下超出屏幕)
                if (y < workArea.Top) y = workArea.Top + 4;
                if (y + h > workArea.Bottom) y = workArea.Bottom - h - 4;
            }
            else
            {
                // ====== 水平模式 (原有逻辑) ======
                
                // 水平方向：优先基于鼠标位置居中 (用户偏好)
                x = cursorPosition.X - (w / 2);
                
                // 垂直方向：基于目标窗体位置 (确保在自动隐藏任务栏时位置稳定)
                y = targetRect.Top - h - S(4);

                // --- X轴约束 ---
                if (x < workArea.Left) x = workArea.Left + 4;
                if (x + w > workArea.Right) x = workArea.Right - w - 4;

                // --- Y轴约束 ---
                // [逻辑优化] 修正多显示器下的坐标判断 (原代码未考虑 workArea.Top 偏移)
                bool isBottomSide = targetRect.Top > (workArea.Top + workArea.Height / 2);

                if (isBottomSide)
                {
                    // 底部模式：尝试显示在上方
                    if (y < workArea.Top) y = workArea.Top + 2; 
                    if (y + h > workArea.Bottom) y = workArea.Bottom - h - 2;
                }
                else
                {
                    // 顶部/上侧模式：显示在下方
                    y = targetRect.Bottom + S(4);
                    if (y + h > workArea.Bottom) y = workArea.Bottom - h - 2;
                }
            }

            // [性能优化] 仅当坐标实际发生变化时才更新 Location，避免不必要的重绘消息
            if (Location.X != x || Location.Y != y)
            {
                Location = new Point(x, y);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // 极速绘制模式
            var g = e.Graphics;
            g.PixelOffsetMode = PixelOffsetMode.None; // 锐利线条
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit; // 清晰文字

            // 1. 背景
            using (var b = new SolidBrush(_bgColor))
            {
                g.FillRectangle(b, ClientRectangle);
            }

            // 2. 边框
            // 优化：重用 Pen (虽然 Pen 创建开销很小，但为了极致性能)
            // 实际上 .NET 的 Pen(Color) 构造函数已经很轻量。
            // 重点是：减少 CreateGraphics 调用（我们用的是 e.Graphics，很好）。
            
            using (var p = new Pen(_borderColor))
            {
                g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }

            if (_groups == null || _theme == null) return;

            // 3. 绘制内容
            int paddingX = S(PADDING_X);
            int paddingY = S(PADDING_Y);
            int rowHeight = S(ROW_HEIGHT);
            int groupGap = S(GROUP_GAP);

            int currentY = paddingY;
            
            // 准备资源
            Color labelColor = ThemeManager.ParseColor(_theme.Color.TextPrimary);
            using var penSep = new Pen(_separatorColor);

            // 优化：提前获取字体，避免在循环中重复查找 (GetFont 内部虽然有字典缓存，但仍有哈希查找开销)
            var smallFont = GetTextFont();
            rowHeight = GetRowHeight(smallFont);

            for (int i = 0; i < _groups.Count; i++)
            {
                var group = _groups[i];

                // 绘制分隔线 (跳过第一组)
                if (i > 0)
                {
                    currentY += groupGap; // 线上方空隙
                    g.DrawLine(penSep, paddingX, currentY, Width - paddingX, currentY);
                    currentY += 1 + groupGap; // 线 + 线下方空隙
                }

                // 绘制组内项目
                foreach (var item in group.Items)
                {
                    // A. Label (左对齐) - 占左半边 (减去 Padding)
                    string label = item.Label;
                    if (string.IsNullOrEmpty(label)) label = item.Key;
                    
                    var rectLabel = new Rectangle(paddingX, currentY, Width - paddingX * 2, rowHeight);

                    TextRenderer.DrawText(g, label, smallFont, rectLabel, labelColor, 
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

                    // B. Value (右对齐) - 占右半边 (复用同一个 Rect，靠右对齐即可)
                    string valText = item.GetFormattedText(false); 
                    Color valColor = item.GetTextColor(_theme);
                    
                    TextRenderer.DrawText(g, valText, smallFont, rectLabel, valColor,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

                    currentY += rowHeight;
                }
            }
        }
    }
}
