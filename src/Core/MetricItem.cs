using System;
using System.Drawing;

namespace LiteMonitor
{
    /// <summary>
    /// 定义该指标项的渲染风格
    /// </summary>
    public enum MetricRenderStyle
    {
        StandardBar, // 标准：左标签 + 右数值 + 底部进度条 (CPU/MEM/GPU)
        TwoColumn    // 双列：居中标签 + 居中数值 (NET/DISK)
    }

    public class MetricItem
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        
        // 原始数值与显示数值
        public float? Value { get; set; } = null;
        public float DisplayValue { get; set; } = 0f;

        // =============================
        // 布局数据 (由 UILayout 计算填充)
        // =============================
        
        /// <summary>
        /// 渲染风格
        /// </summary>
        public MetricRenderStyle Style { get; set; } = MetricRenderStyle.StandardBar;

        /// <summary>
        /// 整个项目的边界（用于鼠标交互或调试）
        /// </summary>
        public Rectangle Bounds { get; set; } = Rectangle.Empty;

        // --- 内部组件区域 ---
        public Rectangle LabelRect;   // 标签文本区域
        public Rectangle ValueRect;   // 数值文本区域
        public Rectangle BarRect;     // 进度条区域 (仅 StandardBar 有效)
        public Rectangle BackRect;    // 背景区域 (用于圆角矩形等)

        /// <summary>
        /// 平滑更新显示值
        /// </summary>
        public void TickSmooth(double speed)
        {
            if (!Value.HasValue) return;
            float target = Value.Value;
            float diff = Math.Abs(target - DisplayValue);

            if (diff < 0.05f) return;

            if (diff > 15f || speed >= 0.9)
                DisplayValue = target;
            else
                DisplayValue += (float)((target - DisplayValue) * speed);
        }
    }
}