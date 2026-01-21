using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LiteMonitor;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 插件执行引擎 (Refactored)
    /// 负责执行 API 请求、链式步骤、数据处理和结果注入
    /// </summary>
    public class PluginExecutor
    {
        private readonly HttpClient _http;
        private readonly ConcurrentDictionary<string, Task<string>> _inflightRequests = new();
        
        // Key = InstanceID_StepID_ParamsHash
        private class CacheItem
        {
            public string RawResponse { get; set; } 
            public DateTime Timestamp { get; set; }
        }
        private readonly ConcurrentDictionary<string, CacheItem> _stepCache = new();

        public event Action OnSchemaChanged;

        public PluginExecutor()
        {
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(10); 
            _http.DefaultRequestHeaders.Add("User-Agent", "LiteMonitor/1.0");
            
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
                var keysToRemove = _stepCache.Keys.Where(k => k.StartsWith(instanceId)).ToList();
                foreach (var k in keysToRemove) _stepCache.TryRemove(k, out _);
            }
        }

        public async Task<bool> ExecuteInstanceAsync(PluginInstanceConfig inst, PluginTemplate tmpl, System.Threading.CancellationToken token = default)
        {
            if (inst == null || tmpl == null) return false;

            var targets = inst.Targets != null && inst.Targets.Count > 0 ? inst.Targets : new List<Dictionary<string, string>> { new Dictionary<string, string>() };

            var tasks = new List<Task<bool>>();
            for (int i = 0; i < targets.Count; i++)
            {
                if (token.IsCancellationRequested) break;

                var idx = i; 
                // Explicitly specify Task<bool> to avoid ambiguity
                tasks.Add(Task.Run<bool>(async () => 
                {
                    if (token.IsCancellationRequested) return false;

                    if (idx > 0) 
                    {
                        try { await Task.Delay(idx * 50, token); } catch (OperationCanceledException) { return false; }
                    }

                    var mergedInputs = new Dictionary<string, string>(inst.InputValues);
                    foreach (var kv in targets[idx]) mergedInputs[kv.Key] = kv.Value;
                    
                    if (tmpl.Inputs != null)
                    {
                        foreach (var input in tmpl.Inputs)
                        {
                            if (!mergedInputs.ContainsKey(input.Key)) mergedInputs[input.Key] = input.DefaultValue;
                        }
                    }

                    string keySuffix = (inst.Targets != null && inst.Targets.Count > 0) ? $".{idx}" : "";
                    
                    return await ExecuteSingleTargetAsync(inst, tmpl, mergedInputs, keySuffix, token);
                }, token));
            }
            try
            {
                var results = await Task.WhenAll(tasks);
                // If any target succeeded, we consider it a partial success (or maybe we require all? let's be lenient for retry logic)
                // If ALL failed, return false to trigger fast retry.
                return results.Any(x => x);
            }
            catch (OperationCanceledException) { return false; }
        }

        private async Task<bool> ExecuteSingleTargetAsync(PluginInstanceConfig inst, PluginTemplate tmpl, Dictionary<string, string> inputs, string keySuffix, System.Threading.CancellationToken token)
        {
            try
            {
                if (token.IsCancellationRequested) return false;

                string url = PluginProcessor.ResolveTemplate(tmpl.Execution.Url, inputs);
                string body = PluginProcessor.ResolveTemplate(tmpl.Execution.Body ?? "", inputs);

                string resultRaw = "";
                // Handle legacy execution types by mapping them to steps internally or executing directly
                if (tmpl.Execution.Type == "api_json" || tmpl.Execution.Type == "api_text")
                {
                    // Convert legacy single-request to a "step" concept for consistent execution
                    var step = new PluginExecutionStep
                    {
                        Url = url,
                        Body = body,
                        Method = tmpl.Execution.Method,
                        Headers = tmpl.Execution.Headers,
                        ResponseEncoding = null 
                    };

                    // Direct fetch (no caching logic for legacy root level yet, or use step logic?)
                    // For simplicity and backward compatibility, we execute directly here but reuse Fetch helper
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var resolvedHeaders = ResolveHeaders(step.Headers, inputs);
                    resultRaw = await FetchRawAsync(step.Method, url, body, resolvedHeaders, step.ResponseEncoding, token);
                    sw.Stop();
                    inputs["__latency__"] = sw.ElapsedMilliseconds.ToString();
                }

                if (tmpl.Execution.Type == "api_json" || tmpl.Execution.Type == "chain")
                {
                    if (tmpl.Execution.Type == "chain")
                    {
                        if (tmpl.Execution.Steps != null)
                        {
                            foreach (var step in tmpl.Execution.Steps)
                            {
                                if (token.IsCancellationRequested) return false;
                                await ExecuteStepAsync(inst, step, inputs, keySuffix, token);
                            }
                        }
                    }
                    else
                    {
                        // Legacy api_json processing
                        ParseAndExtract(resultRaw, tmpl.Execution.Extract, inputs, "json");
                    }

                    PluginProcessor.ApplyTransforms(tmpl.Execution.Process, inputs);
                    
                    if (tmpl.Outputs != null)
                    {
                        ProcessOutputs(inst, tmpl, inputs, keySuffix);
                    }
                }
                else
                {
                     // api_text
                     string injectKey = inst.Id + keySuffix;
                     InfoService.Instance.InjectValue(injectKey, resultRaw);
                }
                return true;
            }
            catch (OperationCanceledException) 
            {
                return false;
            }
            catch (Exception ex)
            {
                 HandleExecutionError(inst, tmpl, keySuffix, ex);
                 return false;
            }
        }

        private async Task ExecuteStepAsync(PluginInstanceConfig inst, PluginExecutionStep step, Dictionary<string, string> context, string keySuffix, System.Threading.CancellationToken token)
        {
            try {
                // 0. Check Skip Condition
                if (!string.IsNullOrEmpty(step.SkipIfSet))
                {
                    if (context.TryGetValue(step.SkipIfSet, out var val) && !string.IsNullOrEmpty(val))
                    {
                        return; // Skip this step
                    }
                }

                string url = PluginProcessor.ResolveTemplate(step.Url, context);
                string body = PluginProcessor.ResolveTemplate(step.Body ?? "", context);

                string contentHash = (url + "|" + body).GetHashCode().ToString("X"); 
                string cacheKey = $"{inst.Id}{keySuffix}_{step.Id}_{contentHash}";

                bool hit = false;
                string resultRaw = "";

                if (step.CacheMinutes > 0)
                {
                    if (_stepCache.TryGetValue(cacheKey, out var cached))
                    {
                        if (DateTime.Now - cached.Timestamp < TimeSpan.FromMinutes(step.CacheMinutes))
                        {
                            resultRaw = cached.RawResponse;
                            hit = true;
                        }
                        else
                        {
                            _stepCache.TryRemove(cacheKey, out _); 
                        }
                    }
                }

                if (!hit)
                {
                    string proxy = step.Proxy;
                    if (!string.IsNullOrEmpty(proxy) && proxy.Contains("{{"))
                    {
                         proxy = PluginProcessor.ResolveTemplate(proxy, context);
                    }

                    // Request Coalescing
                    var task = _inflightRequests.GetOrAdd(cacheKey, _ => FetchRawAsync(step.Method, url, body, step.Headers, step.ResponseEncoding, CancellationToken.None, proxy));
                    
                    try 
                    {
                        // Wait with cancellation support
                        var tcs = new TaskCompletionSource<string>();
                        using (token.Register(() => tcs.TrySetCanceled()))
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            var finishedTask = await Task.WhenAny(task, tcs.Task);
                            if (finishedTask == tcs.Task) throw new OperationCanceledException(token);
                            resultRaw = await task;
                            sw.Stop();
                            context["__latency__"] = sw.ElapsedMilliseconds.ToString();
                        }

                        // Update Cache
                        if (step.CacheMinutes != 0)
                        {
                            _stepCache[cacheKey] = new CacheItem
                            {
                                RawResponse = resultRaw,
                                Timestamp = DateTime.Now
                            };
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        _inflightRequests.TryRemove(cacheKey, out _);
                        throw;
                    }
                    finally
                    {
                         _inflightRequests.TryRemove(cacheKey, out _);
                    }
                }

                // Parse
                ParseAndExtract(resultRaw, step.Extract, context, step.ResponseFormat);
                
                // Process
                PluginProcessor.ApplyTransforms(step.Process, context);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Step {step.Id} Error: {ex.Message}");
                throw; 
            }
        }

        // Proxy Client Cache: Key = "host:port" or "http://host:port"
        private readonly ConcurrentDictionary<string, HttpClient> _proxyClients = new();

        private HttpClient GetClient(string proxy)
        {
            if (string.IsNullOrEmpty(proxy)) return _http;

            return _proxyClients.GetOrAdd(proxy, p => 
            {
                var handler = new HttpClientHandler();
                handler.Proxy = new System.Net.WebProxy(p);
                handler.UseProxy = true;
                
                var client = new HttpClient(handler);
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.Add("User-Agent", "LiteMonitor/1.0");
                return client;
            });
        }

        private async Task<string> FetchRawAsync(string methodStr, string url, string body, Dictionary<string, string> headers, string encoding, System.Threading.CancellationToken token, string proxy = null)
        {
            var method = (methodStr?.ToUpper() == "POST") ? HttpMethod.Post : HttpMethod.Get;
            var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Post && !string.IsNullOrEmpty(body))
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            if (headers != null)
            {
                foreach (var h in headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            var client = GetClient(proxy);
            var response = await client.SendAsync(request, token);
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(token);
            
            if (encoding?.ToLower() == "gbk")
            {
                return Encoding.GetEncoding("GBK").GetString(bytes);
            }
            return Encoding.UTF8.GetString(bytes);
        }

        private void ParseAndExtract(string resultRaw, Dictionary<string, string> extractRules, Dictionary<string, string> context, string format = "json")
        {
            if (extractRules == null || extractRules.Count == 0) return;

            string json = resultRaw.Trim();
            string fmt = format?.ToLower() ?? "json";
            
            if (fmt == "jsonp")
            {
                if (json.StartsWith("(") && json.EndsWith(")"))
                {
                    json = json.Substring(1, json.Length - 2);
                }
            }
            
            if (fmt == "json" || fmt == "jsonp")
            {
                if (json.StartsWith("{") || json.StartsWith("["))
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    foreach (var kv in extractRules)
                    {
                        // Support dynamic paths like "rates.{{to}}"
                        string resolvedPath = PluginProcessor.ResolveTemplate(kv.Value, context);
                        context[kv.Key] = PluginProcessor.ExtractJsonValue(root, resolvedPath);
                    }
                }
            }
            else if (fmt == "text")
            {
                foreach (var kv in extractRules)
                {
                    if (kv.Value == "$")
                    {
                        context[kv.Key] = resultRaw;
                    }
                }
            }
        }

        private void ProcessOutputs(PluginInstanceConfig inst, PluginTemplate tmpl, Dictionary<string, string> inputs, string keySuffix)
        {
            bool schemaChanged = false;
            var settings = Settings.Load(); 

            lock (settings) 
            {
                foreach (var output in tmpl.Outputs)
                {
                    string val = PluginProcessor.ResolveTemplate(output.Format, inputs);
                    string injectKey = inst.Id + keySuffix + "." + output.Key;
                    
                    if (string.IsNullOrEmpty(val)) val = PluginConstants.STATUS_EMPTY;
                    InfoService.Instance.InjectValue(injectKey, val);

                    if (!string.IsNullOrEmpty(output.Color))
                    {
                        string colorState = PluginProcessor.ResolveTemplate(output.Color, inputs);
                        InfoService.Instance.InjectValue(injectKey + ".Color", colorState);
                    }

                    // Inject Unit
                    if (!string.IsNullOrEmpty(output.Unit))
                    {
                        string resolvedUnit = PluginProcessor.ResolveTemplate(output.Unit, inputs);
                        InfoService.Instance.InjectValue(injectKey + ".Unit", resolvedUnit);
                    }

                    // Dynamic Label Update Logic
                    string itemKey = PluginConstants.DASH_PREFIX + injectKey;
                    
                    string labelPattern = !string.IsNullOrEmpty(output.Label) ? output.Label : (tmpl.Meta.Name + " " + output.Key);
                    
                    string newName = PluginProcessor.ResolveTemplate(labelPattern, inputs);
                    string newShort = PluginProcessor.ResolveTemplate(output.ShortLabel ?? "", inputs);
                    
                    // Apply default values for missing inputs in labels
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

                    // [Refactor] Decouple from Settings.MonitorItems
                    // Instead of modifying Settings directly (SRP Violation & Race Condition),
                    // we inject the dynamic labels into InfoService as properties.
                    // The UI (MetricItem) will read these properties at runtime.
                    InfoService.Instance.InjectValue("PROP.Label." + itemKey, newName);
                    InfoService.Instance.InjectValue("PROP.ShortLabel." + itemKey, newShort);
                    
                    // Notify schema change only if this is the first time we see this item 
                    // or if significant change happens (Optional, maybe not needed if UI binds to InfoService)
                    // For now, we assume UI refreshes periodically or on specific events.
                    // If we need to trigger a full layout rebuild, we might need an event, 
                    // but standard label text updates are handled by invalidating the control.
                }
            }
            
            // Removed direct Settings modification logic
            // OnSchemaChanged?.Invoke(); // Only needed if structure changes (Add/Remove), not for label updates
        }

        private void HandleExecutionError(PluginInstanceConfig inst, PluginTemplate tmpl, string keySuffix, Exception ex)
        {
            if (tmpl.Outputs != null)
            {
                foreach(var o in tmpl.Outputs) 
                {
                    string injectKey = inst.Id + keySuffix + "." + o.Key;
                    InfoService.Instance.InjectValue(injectKey, PluginConstants.STATUS_ERROR);
                }
            }
            System.Diagnostics.Debug.WriteLine($"Plugin exec error ({inst.Id}): {ex.Message}");
        }

        private Dictionary<string, string> ResolveHeaders(Dictionary<string, string> headers, Dictionary<string, string> context)
        {
            if (headers == null || headers.Count == 0) return null;
            var resolved = new Dictionary<string, string>();
            foreach (var kv in headers)
            {
                resolved[kv.Key] = PluginProcessor.ResolveTemplate(kv.Value, context);
            }
            return resolved;
        }
    }
}
