namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 插件系统常量定义
    /// </summary>
    public static class PluginConstants
    {
        /// <summary>
        /// 插件监控项的前缀 Key
        /// </summary>
        public const string DASH_PREFIX = "DASH.";

        /// <summary>
        /// 默认刷新间隔 (秒)
        /// </summary>
        public const int DEFAULT_INTERVAL = 1;

        /// <summary>
        /// 错误状态显示文本
        /// </summary>
        public const string STATUS_ERROR = "❌";

        /// <summary>
        /// 加载中状态显示文本
        /// </summary>
        public const string STATUS_LOADING = "...";

        /// <summary>
        /// 未知状态显示文本 (解析失败/路径错误)
        /// </summary>
        public const string STATUS_UNKNOWN = "❓";

        /// <summary>
        /// 空值状态显示文本 (值为空)
        /// </summary>
        public const string STATUS_EMPTY = "⛔";

        /// <summary>
        /// 插件配置文件扩展名
        /// </summary>
        public const string CONFIG_EXT = "*.json";
    }
}
