using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 插件数据处理类
    /// 负责 JSON 解析、变量提取和数据转换
    /// </summary>
    public static class PluginProcessor
    {
        // [Optimization] Cached Compiled Regex for Template Resolution
        private static readonly Regex _templateRegex = new Regex(@"\{\{(.+?)\}\}", RegexOptions.Compiled);

        /// <summary>
        /// 从 JSON 对象中提取值
        /// 支持路径语法: "data.current.temp" 或 "list[0].id"
        /// </summary>
        public static string ExtractJsonValue(JsonElement root, string path)
        {
            try
            {
                var current = root;
                // [Optimization] Avoid frequent Split if path is simple
                if (!path.Contains('.') && !path.Contains('[')) 
                {
                    if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(path, out var prop))
                        return JsonElementToString(prop);
                    return PluginConstants.STATUS_UNKNOWN;
                }

                var parts = path.Split('.');

                foreach (var part in parts)
                {
                    if (part.Contains("[") && part.EndsWith("]"))
                    {
                        // Handle Array Index: list[0]
                        int idxStart = part.IndexOf('[');
                        string name = part.Substring(0, idxStart);
                        
                        // Parse index (fast span-like logic not available in .NET Framework / Standard 2.0 easily, keep substring)
                        if (!int.TryParse(part.Substring(idxStart + 1, part.Length - idxStart - 2), out int index)) return PluginConstants.STATUS_UNKNOWN;

                        if (!string.IsNullOrEmpty(name))
                        {
                            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) return PluginConstants.STATUS_UNKNOWN;
                        }
                        
                        if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength()) return PluginConstants.STATUS_UNKNOWN;
                        current = current[index];
                    }
                    else
                    {
                        // Handle Object Property
                        if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) return PluginConstants.STATUS_UNKNOWN;
                    }
                }

                return JsonElementToString(current);
            }
            catch
            {
                return PluginConstants.STATUS_UNKNOWN;
            }
        }

        private static string JsonElementToString(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? "",
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "",
                _ => element.GetRawText() // Object or Array as string
            };
        }

        /// <summary>
        /// 应用数据转换规则 (Transforms)
        /// </summary>
        public static void ApplyTransforms(List<PluginTransform> transforms, Dictionary<string, string> context)
        {
            if (transforms == null) return;

            foreach (var t in transforms)
            {
                string src = !string.IsNullOrEmpty(t.SourceVar) ? t.SourceVar : t.TargetVar;

                if (!context.ContainsKey(src)) continue; 

                string val = context[src];

                switch (t.Function)
                {
                    case "regex_replace":
                        val = ApplyRegexReplace(val, t, context);
                        break;
                    case "regex_match":
                        val = ApplyRegexMatch(val, t);
                        break;
                    case "map":
                        val = ApplyMap(val, t);
                        break;
                    case "resolve_template":
                        val = ResolveTemplate(val, context);
                        break;
                    case "threshold_switch":
                        val = ApplyThresholdSwitch(val, t);
                        break;
                }

                context[t.TargetVar] = val;
            }
        }

        private static string ApplyRegexReplace(string val, PluginTransform t, Dictionary<string, string> context)
        {
            try
            {
                string replacement = t.To;
                if (replacement.Contains("{{")) 
                {
                    replacement = ResolveTemplate(replacement, context);
                }
                return Regex.Replace(val, t.Pattern, replacement);
            }
            catch { return val; }
        }

        private static string ApplyRegexMatch(string val, PluginTransform t)
        {
            try
            {
                var match = Regex.Match(val, t.Pattern);
                if (match.Success)
                {
                    int groupIndex = 1;
                    if (int.TryParse(t.To, out int idx)) groupIndex = idx;
                    
                    if (groupIndex < match.Groups.Count)
                    {
                        return match.Groups[groupIndex].Value;
                    }
                }
                return "";
            }
            catch { return ""; }
        }

        private static string ApplyMap(string val, PluginTransform t)
        {
            if (t.Map != null && t.Map.ContainsKey(val))
            {
                return t.Map[val];
            }
            return val;
        }

        private static string ApplyThresholdSwitch(string val, PluginTransform t)
        {
            if (double.TryParse(val, out double numVal))
            {
                if (t.ValueMap != null && t.ValueMap.Count > 0)
                {
                    var sorted = new List<(double Th, string Val)>();
                    foreach (var kv in t.ValueMap)
                    {
                        if (double.TryParse(kv.Key, out double k)) sorted.Add((k, kv.Value));
                    }
                    
                    // [Fix] 防止空数组越界访问
                    if (sorted.Count == 0) return "0";
                    
                    sorted.Sort((a, b) => a.Th.CompareTo(b.Th));

                    string result = sorted[0].Val; 
                    
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        if (numVal >= sorted[i].Th) result = sorted[i].Val;
                        else break; 
                    }
                    return result;
                }
                return "0"; 
            }
            return "0";
        }

        /// <summary>
        /// 处理字符串模版替换
        /// </summary>
        public static string ResolveTemplate(string template, Dictionary<string, string> context)
        {
            if (string.IsNullOrEmpty(template)) return "";
            if (!template.Contains("{{")) return template;

            return _templateRegex.Replace(template, m =>
            {
                string content = m.Groups[1].Value.Trim();
                
                // Handle Ternary Syntax: "cond ? trueVal : falseVal"
                int qIdx = content.IndexOf('?');
                int cIdx = content.LastIndexOf(':');
                if (qIdx > 0 && cIdx > qIdx && (qIdx + 1 >= content.Length || content[qIdx + 1] != '?'))
                {
                    string condKey = content.Substring(0, qIdx).Trim();
                    string truePart = content.Substring(qIdx + 1, cIdx - (qIdx + 1)).Trim();
                    string falsePart = content.Substring(cIdx + 1).Trim();

                    bool isTrue = context.TryGetValue(condKey, out string val) && !string.IsNullOrEmpty(val);
                    string target = isTrue ? truePart : falsePart;

                    // Remove quotes if present
                    if (target.Length >= 2 && ((target.StartsWith("\"") && target.EndsWith("\"")) || (target.StartsWith("'") && target.EndsWith("'"))))
                    {
                        return target.Substring(1, target.Length - 2);
                    }

                    // Treat as variable if not quoted
                    return context.TryGetValue(target, out string tVal) ? tVal : "";
                }

                // Handle Fallback Syntax: "var ?? fallback"
                if (content.Contains("??"))
                {
                    var parts = content.Split(new[] { "??" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        string key = part.Trim();

                        // Check for string literal
                        if (key.Length >= 2 && ((key.StartsWith("\"") && key.EndsWith("\"")) || (key.StartsWith("'") && key.EndsWith("'"))))
                        {
                            return key.Substring(1, key.Length - 2);
                        }

                        if (context.TryGetValue(key, out string val) && !string.IsNullOrEmpty(val))
                        {
                            return val;
                        }
                    }
                    return ""; // All fallbacks failed
                }
                
                // Standard Lookup
                if (context.TryGetValue(content, out string value))
                {
                    return value;
                }
                
                return "";
            });
        }
    }
}
