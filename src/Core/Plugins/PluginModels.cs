using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LiteMonitor.src.Core.Plugins
{
    // ==========================================
    // 1. 模版定义 (对应 JSON 文件)
    // ==========================================
    public class PluginTemplate
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = ""; // 模版唯一ID (如 "com.weather")

        [JsonPropertyName("meta")]
        public PluginMeta Meta { get; set; } = new();

        [JsonPropertyName("inputs")]
        public List<PluginInput> Inputs { get; set; } = new();

        [JsonPropertyName("execution")]
        public PluginExecution Execution { get; set; } = new();

        [JsonPropertyName("outputs")]
        public List<PluginOutput> Outputs { get; set; } = new();

        [JsonPropertyName("display")]
        public PluginDisplay Display { get; set; } = new();
    }

    public class PluginDisplay
    {
        // 监控项显示名称 (支持 {{key}} 替换)
        [JsonPropertyName("label")]
        public string Label { get; set; } = ""; 

        // 任务栏简写 (支持 {{key}} 替换)
        [JsonPropertyName("short_label")]
        public string ShortLabel { get; set; } = "";
    }

    public class PluginMeta
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0";

        [JsonPropertyName("author")]
        public string Author { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";
        
        [JsonPropertyName("url")]
        public string Url { get; set; } = "";
    }

    public class PluginInput
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = ""; // 参数名 (如 "city_code")

        [JsonPropertyName("label")]
        public string Label { get; set; } = ""; // 显示给用户的名称

        [JsonPropertyName("type")]
        public string Type { get; set; } = "text"; // text, password, select...

        [JsonPropertyName("default")]
        public string DefaultValue { get; set; } = "";
        
        [JsonPropertyName("placeholder")]
        public string Placeholder { get; set; } = "";

        // Scope: "global" (default) or "target"
        // global: 整个插件实例共享 (如 API Key)
        // target: 每个目标单独配置 (如 股票代码)
        [JsonPropertyName("scope")]
        public string Scope { get; set; } = "global"; 
    }

    public class PluginExecution
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "api_json"; // api_json, api_text, chain

        [JsonPropertyName("method")]
        public string Method { get; set; } = "GET"; // GET, POST

        [JsonPropertyName("interval")]
        public int Interval { get; set; } = 60000;

        [JsonPropertyName("url")]
        public string Url { get; set; } = ""; // 支持 {{key}} 替换

        [JsonPropertyName("body")]
        public string Body { get; set; } = ""; // POST body, 支持 {{key}} 替换

        [JsonPropertyName("headers")]
        public Dictionary<string, string>? Headers { get; set; }

        [JsonPropertyName("extract")]
        public Dictionary<string, string> Extract { get; set; } = new();

        [JsonPropertyName("process")]
        public List<PluginTransform>? Process { get; set; }

        [JsonPropertyName("steps")]
        public List<PluginExecutionStep>? Steps { get; set; }
    }

    public class PluginExecutionStep
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [JsonPropertyName("method")]
        public string Method { get; set; } = "GET";

        [JsonPropertyName("body")]
        public string Body { get; set; } = "";

        [JsonPropertyName("headers")]
        public Dictionary<string, string>? Headers { get; set; }

        [JsonPropertyName("response_encoding")]
        public string ResponseEncoding { get; set; } = "utf-8"; // utf-8, gbk

        [JsonPropertyName("response_format")]
        public string ResponseFormat { get; set; } = "json"; // json, jsonp, text

        [JsonPropertyName("extract")]
        public Dictionary<string, string> Extract { get; set; } = new(); // Variables to extract

        [JsonPropertyName("process")]
        public List<PluginTransform>? Process { get; set; }

        [JsonPropertyName("cache_minutes")]
        public int CacheMinutes { get; set; } = 0; // 0=No Cache, -1=Forever
    }

    public class PluginTransform
    {
        [JsonPropertyName("var")]
        public string TargetVar { get; set; } = ""; // 目标变量名 (如 "temp")

        [JsonPropertyName("source")]
        public string SourceVar { get; set; } = ""; // 源变量名 (如 "temp")

        [JsonPropertyName("function")]
        public string Function { get; set; } = "regex_replace"; // 转换函数 (regex_replace, map)

        // For regex_replace
        [JsonPropertyName("pattern")]
        public string Pattern { get; set; } = ""; // 正则表达式模式
        [JsonPropertyName("to")]
        public string To { get; set; } = "";// 替换字符串

        // For map
        [JsonPropertyName("map")]
        public Dictionary<string, string>? Map { get; set; }
    }

    public class PluginOutput
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = ""; // 唯一标识后缀，如 "temp"

        [JsonPropertyName("label")]
        public string Label { get; set; } = ""; // 完整显示名称模式，如 "{{city}} 温度"

        [JsonPropertyName("short_label")]
        public string ShortLabel { get; set; } = ""; // 任务栏名称模式

        [JsonPropertyName("format")]
        public string Format { get; set; } = ""; // 数据格式，如 "{{val}}"

        [JsonPropertyName("unit")]
        public string Unit { get; set; } = ""; // 单位
    }
}
