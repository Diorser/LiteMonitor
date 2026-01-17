using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiteMonitor.src.Core.Plugins
{
    /// <summary>
    /// 插件数据处理类
    /// 负责 JSON 解析、变量提取和数据转换
    /// </summary>
    public static class PluginProcessor
    {
        /// <summary>
        /// 从 JSON 对象中提取值
        /// 支持路径语法: "data.current.temp" 或 "list[0].id"
        /// </summary>
        /// <param name="root">JSON 根元素</param>
        /// <param name="path">提取路径</param>
        /// <returns>提取到的字符串值，如果失败返回 "?" 或 "[Empty]"</returns>
        public static string ExtractJsonValue(JsonElement root, string path)
        {
            try
            {
                var current = root;
                var parts = path.Split('.');

                foreach (var part in parts)
                {
                    if (part.Contains("[") && part.EndsWith("]"))
                    {
                        // 处理数组索引: list[0]
                        int idxStart = part.IndexOf('[');
                        string name = part.Substring(0, idxStart);
                        int index = int.Parse(part.Substring(idxStart + 1, part.Length - idxStart - 2));

                        if (!string.IsNullOrEmpty(name))
                        {
                            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) return "?";
                        }
                        
                        if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength()) return "?";
                        current = current[index];
                    }
                    else
                    {
                        // 处理普通属性
                        if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) return "?";
                    }
                }

                return current.ValueKind switch
                {
                    JsonValueKind.String => current.GetString() ?? "",
                    JsonValueKind.Number => current.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "",
                    _ => current.GetRawText() // Object or Array as string
                };
            }
            catch
            {
                return "?";
            }
        }

        /// <summary>
        /// 应用数据转换规则 (Transforms)
        /// 对上下文中的变量进行正则替换或映射
        /// </summary>
        /// <param name="transforms">转换规则列表</param>
        /// <param name="context">变量上下文</param>
        public static void ApplyTransforms(List<PluginTransform> transforms, Dictionary<string, string> context)
        {
            if (transforms == null) return;

            foreach (var t in transforms)
            {
                string src = t.TargetVar;
                if (!string.IsNullOrEmpty(t.SourceVar)) src = t.SourceVar;

                if (!context.ContainsKey(src)) continue; // 源变量不存在则跳过

                string val = context[src];

                if (t.Function == "regex_replace")
                {
                    try
                    {
                        val = Regex.Replace(val, t.Pattern, t.To); // 使用 To 替换匹配项
                    }
                    catch { }
                }
                else if (t.Function == "map")
                {
                    if (t.Map != null && t.Map.ContainsKey(val))
                    {
                        val = t.Map[val]; // 使用 Map 替换匹配项
                    }
                }

                context[t.TargetVar] = val;
            }
        }

        /// <summary>
        /// 处理字符串模版替换
        /// 将 "{{key}}" 替换为 context 中的值
        /// </summary>
        /// <param name="template">模版字符串</param>
        /// <param name="context">变量上下文</param>
        /// <returns>替换后的字符串</returns>
        public static string ResolveTemplate(string template, Dictionary<string, string> context)
        {
            if (string.IsNullOrEmpty(template)) return "";
            
            // 简单优化：如果模版不包含 {{ 则直接返回
            if (!template.Contains("{{")) return template;

            var result = template;
            foreach (var kv in context)
            {
                result = result.Replace("{{" + kv.Key + "}}", kv.Value);
            }

            // [Fix] Issue 3: 清理未解析的占位符 (防止 UI 显示 {{xxx}})
            // 简单的正则匹配 {{...}} 并替换为空，或者保留？
            // 用户反馈 {auto_city} 这种显示出来很丑，最好替换为 "?" 或空
            // 使用非贪婪匹配
            result = Regex.Replace(result, @"\{\{.*?\}\}", "");

            return result;
        }
    }
}
