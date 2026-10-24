using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LiteMonitor;
using LiteMonitor.src.SystemServices.InfoService;
using LiteMonitor.src.Plugins.Native;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 插件执行引擎 (Refactored)
    /// 负责执行 API 请求、链式步骤、数据处理和结果注入
    /// </summary>
    public class PluginExecutor : IDisposable
    {
        private HttpClient _http;
        private readonly ConcurrentDictionary<string, Task<string>> _inflightRequests = new();
        private readonly object _httpLock = new object();
        
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
            InitializeDefaultClient();
            
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        }

        private void InitializeDefaultClient()
        {
            lock (_httpLock)
            {
                _http = new HttpClient(new SocketsHttpHandler
                {
                    // 关键修复：允许 HttpClient 自动识别并解压 GZip 或 Deflate 数据
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                    
                    // 使用系统默认证书校验，拒绝不可信或域名不匹配的 HTTPS 服务。
                    // [Fix] Reduce pooled connection lifetime to ensure timely DNS updates and socket release
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    // [Fix] Limit connections per server to prevent ephemeral port exhaustion
                    MaxConnectionsPerServer = 20
                });
                _http.Timeout = TimeSpan.FromSeconds(10); 
                _http.DefaultRequestHeaders.Add("User-Agent", "LiteMonitor/1.0");
            }
        }

        private DateTime _lastNetworkReset = DateTime.MinValue;

        public void ResetNetworkClients()
        {
            lock (_httpLock)
            {
                // [Fix] Throttle resets to prevent resource exhaustion (e.g. during system sleep/wake cycle)
                if ((DateTime.Now - _lastNetworkReset).TotalSeconds < 60) return;
                _lastNetworkReset = DateTime.Now;

                // [Fix] Properly dispose old clients to release sockets
                var oldHttp = _http;
                var oldProxies = new List<HttpClient>(_proxyClients.Values);

                InitializeDefaultClient();
                
                // [Optimized] Clear proxy clients dictionary but do NOT dispose them.
                // Other threads might be using them. Removing them from the dictionary ensures
                // new requests get fresh clients, while old requests can finish (or fail safely).
                _proxyClients.Clear();
                
                // [Fix] Also reset native resolvers that might hold network state or cache
                CityCodeResolver.ResetClient();
                CryptoNative.ResetClient();

                // [Fix] Delay dispose old clients to allow inflight requests to complete (or timeout)
                // This prevents ObjectDisposedException while ensuring Sockets are eventually released
                Task.Run(async () => 
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    try 
                    {
                        oldHttp?.Dispose();
                        foreach(var c in oldProxies) c.Dispose();
                    }
                    catch {}
                });
            }
            
        }

        public void Dispose()
        {
            _http?.Dispose();
            foreach (var client in _proxyClients.Values)
            {
                client.Dispose();
            }
            _proxyClients.Clear();
            _stepCache.Clear();
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
            catch (OperationCanceledException opEx) 
            {
                if (token.IsCancellationRequested) return false;
                
                // If token is NOT cancelled, it's a timeout. Treat as error to trigger reset logic.
                HandleExecutionError(inst, tmpl, inputs, keySuffix, opEx);
                return false;
            }
            catch (Exception ex)
            {
                 HandleExecutionError(inst, tmpl, inputs, keySuffix, ex);
                 return false;
            }
        }

        private async Task ExecuteStepAsync(PluginInstanceConfig inst, PluginExecutionStep step, Dictionary<string, string> context, string keySuffix, System.Threading.CancellationToken token)
        {
            if (!step.ContinueOnError)
            {
                await ExecuteStepCoreAsync(inst, step, context, keySuffix, token);
                return;
            }

            // 可回退的步骤只在成功后合并变量，避免部分结果或错误标记阻止备用步骤。
            var stepContext = new Dictionary<string, string>(context);
            try
            {
                token.ThrowIfCancellationRequested();
                await ExecuteStepCoreAsync(inst, step, stepContext, keySuffix, token);
                token.ThrowIfCancellationRequested();
                foreach (var kv in stepContext) context[kv.Key] = kv.Value;
            }
            catch (Exception ex)
            {
                // 请求超时也允许回退，但用户停用插件或退出时的取消必须继续传播。
                token.ThrowIfCancellationRequested();
                Debug.WriteLine($"Step {step.Id} fallback: {ex.Message}");
            }
        }

        private async Task ExecuteStepCoreAsync(PluginInstanceConfig inst, PluginExecutionStep step, Dictionary<string, string> context, string keySuffix, System.Threading.CancellationToken token)
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

                // [Fix] Check for invalid parameters in URL (e.g. "?") to prevent downstream errors
                if (url.Contains(PluginConstants.STATUS_UNKNOWN) || url.Contains(PluginConstants.STATUS_ERROR))
                {
                     throw new Exception($"Step {step.Id} blocked: URL contains invalid parameter. URL: {url}");
                }

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
                    // [Feature] ICMP Ping: 支持 icmp:// 前缀，或直接填写 IP/域名（无 http(s):// 时按 Ping 处理）
                    if (TryGetPingHost(url, out string pingHost))
                    {
                        var ms = await PingHostAsync(pingHost, token);
                        resultRaw = ms.ToString();
                        context["__latency__"] = ms.ToString();
                    }
                    // [Feature] Support Native Native Handlers (Weather, Crypto)
                    else if (url.StartsWith("native://"))
                    {
                        resultRaw = await ExecuteNativeAsync(url, context);
                    }
                    else
                    {
                        string proxy = step.Proxy;
                        if (!string.IsNullOrEmpty(proxy) && proxy.Contains("{{"))
                        {
                             proxy = PluginProcessor.ResolveTemplate(proxy, context);
                        }
                        // 1. 先解析 Header 中的占位符 (例如 Authorization: Bearer {{token}})
                        var resolvedHeaders = ResolveHeaders(step.Headers, context);
                        
                        // Request Coalescing
                        var task = _inflightRequests.GetOrAdd(cacheKey, _ => FetchRawAsync(step.Method, url, body, resolvedHeaders, step.ResponseEncoding, CancellationToken.None, proxy));     
                        
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
                }

                // Parse
                ParseAndExtract(resultRaw, step.Extract, context, step.ResponseFormat);
                
                // Process
                PluginProcessor.ApplyTransforms(step.Process, context);

                // [Fix] Validate extracted data before caching
                // If critical data is missing (Unknown/Error), THROW exception to prevent downstream steps from executing with bad data
                if (step.Extract != null)
                {
                    foreach (var kv in step.Extract)
                    {
                        if (context.TryGetValue(kv.Key, out string val))
                        {
                            if (val == PluginConstants.STATUS_UNKNOWN || val == PluginConstants.STATUS_ERROR)
                            {
                                // [Fix] Fail fast if critical extraction failed
                                throw new Exception($"Step {step.Id} failed: Extracted invalid value for '{kv.Key}'. Value: '{val}'");
                            }
                        }
                    }
                }

                // [Fix] Update Cache AFTER successful parsing and processing
                // This ensures we don't cache invalid data (e.g. HTML error pages that passed HTTP check but failed JSON parse)
                if (!hit && step.CacheMinutes != 0)
                {
                    // [Optimization] Prevent large blobs from polluting cache (limit 500KB)
                    if (resultRaw != null && resultRaw.Length > 500 * 1024)
                    {
                        System.Diagnostics.Debug.WriteLine($"Plugin response too large to cache ({resultRaw.Length} bytes). Key: {cacheKey}");
                    }
                    else
                    {
                        // [Optimization] Prevent unbounded growth with timestamp-based eviction
                        if (_stepCache.Count > 100)
                        {
                            // Evict oldest items first
                            var keysToRemove = _stepCache
                                .OrderBy(kv => kv.Value.Timestamp)
                                .Take(20)
                                .Select(kv => kv.Key)
                                .ToList();
                                
                            foreach(var k in keysToRemove) _stepCache.TryRemove(k, out _);
                        }

                        _stepCache[cacheKey] = new CacheItem
                        {
                            RawResponse = resultRaw,
                            Timestamp = DateTime.Now
                        };
                    }
                }
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

        // =====================================================================
        // ICMP Ping
        // 支持 "icmp://host"、"ping://host" 或裸主机（IP/域名），不支持 HTTP 代理
        // =====================================================================
        private static bool TryGetPingHost(string value, out string host)
        {
            host = "";
            if (string.IsNullOrWhiteSpace(value)) return false;

            string v = value.Trim();

            if (v.StartsWith("icmp://", StringComparison.OrdinalIgnoreCase) ||
                v.StartsWith("ping://", StringComparison.OrdinalIgnoreCase))
            {
                v = v.Substring(7);
            }
            else if (v.Contains("://") || v.Contains('/') || v.Contains(' ') || v.Contains('?'))
            {
                return false; // 完整 HTTP(S) URL 或非法内容，走普通请求
            }

            if (v.Length == 0) return false;

            // 借用 Uri 解析 host:port / [IPv6]:port
            if (!Uri.TryCreate("http://" + v, UriKind.Absolute, out var uri)) return false;
            if (string.IsNullOrEmpty(uri.Host)) return false;

            host = uri.Host;
            return true;
        }

        private async Task<long> PingHostAsync(string host, System.Threading.CancellationToken token)
        {
            const int attempts = 3;
            const int timeoutMs = 2000;

            long best = -1;
            Exception? lastError = null;

            using var ping = new Ping();
            for (int i = 0; i < attempts; i++)
            {
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);

                try
                {
                    var sendTask = ping.SendPingAsync(host, timeoutMs);
                    var completed = await Task.WhenAny(sendTask, Task.Delay(System.Threading.Timeout.Infinite, token));
                    if (completed != sendTask) throw new OperationCanceledException(token);

                    var reply = await sendTask;
                    if (reply.Status == IPStatus.Success)
                    {
                        if (best < 0 || reply.RoundtripTime < best) best = reply.RoundtripTime;
                    }
                    else
                    {
                        lastError = new Exception($"ICMP ping {host} failed: {reply.Status}");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            if (best < 0)
                throw lastError ?? new Exception($"ICMP ping {host} failed");

            return best;
        }

        private HttpClient GetClient(string proxy)
        {
            if (string.IsNullOrEmpty(proxy))
            {
                lock (_httpLock) return _http;
            }

            return _proxyClients.GetOrAdd(proxy.Trim(), p => 
            {
                var handler = new SocketsHttpHandler();
                handler.Proxy = new System.Net.WebProxy(p);
                handler.UseProxy = true;
                // 补上这一行，确保代理请求也能解压缩
                handler.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;
                // [Fix] Limit pooled connections for proxies too
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2);
                handler.MaxConnectionsPerServer = 20;
                
                var client = new HttpClient(handler);
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.Add("User-Agent", "LiteMonitor/1.0");
                return client;
            });
        }

        private async Task<string> ExecuteNativeAsync(string url, Dictionary<string, string> context)
        {
            // url: native://citycode?province=...
            var uri = new Uri(url);
            
            // Simple Query Parse
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(uri.Query))
            {
                var q = uri.Query.TrimStart('?');
                foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split(new[] { '=' }, 2);
                    if (kv.Length == 2) 
                    {
                        args[kv[0]] = System.Net.WebUtility.UrlDecode(kv[1]);
                    }
                }
            }

            if (uri.Host.Equals("citycode", StringComparison.OrdinalIgnoreCase))
            {
                string p = args.ContainsKey("province") ? args["province"] : "";
                string c = args.ContainsKey("city") ? args["city"] : "";
                string d = args.ContainsKey("district") ? args["district"] : "";
                return await CityCodeResolver.ResolveAsync(p, c, d);
            }
            else if (uri.Host.Equals("crypto", StringComparison.OrdinalIgnoreCase))
            {
                string s = args.ContainsKey("symbol") ? args["symbol"] : "BTC";
                string fb = args.ContainsKey("fallback") ? args["fallback"] : "";
                return await CryptoNative.FetchAsync(s, fb);
            }
            else if (uri.Host.Equals("newyear", StringComparison.OrdinalIgnoreCase))
            {
                return NewYearNative.GetCountdownJson(DateTime.Today);
            }
            else if (uri.Host.Equals("multiping", StringComparison.OrdinalIgnoreCase))
            {
                string host = GetArg(args, "host");
                string protocol = GetArg(args, "protocol", "icmp");
                int port = ParseInt(GetArg(args, "port"), 443);
                int interval = ParseInt(GetArg(args, "interval"), 5);
                string mode = GetArg(args, "mode", "auto");
                string proxy = GetArg(args, "proxy");
                int timeout = ParseInt(GetArg(args, "timeout"), 2000);
                int rounds = ParseInt(GetArg(args, "rounds"), 3);

                return await MultiPingNative.RunAsync(host, protocol, port, interval, mode, proxy, timeout, rounds);
            }

            throw new Exception($"Unknown native host: {uri.Host}");
        }

        private static string GetArg(Dictionary<string, string> args, string key, string defaultValue = "")
        {
            return args.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : defaultValue;
        }

        private static int ParseInt(string value, int defaultValue)
        {
            return int.TryParse(value, out int v) ? v : defaultValue;
        }

        private async Task<string> FetchRawAsync(string methodStr, string url, string body, Dictionary<string, string> headers, string encoding, System.Threading.CancellationToken token, string proxy = null)
        {
            HttpMethod method = HttpMethod.Get;
            if (methodStr?.ToUpper() == "POST") method = HttpMethod.Post;
            else if (methodStr?.ToUpper() == "HEAD") method = HttpMethod.Head;
            
            // Log($"[Start] {method} {url} Proxy:{proxy ?? "None"}");

            using var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Post && !string.IsNullOrEmpty(body))
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            if (headers != null)
            {
                foreach (var h in headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            var client = GetClient(proxy);
            try
            {
                using var response = await client.SendAsync(request, token);
                // Log($"[End] {url} Status:{response.StatusCode}");

                // [Fix] Enforce success status code to prevent caching error pages (e.g. 404/500 HTML)
                response.EnsureSuccessStatusCode();
                
                // [Optimization] HEAD request optimization
                if (method == HttpMethod.Head) return "";

                byte[] bytes = await response.Content.ReadAsByteArrayAsync(token);
                
                if (encoding?.ToLower() == "gbk")
                {
                    return Encoding.GetEncoding("GBK").GetString(bytes);
                }
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Error] {url} {ex.Message}");
                // Log($"[Error] {url} {ex.Message}");
                throw;
            }
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
                // 必须实际解析响应，不能将 HTML 或空正文误判为成功并写入步骤缓存。
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                foreach (var kv in extractRules)
                {
                    // 支持 rates.{{to}} 这样的动态字段路径。
                    string resolvedPath = PluginProcessor.ResolveTemplate(kv.Value, context);
                    context[kv.Key] = PluginProcessor.ExtractJsonValue(root, resolvedPath);
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
            // [Optimization] Use Unified KeyCache from PluginInstanceConfig
            // Cache Key format: "{keySuffix}.{outputKey}" (e.g. ".0.main" or ".main")
            
            foreach (var output in tmpl.Outputs)
            {
                string cacheKey = keySuffix + "." + output.Key;
                
                var keys = inst.KeyCache.GetOrAdd(cacheKey, _ => 
                {
                    var k = new PluginOutputKeys();
                    
                    // 1. InjectKey: "Id.0.Key"
                    string rawInjectKey = inst.Id + keySuffix + "." + output.Key;
                    k.InjectKey = UIUtils.Intern(rawInjectKey);

                    // 2. Attribute Keys
                    k.InjectColorKey = UIUtils.Intern(rawInjectKey + ".Color");
                    k.InjectUnitKey = UIUtils.Intern(rawInjectKey + ".Unit");

                    // 3. Prop Keys (DASH prefix)
                    string itemKey = PluginConstants.DASH_PREFIX + rawInjectKey;
                    k.PropLabelKey = UIUtils.Intern("PROP.Label." + itemKey);
                    k.PropShortKey = UIUtils.Intern("PROP.ShortLabel." + itemKey);
                    
                    return k;
                });

                // 1. Value Injection
                string val = PluginProcessor.ResolveTemplate(output.Format, inputs);
                if (string.IsNullOrEmpty(val)) val = PluginConstants.STATUS_EMPTY;
                
                // [Optimization] Check existing value to avoid string churn
                string currentVal = InfoService.Instance.GetValue(keys.InjectKey);
                if (val != currentVal)
                {
                    InfoService.Instance.InjectValue(keys.InjectKey, val);
                }

                // 2. Color Injection
                if (!string.IsNullOrEmpty(output.Color))
                {
                    string colorState = PluginProcessor.ResolveTemplate(output.Color, inputs);
                    string currentColor = InfoService.Instance.GetValue(keys.InjectColorKey);
                    
                    if (colorState != currentColor)
                    {
                        InfoService.Instance.InjectValue(keys.InjectColorKey, colorState);
                    }
                }

                // 3. Unit Injection
                if (!string.IsNullOrEmpty(output.Unit))
                {
                    string resolvedUnit = PluginProcessor.ResolveTemplate(output.Unit, inputs);
                    string currentUnit = InfoService.Instance.GetValue(keys.InjectUnitKey);

                    if (resolvedUnit != currentUnit)
                    {
                        InfoService.Instance.InjectValue(keys.InjectUnitKey, resolvedUnit);
                    }
                }

                // 4. Dynamic Label Logic
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

                // [Optimization] Use cached Prop keys
                if (InfoService.Instance.GetValue(keys.PropLabelKey) != newName)
                {
                    InfoService.Instance.InjectValue(keys.PropLabelKey, newName);
                }

                if (InfoService.Instance.GetValue(keys.PropShortKey) != newShort)
                {
                    InfoService.Instance.InjectValue(keys.PropShortKey, newShort);
                }
            }
        }

        private void HandleExecutionError(PluginInstanceConfig inst, PluginTemplate tmpl, Dictionary<string, string> inputs, string keySuffix, Exception ex)
        {
            // [Fix] Removed aggressive ResetNetworkClients() on single error.
            // Let PluginManager handle network resets based on consecutive failures to avoid client storm.

            // [Improvement] Even on error, try to resolve labels if possible (so user knows which item failed)
            if (tmpl.Outputs != null)
            {
                foreach(var output in tmpl.Outputs) 
                {
                    string injectKey = inst.Id + keySuffix + "." + output.Key;
                    
                    // Inject Error Status
                    InfoService.Instance.InjectValue(injectKey, PluginConstants.STATUS_ERROR);

                    // Try resolve label (ignore errors in template resolution)
                    try 
                    {
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

                        string propLabelKey = "PROP.Label." + itemKey;
                        string propShortKey = "PROP.ShortLabel." + itemKey;

                        if (InfoService.Instance.GetValue(propLabelKey) != newName)
                            InfoService.Instance.InjectValue(propLabelKey, newName);

                        if (InfoService.Instance.GetValue(propShortKey) != newShort)
                            InfoService.Instance.InjectValue(propShortKey, newShort);
                    }
                    catch (Exception lblEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"Label resolution failed during error handling: {lblEx.Message}");
                        // [Fix] Fallback to raw pattern if template resolution fails, to avoid showing raw keys like DASH.GitHub.0.stats
                        try 
                        {
                            string itemKey = PluginConstants.DASH_PREFIX + injectKey;
                            string fallback = !string.IsNullOrEmpty(output.Label) ? output.Label : (tmpl.Meta.Name + " " + output.Key);
                            
                            // Remove template markers to make it look cleaner if possible
                            if (fallback.Contains("{{")) 
                            {
                                // Simple cleanup: remove {{...}} parts or keep them, keeping them is better than nothing
                            }

                            InfoService.Instance.InjectValue(UIUtils.Intern("PROP.Label." + itemKey), fallback);
                        }
                        catch {}
                    }
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
