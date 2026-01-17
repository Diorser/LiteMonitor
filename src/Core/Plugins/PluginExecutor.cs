using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LiteMonitor;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.src.Core.Plugins
{
    /// <summary>
    /// 插件执行引擎
    /// 负责执行 API 请求、链式步骤、数据处理和结果注入
    /// </summary>
    public class PluginExecutor
    {
        private readonly HttpClient _http;
        
        // 步骤缓存: Key = InstanceID_StepID, Value = CacheItem
        private class CacheItem
        {
            public Dictionary<string, string> Data { get; set; }
            public DateTime Timestamp { get; set; }
        }
        private readonly Dictionary<string, CacheItem> _stepCache = new();

        // 当插件动态修改了 UI 配置（如 Label）时触发
        public event Action OnSchemaChanged;

        public PluginExecutor()
        {
            // 初始化 HttpClient
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(10);
            _http.DefaultRequestHeaders.Add("User-Agent", "LiteMonitor/1.0");
            
            // 注册 GBK 编码支持
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        }

        public void ClearCache(string instanceId = null)
        {
            if (string.IsNullOrEmpty(instanceId))
            {
                _stepCache.Clear();
            }
            else
            {
                // 清除特定实例的所有缓存 (Key 格式: InstId + suffix + _ + StepId)
                // 由于 suffix 可能是 .0, .1 等，所以匹配 InstId 开头即可
                var keysToRemove = _stepCache.Keys.Where(k => k.StartsWith(instanceId)).ToList();
                foreach (var k in keysToRemove) _stepCache.Remove(k);
            }
        }

        /// <summary>
        /// 执行单个插件实例
        /// </summary>
        public async Task ExecuteInstanceAsync(PluginInstanceConfig inst, PluginTemplate tmpl)
        {
            // 处理多目标 (Targets)
            var targets = inst.Targets != null && inst.Targets.Count > 0 ? inst.Targets : new List<Dictionary<string, string>> { new Dictionary<string, string>() };

            for (int i = 0; i < targets.Count; i++)
            {
                // 多目标间增加微小延迟，避免触发 API 速率限制
                if (i > 0) await Task.Delay(500);

                // 1. 合并输入参数 (默认值 + 实例配置 + Target配置)
                var mergedInputs = new Dictionary<string, string>(inst.InputValues);
                foreach (var kv in targets[i])
                {
                    mergedInputs[kv.Key] = kv.Value;
                }
                
                // 补全默认值
                if (tmpl.Inputs != null)
                {
                    foreach (var input in tmpl.Inputs)
                    {
                        if (!mergedInputs.ContainsKey(input.Key))
                        {
                            mergedInputs[input.Key] = input.DefaultValue;
                        }
                    }
                }

                // 生成 Key 后缀 (用于区分多目标)
                string keySuffix = (inst.Targets != null && inst.Targets.Count > 0) ? $".{i}" : "";
                
                await ExecuteSingleTargetAsync(inst, tmpl, mergedInputs, keySuffix);
            }
        }

        private async Task ExecuteSingleTargetAsync(PluginInstanceConfig inst, PluginTemplate tmpl, Dictionary<string, string> inputs, string keySuffix)
        {
            try
            {
                // 1. URL & Body 变量替换
                string url = PluginProcessor.ResolveTemplate(tmpl.Execution.Url, inputs);
                string body = tmpl.Execution.Body ?? "";
                body = PluginProcessor.ResolveTemplate(body, inputs);

                // 2. 执行请求 (api_json/api_text 模式)
                string resultRaw = "";
                if (tmpl.Execution.Type == "api_json" || tmpl.Execution.Type == "api_text")
                {
                    var method = (tmpl.Execution.Method?.ToUpper() == "POST") ? HttpMethod.Post : HttpMethod.Get;
                    var request = new HttpRequestMessage(method, url);
                    
                    if (method == HttpMethod.Post && !string.IsNullOrEmpty(body))
                    {
                        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    }

                    if (tmpl.Execution.Headers != null)
                    {
                        foreach (var h in tmpl.Execution.Headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    }
                    
                    var response = await _http.SendAsync(request);
                    resultRaw = await response.Content.ReadAsStringAsync();
                }

                // 3. 数据解析与处理
                if (tmpl.Execution.Type == "api_json" || tmpl.Execution.Type == "chain")
                {
                    if (tmpl.Execution.Type == "chain")
                    {
                        // 链式执行: 依次执行每个步骤
                        if (tmpl.Execution.Steps != null)
                        {
                            foreach (var step in tmpl.Execution.Steps)
                            {
                                await ExecuteStepAsync(inst, step, inputs, keySuffix);
                            }
                        }
                    }
                    else
                    {
                        // 传统 api_json 解析
                        using var doc = JsonDocument.Parse(resultRaw);
                        var root = doc.RootElement;
                        
                        // 检查错误字段
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var errProp) && errProp.GetBoolean())
                        {
                             // TODO: 错误处理逻辑
                        }

                        // 提取变量
                        if (tmpl.Execution.Extract != null)
                        {
                            foreach (var v in tmpl.Execution.Extract)
                            {
                                inputs[v.Key] = PluginProcessor.ExtractJsonValue(root, v.Value);
                            }
                        }
                    }

                    // 全局数据转换 (Global Transforms)
                    PluginProcessor.ApplyTransforms(tmpl.Execution.Process, inputs);
                    
                    // 4. 生成最终输出并注入
                    if (tmpl.Outputs != null)
                    {
                        ProcessOutputs(inst, tmpl, inputs, keySuffix);
                    }
                }
                else
                {
                     // api_text 模式直接输出原始内容
                     string injectKey = inst.Id + keySuffix;
                     InfoService.Instance.InjectValue(injectKey, resultRaw);
                }
            }
            catch (Exception ex)
            {
                 HandleExecutionError(inst, tmpl, keySuffix, ex);
            }
        }

        private async Task ExecuteStepAsync(PluginInstanceConfig inst, PluginExecutionStep step, Dictionary<string, string> context, string keySuffix)
        {
            try {
                // 0. 检查缓存
                // [Fix] 缓存 Key 必须包含 keySuffix，否则多目标 (Targets) 之间会共享缓存导致数据覆盖
                string cacheKey = inst.Id + keySuffix + "_" + step.Id;
                if (step.CacheMinutes != 0)
                {
                    if (_stepCache.ContainsKey(cacheKey))
                    {
                        var cached = _stepCache[cacheKey];
                        // 检查过期时间
                        if (DateTime.Now - cached.Timestamp < TimeSpan.FromMinutes(step.CacheMinutes))
                        {
                            foreach (var kv in cached.Data) context[kv.Key] = kv.Value;
                            System.Diagnostics.Debug.WriteLine($"Step {step.Id} used cache.");
                            return;
                        }
                        else
                        {
                            // 已过期，移除
                            _stepCache.Remove(cacheKey);
                        }
                    }
                }

                // 1. 准备 URL/Body
                string url = PluginProcessor.ResolveTemplate(step.Url, context);
                string body = PluginProcessor.ResolveTemplate(step.Body ?? "", context);

                // 2. 发起请求
                var method = (step.Method?.ToUpper() == "POST") ? HttpMethod.Post : HttpMethod.Get;
                var request = new HttpRequestMessage(method, url);
                if (method == HttpMethod.Post && !string.IsNullOrEmpty(body))
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }
                if (step.Headers != null)
                {
                    foreach (var h in step.Headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }

                var response = await _http.SendAsync(request);
                byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                
                // 处理编码 (GBK支持)
                string resultRaw;
                if (step.ResponseEncoding?.ToLower() == "gbk")
                {
                    resultRaw = Encoding.GetEncoding("GBK").GetString(bytes);
                }
                else
                {
                    resultRaw = Encoding.UTF8.GetString(bytes);
                }

                // 3. 解析变量
                if (step.Extract != null && step.Extract.Count > 0)
                {
                    string json = resultRaw.Trim();
                    string format = step.ResponseFormat?.ToLower() ?? "json";
                    
                    // JSONP 处理
                    if (format == "jsonp")
                    {
                        if (json.StartsWith("(") && json.EndsWith(")"))
                        {
                            json = json.Substring(1, json.Length - 2);
                        }
                    }
                    
                    if (format == "json" || format == "jsonp")
                    {
                        if (json.StartsWith("{") || json.StartsWith("["))
                        {
                            using var doc = JsonDocument.Parse(json);
                            var root = doc.RootElement;
                            foreach (var kv in step.Extract)
                            {
                                context[kv.Key] = PluginProcessor.ExtractJsonValue(root, kv.Value);
                            }
                        }
                    }
                }

                // 4. 步骤级数据处理
                PluginProcessor.ApplyTransforms(step.Process, context);

                // 5. 写入缓存
                if (step.CacheMinutes != 0)
                {
                    var outputs = new Dictionary<string, string>();
                    
                    // 仅缓存此步骤产生的变量
                    if (step.Extract != null)
                        foreach(var k in step.Extract.Keys) 
                            if (context.ContainsKey(k)) outputs[k] = context[k];
                    
                    if (step.Process != null)
                        foreach(var t in step.Process)
                            if (context.ContainsKey(t.TargetVar)) outputs[t.TargetVar] = context[t.TargetVar];

                    _stepCache[cacheKey] = new CacheItem
                    {
                        Data = outputs,
                        Timestamp = DateTime.Now
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Step {step.Id} Error: {ex.Message}");
                // 步骤出错不抛出，允许后续步骤尝试运行 (或者根据需求决定是否中断)
                throw; // 中断链式执行
            }
        }

        private void ProcessOutputs(PluginInstanceConfig inst, PluginTemplate tmpl, Dictionary<string, string> inputs, string keySuffix)
        {
            bool schemaChanged = false;
            var settings = Settings.Load();

            foreach (var output in tmpl.Outputs)
            {
                // 1. 注入数值
                string val = PluginProcessor.ResolveTemplate(output.Format, inputs);
                string injectKey = inst.Id + keySuffix + "." + output.Key;
                
                if (string.IsNullOrEmpty(val)) val = "[Empty]";
                InfoService.Instance.InjectValue(injectKey, val);

                // 2. 动态更新 Label (UI)
                string itemKey = "DASH." + injectKey;
                var item = settings.MonitorItems.FirstOrDefault(x => x.Key == itemKey);
                if (item != null)
                {
                    string labelPattern = output.Label;
                    if (string.IsNullOrEmpty(labelPattern)) labelPattern = (tmpl.Meta.Name) + " " + output.Key;
                    
                    // 使用当前上下文解析 Label
                    string newName = PluginProcessor.ResolveTemplate(labelPattern, inputs);
                    string newShort = PluginProcessor.ResolveTemplate(output.ShortLabel ?? "", inputs);
                    
                    // 补全默认参数
                    if (tmpl.Inputs != null)
                    {
                        foreach (var input in tmpl.Inputs)
                        {
                            if (!inputs.ContainsKey(input.Key))
                            {
                                newName = newName.Replace("{{" + input.Key + "}}", input.DefaultValue);
                                newShort = newShort.Replace("{{" + input.Key + "}}", input.DefaultValue);
                            }
                        }
                    }

                    if (item.UserLabel != newName)
                    {
                        item.UserLabel = newName;
                        schemaChanged = true;
                    }
                    if (item.TaskbarLabel != newShort)
                    {
                        item.TaskbarLabel = newShort;
                        schemaChanged = true;
                    }
                }
            }

            if (schemaChanged)
            {
                lock(settings) { settings.Save(); }
                OnSchemaChanged?.Invoke();
            }
        }

        private void HandleExecutionError(PluginInstanceConfig inst, PluginTemplate tmpl, string keySuffix, Exception ex)
        {
            if (tmpl.Outputs != null)
            {
                foreach(var o in tmpl.Outputs) 
                {
                    string injectKey = inst.Id + keySuffix + "." + o.Key;
                    InfoService.Instance.InjectValue(injectKey, "Err");
                }
            }
            System.Diagnostics.Debug.WriteLine($"Plugin exec error ({inst.Id}): {ex.Message} \nStack: {ex.StackTrace}");
        }
    }
}
