using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Win32; // ★★★ 新增引用 ★★★

// LiteMonitor 系统服务命名空间
namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// FPS 计数器类，用于监控系统中各进程的帧率
    /// 使用 PresentMon 收集帧率数据，并通过多层算法平滑处理确保数据稳定
    /// 实现 IDisposable 接口以确保资源正确释放
    /// </summary>
    public class FpsCounter : IDisposable
    {
        // 状态标志
        private bool _isRunning = false;     // FPS 计数服务运行状态
        private bool _isRestarting = false;  // 服务重启状态
        
        // ★★★ [新增] 静态关机标志 ★★★
        private static bool _isSystemShuttingDown = false;

        static FpsCounter()
        {
            // 订阅系统关机事件
            try { SystemEvents.SessionEnding += (s, e) => _isSystemShuttingDown = true; } catch { }
        }

        // ★★★ [新增] 启动锁和最后 activity 时间，用于控制进程生命周期 ★★★
        private bool _isStarting = false;    // 防止重复启动的标志
        private DateTime _lastAccessTime = DateTime.MinValue; // 最后一次被 UI 请求数据的时间
        
        private Process? _presentMonProc;     // PresentMon 进程实例
        private DateTime _lastDataTime = DateTime.MinValue; // 最后一次收到数据的时间
        
        // ★★★ [新增] 引用 DriverInstaller ★★★
        private readonly DriverInstaller _driverInstaller;

        // 秒表计时，用于计算采样周期
        private Stopwatch _cycleTimer = new Stopwatch();
        
        // 原始数据累加：Key=PID，Value=本周期内的帧数
        private readonly ConcurrentDictionary<int, int> _processFrameCounts = new();
        
        // 第一层：滑动累计窗口（解决管道拥堵导致的 600~1200 FPS 跳动）
        // Key: PID, Value: 过去 N 次采样的记录
        private readonly ConcurrentDictionary<int, Queue<FrameSample>> _accumulatorHistory = new();

        // 第二层：奥运会平滑队列（解决数值毛刺）
        private readonly ConcurrentDictionary<int, Queue<float>> _olympicHistory = new();
        
        // 最终算出来的各进程稳定 FPS
        private readonly ConcurrentDictionary<int, float> _calculatedProcessFps = new();
        
        // ★★★ [新增] 进程名缓存，减少 GetProcessById 的 CPU 消耗 ★★★
        private readonly ConcurrentDictionary<int, string> _processNameCache = new();

        // ★★★ [新增] 排除进程列表，减少字符串比较开销 ★★★
        private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "LiteMonitor", "LiteMonitorFPS", "PresentMon", "Unknown"
        };

        // ★★★ [Fix BF6] 反作弊保护游戏黑名单：这些游戏使用内核级反作弊（EA Javelin/EAC/BattlEye/Vanguard/ACE），
        // 会检测并干扰 PresentMon 的系统级 ETW 帧率追踪会话，导致游戏被反作弊静默终止（表现为随机闪退、无报错）。
        // 命中黑名单时立即停止 ETW 会话并在冷却期内不再拉起。可按需继续补充进程名（不含 .exe 后缀）。
        private static readonly HashSet<string> ProtectedGames = new(StringComparer.OrdinalIgnoreCase)
        {
            "bf6", "bf2042",            // EA Javelin（战地系列）
            "r5apex", "apex",           // EAC（Apex 英雄）
            "valorant",                 // Vanguard（无畏契约）
            "tslgame", "pubg",          // BattlEye（绝地求生）
            "escapefromtarkov",         // BattlEye（逃离塔科夫）
            "fortnite",                 // EAC/BattlEye（堡垒之夜）
            "deltaforce"                // ACE（三角洲行动）
        };

        // ★★★ [Fix BF6] 反作弊守卫冷却期：命中黑名单后，此时间点之前 GetFps/StartService 一律拒绝拉起会话 ★★★
        private DateTime _protectedGuardUntil = DateTime.MinValue;

        // ★★★ [Fix BF6] 崩溃熔断：PresentMon 连续启动失败计数与冷却期（如被反作弊拒绝建会话时避免无限重启） ★★★
        private int _consecutiveFailures = 0;
        private DateTime _cooldownUntil = DateTime.MinValue;

        // ★★★ [Fix BF6] 启动时间与主动停止标志：区分"PresentMon 自行崩溃退出"与"程序主动 Dispose 杀进程" ★★★
        private DateTime _presentStartTime = DateTime.MinValue;
        private volatile bool _intentionalStop = false;
        
        // 锁定机制变量，用于实现粘性锁定逻辑
        private int _currentFocusPid = 0;       // 当前聚焦的进程 PID
        private int _pendingPid = 0;            // 待切换的进程 PID
        private int _pendingCount = 0;          // 待切换进程的连续领先周期数

        // PresentMon 会话名称
        private const string SESSION_NAME = "LiteMonitor_Golden_Session";
        
        // 累计窗口大小：4次采样 = 2.0秒（抹平 GpuTest 波动）
        private const int ACCUMULATOR_SIZE = 4;
        
        // 奥运会窗口大小：6次采样（平滑微小抖动）
        private const int OLYMPIC_SIZE = 6;

        // 文件路径常量
        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory; // 使用 AppDomain 确保准确
        // ★★★ [修改] 统一路径到 resources/assets ★★★
        private static readonly string AssetDir = Path.Combine(BaseDir, "resources");
        private static readonly string ExePath = Path.Combine(AssetDir, "LiteMonitorFPS.exe");
        private static readonly string LogPath = Path.Combine(BaseDir, "fps_debug.log");
        
        /// <summary>
        /// 内部结构体：一次采样的记录
        /// 存储某段时间内的帧数和持续时间
        /// </summary>
        private struct FrameSample
        {
            public int Count;       // 这一段收到的帧数
            public double Duration; // 这一段花费的时间(秒)
        }

        /// <summary>
        /// FpsCounter 构造函数
        /// 初始化环境并启动后台任务
        /// </summary>
        /// <param name="installer">注入 DriverInstaller 以复用下载逻辑</param>
        public FpsCounter(DriverInstaller installer)
        {
            _driverInstaller = installer;

            // [修改] 确保目录存在 (DriverInstaller 也会做，这里双重保险)
            try { if (!Directory.Exists(AssetDir)) Directory.CreateDirectory(AssetDir); } catch { }
            
            // 清除旧日志文件
            try { File.Delete(LogPath); } catch { }

            // 500ms 刷新一次 FPS 计算（稳定版频率）
            Task.Run(async () =>
            {
                _cycleTimer.Start(); 
                while (true)
                {
                    await Task.Delay(500); 
                    CalculateFps();
                }
            });

            // 3秒检查一次服务健康状态和生命周期
            Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(3000);
                    CheckHealth();
                }
            });
            
            // ★★★ [修改] 构造函数中不再自动启动服务，改为按需启动 ★★★
        }

        private void Log(string msg)
        {
            try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
        }

        /// <summary>
        /// 获取当前聚焦进程的 FPS 值
        /// 实现了粘性锁定逻辑，避免频繁切换焦点
        /// </summary>
        /// <returns>当前聚焦进程的 FPS 值，无数据时返回 0</returns>
        public float? GetFps()
        {
            // ★★★ [修改] 记录本次访问时间，并触发惰性启动 ★★★
            _lastAccessTime = DateTime.Now;

            // ★★★ [Fix BF6] 反作弊守卫/崩溃熔断冷却期内：不拉起会话，直接返回 ★★★
            if (DateTime.Now < _protectedGuardUntil || DateTime.Now < _cooldownUntil) return 0f;

            // 如果没运行且没在启动中，尝试启动服务
            if (!_isRunning && !_isStarting && !_isRestarting)
            {
                _isStarting = true; // 简单锁
                Task.Run(() => StartService());
            }

            if (!_isRunning) return 0f;
            if (_calculatedProcessFps.IsEmpty) 
            {
                _currentFocusPid = 0;
                return 0f;
            }

            // 粘性锁定逻辑：找出全场 FPS 最高的进程（优化版，避免 OrderBy 全量排序）
            int challengerPid = 0;
            float challengerFps = -1f;
            foreach (var pair in _calculatedProcessFps)
            {
                if (pair.Value > challengerFps)
                {
                    challengerFps = pair.Value;
                    challengerPid = pair.Key;
                }
            }

            if (challengerPid == 0) return 0f;

            // 焦点进程仲裁逻辑
            if (_currentFocusPid == 0 || !_calculatedProcessFps.ContainsKey(_currentFocusPid))
            {
                // 当前无焦点或焦点进程已退出，直接切换
                _currentFocusPid = challengerPid;
                _pendingCount = 0;
            }
            else if (challengerPid != _currentFocusPid)
            {
                // 有新的挑战者进程
                float currentFps = _calculatedProcessFps[_currentFocusPid];
                
                // 判断是否是 DWM 进程（桌面窗口管理器）
                bool isChallengerDwm = IsDwm(challengerPid);

                // DWM 进程需要更高的阈值才能切换（1.2倍），普通进程只需 1.1倍
                float thresholdRatio = isChallengerDwm ? 1.2f : 1.1f;

                if (challengerFps > currentFps * thresholdRatio)
                {
                    if (_pendingPid == challengerPid)
                    {
                        // 同一挑战者连续领先，增加计数
                        _pendingCount++;
                        // 碾压式超越（2倍）：直接切换
                        if (challengerFps > currentFps * 2.0f) _pendingCount += 10;

                        // 连续 4 个周期（约2秒）保持领先 -> 切换焦点
                        if (_pendingCount >= 2) 
                        {
                            _currentFocusPid = challengerPid;
                            _pendingCount = 0;
                        }
                    }
                    else
                    {
                        // 新挑战者出现，初始化计数
                        _pendingPid = challengerPid;
                        _pendingCount = 1;
                    }
                }
                else
                {
                    // 挑战者未达到切换阈值，重置计数
                    _pendingCount = 0;
                }
            }
            else
            {
                // 当前焦点仍是最高 FPS 进程，重置计数
                _pendingCount = 0;
            }

            // 返回当前焦点进程的 FPS 值
            if (_calculatedProcessFps.TryGetValue(_currentFocusPid, out float val))
            {
                return (float)Math.Round(val);
            }
            
            return 0f;
        }

        /// <summary>
        /// 判断是否是 DWM 进程（桌面窗口管理器）
        /// </summary>
        /// <param name="pid">进程 PID</param>
        /// <returns>是 DWM 进程返回 true，否则返回 false</returns>
        private bool IsDwm(int pid)
        {
            return GetProcessName(pid).Equals("dwm", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 检查服务健康状态
        /// </summary>
        private void CheckHealth()
        {
            if (_isRestarting || _isStarting) return;

            // ★★★ [新增] 自动关闭逻辑：如果超过 5 秒没有 UI 请求 FPS 数据，关闭进程 ★★★
            if (_isRunning && (DateTime.Now - _lastAccessTime).TotalSeconds > 5)
            {
                Dispose(); // 销毁进程
                return;
            }

            // 只有在应该运行（有请求）的情况下才检查僵死状态
            if (_isRunning && (DateTime.Now - _lastAccessTime).TotalSeconds <= 5)
            {
                // 判断服务是否异常：超过3秒没有收到数据或进程已退出
                bool procDead = _presentMonProc == null || _presentMonProc.HasExited;
                bool isStale = (DateTime.Now - _lastDataTime).TotalSeconds > 3;

                if (procDead)
                {
                    // [Fix BF6] 进程死亡由 WaitForExitAsync 任务统一记录熔断计数，这里只负责重启
                    Task.Run(() => RestartService());
                }
                else if (isStale)
                {
                    // [Fix BF6] 进程存活但 3 秒无数据（僵死），计入熔断计数后重启
                    RecordStartFailure("进程存活但超过 3 秒无数据");
                    Task.Run(() => RestartService());
                }
            }
        }

        /// <summary>
        /// [Fix BF6] 记录一次 PresentMon 启动失败；连续 3 次失败触发熔断（10 分钟内不再尝试启动 ETW 会话）。
        /// 典型场景：反作弊（如 EA Javelin）拒绝外部 ETW 会话导致 PresentMon 秒退，形成"崩溃-重启"循环
        /// </summary>
        private void RecordStartFailure(string reason)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= 3)
            {
                _cooldownUntil = DateTime.Now.AddMinutes(10);
                _consecutiveFailures = 0;
                Log($"[熔断] PresentMon 连续 3 次启动失败（最近原因：{reason}），熔断 10 分钟内不再启动 ETW 会话");
                Dispose();
            }
            else
            {
                Log($"[诊断] PresentMon 启动失败（第 {_consecutiveFailures} 次）：{reason}，将自动重试");
            }
        }

        /// <summary>
        /// [Fix BF6] 判断是否有受反作弊保护的游戏正在运行（用于启动会话前的预扫描）
        /// </summary>
        private static bool IsProtectedGameRunning()
        {
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (ProtectedGames.Contains(p.ProcessName)) return true;
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// [Fix BF6] 检测到 ETW 会话正在追踪受保护游戏：立即停止会话并进入冷却期，
        /// 游戏退出约 30 秒后自动恢复。这是战地6等内核反作弊游戏被静默闪退的核心防护点
        /// </summary>
        private void OnProtectedGameDetected(string gameName)
        {
            _protectedGuardUntil = DateTime.Now.AddSeconds(30);
            Log($"[反作弊守卫] 检测到 ETW 会话正在追踪受保护游戏 {gameName}，已立即停止帧率会话（避免反作弊冲突闪退）");
            Dispose();
        }

        /// <summary>
        /// 重启 PresentMon 服务
        /// </summary>
        private async Task RestartService()
        {
            _isRestarting = true;
            try
            {
                // 释放当前资源
                Dispose();
                // 短暂延迟确保资源完全释放
                await Task.Delay(1000);
                // 重新启动服务
                StartService();
            }
            finally { _isRestarting = false; }
        }

        /// <summary>
        /// 启动 PresentMon 服务
        /// 初始化 PresentMon 进程并设置数据接收
        /// </summary>
        private void StartService()
        {
            try
            {
                _isStarting = true;

                // ★★★ [Fix BF6] 熔断/反作弊守卫冷却期内不再启动会话 ★★★
                if (DateTime.Now < _cooldownUntil) return;
                if (DateTime.Now < _protectedGuardUntil) return;

                // 检查是否有管理员权限（PresentMon 需要管理员权限）
                if (!IsAdministrator()) return;
                
                // ★★★ [修改] 如果 PresentMon 不存在，调用 DriverInstaller 自动下载 ★★★
                if (!File.Exists(ExePath))
                {
                    var downloadTask = _driverInstaller.CheckAndDownloadPresentMon(silent: true);
                    downloadTask.Wait(); 
                }
                
                if (!File.Exists(ExePath)) return;

                // ★★★ [Fix BF6] 启动前预扫描：受保护游戏已在运行则不建 ETW 会话，
                // 关闭"会话先于游戏启动"的竞态窗口（游戏启动瞬间反作弊初始化最敏感）。
                // 每 10 秒放行一次复检，游戏持续运行时自动持续压制 ★★★
                if (IsProtectedGameRunning())
                {
                    _protectedGuardUntil = DateTime.Now.AddSeconds(10);
                    Log("[反作弊守卫] 检测到受保护游戏正在运行，跳过 ETW 帧率会话启动（10 秒后自动复检）");
                    return;
                }

                // 清理可能存在的僵尸进程
                ForceKillZombies();

                // 配置 PresentMon 进程启动信息
                var psi = new ProcessStartInfo
                {
                    FileName = ExePath,
                    Arguments = $"-session_name {SESSION_NAME} -stop_existing_session -output_stdout",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true, 
                    CreateNoWindow = true
                };

                // 启动 PresentMon 进程
                _presentMonProc = Process.Start(psi);
                if (_presentMonProc == null) return;

                // 更新服务状态
                _isRunning = true;
                _intentionalStop = false;          // [Fix BF6] 新进程的退出按"非主动停止"处理，用于熔断计数
                _presentStartTime = DateTime.Now;  // [Fix BF6] 记录启动时间，用于识别秒退
                _lastDataTime = DateTime.Now;
                Log("[Fix BF6] ETW 帧率会话已启动: " + SESSION_NAME);
                // 忽略错误输出
                _presentMonProc.ErrorDataReceived += (s, e) => { };
                _presentMonProc.BeginErrorReadLine();

                // 监控进程退出状态
                Task.Run(async () =>
                {
                    await _presentMonProc.WaitForExitAsync();
                    _isRunning = false;
                    // ★★★ [Fix BF6] 非主动停止且启动后 3 秒内退出 = 秒退（典型：反作弊拒绝建会话），计入熔断 ★★★
                    if (!_intentionalStop && (DateTime.Now - _presentStartTime).TotalSeconds < 3)
                    {
                        RecordStartFailure("PresentMon 启动后 3 秒内退出");
                    }
                    else if (!_intentionalStop)
                    {
                        // 运行超过 3 秒后才退出，视为曾正常工作，重置失败计数
                        _consecutiveFailures = 0;
                    }
                });

                // 异步处理 PresentMon 输出数据
                Task.Run(async () =>
                {
                    try
                    {
                        while (!_presentMonProc.StandardOutput.EndOfStream)
                        {
                            string? line = await _presentMonProc.StandardOutput.ReadLineAsync();
                            if (!string.IsNullOrWhiteSpace(line)) 
                            {
                                // 解析输出数据
                                ParseLine(line);
                                // [Fix BF6] 收到有效数据 = 启动成功，重置熔断计数
                                if (_consecutiveFailures != 0) _consecutiveFailures = 0;
                                // 更新最后数据时间
                                _lastDataTime = DateTime.Now;
                            }
                        }
                    }
                    catch { }
                });
            }
            catch { }
            finally
            {
                _isStarting = false;
            }
        }

        /// <summary>
        /// 解析 PresentMon 输出的一行数据
        /// 提取进程 PID 和帧数信息
        /// </summary>
        /// <param name="line">PresentMon 输出的一行文本</param>
        private void ParseLine(string line)
        {
            try
            {
                // 跳过表头行
                if (line.Length == 0 || line[0] == 'A') return; // 快速检查 "Application"

                // 优化解析：手动查找第一个和第二个逗号，避免 Split(',') 产生大量字符串碎片的 GC 压力
                int firstComma = line.IndexOf(',');
                if (firstComma == -1) return;

                int secondComma = line.IndexOf(',', firstComma + 1);
                int length = (secondComma == -1) ? line.Length - firstComma - 1 : secondComma - firstComma - 1;

                if (length > 0)
                {
                    // 使用 ReadOnlySpan 避免 Substring 内存分配
                    ReadOnlySpan<char> pidSpan = line.AsMemory(firstComma + 1, length).Span;
                    if (int.TryParse(pidSpan, out int pid))
                    {
                        _processFrameCounts.AddOrUpdate(pid, 1, (k, v) => v + 1);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 计算各进程的 FPS 值
        /// 使用双层算法：滑动窗口累加 + 奥运会平滑
        /// </summary>
        private void CalculateFps()
        {
            if (!_isRunning) return;

            // 获取本周期的精确时间（约 0.5s）
            double elapsedSeconds = _cycleTimer.Elapsed.TotalSeconds;
            _cycleTimer.Restart(); 

            if (elapsedSeconds <= 0) return;

            // 清理逻辑：如果没有原始数据，清空计算结果
            if (_processFrameCounts.IsEmpty)
            {
                 if (!_calculatedProcessFps.IsEmpty) _calculatedProcessFps.Clear();
                 return;
            }

            // 遍历所有收集到的进程帧数据
            foreach (var kv in _processFrameCounts)
            {
                int pid = kv.Key;
                int currentCount = kv.Value;
                _processFrameCounts[pid] = 0; // 读后归零，准备下一个周期

                // 获取进程名称，过滤不需要监控的进程
                string pName = GetProcessName(pid);

                // ★★★ [Fix BF6] 反作弊守卫（核心防护）：检测到 ETW 会话正在追踪受保护游戏，
                // 立即拆除会话并进入冷却期，避免内核反作弊（EA Javelin 等）因外部 ETW 追踪而静默杀游戏 ★★★
                if (ProtectedGames.Contains(pName))
                {
                    OnProtectedGameDetected(pName);
                    return;
                }

                if (ExcludedProcesses.Contains(pName))
                {
                    _calculatedProcessFps.TryRemove(pid, out _);
                    continue;
                }

                // 第一层：滑动累计算法（解决 600~1200 FPS 波动）
                var accHistory = _accumulatorHistory.GetOrAdd(pid, new Queue<FrameSample>());
                
                // 入队新的采样记录（保持最近 4 次采样）
                accHistory.Enqueue(new FrameSample { Count = currentCount, Duration = elapsedSeconds });
                while (accHistory.Count > ACCUMULATOR_SIZE) accHistory.Dequeue();

                // 累计求和：计算最近 2 秒内的总帧数和总时间
                long totalCount = 0;
                double totalTime = 0;
                foreach (var sample in accHistory)
                {
                    totalCount += sample.Count;
                    totalTime += sample.Duration;
                }

                // 计算初步稳定的 FPS（基于累计数据）
                float rawStableFps = 0;
                if (totalTime > 0) rawStableFps = (float)(totalCount / totalTime);
                
                // 瞬时响应优化：对于刚启动的进程，如果帧率很高，直接使用瞬时值
                if (accHistory.Count < 2 && (currentCount / elapsedSeconds) > 100)
                {
                    rawStableFps = (float)(currentCount / elapsedSeconds);
                }

                // 第二层：奥运会平滑算法（解决数值毛刺）
                var olyHistory = _olympicHistory.GetOrAdd(pid, new Queue<float>());
                olyHistory.Enqueue(rawStableFps);
                while (olyHistory.Count > OLYMPIC_SIZE) olyHistory.Dequeue();

                // 计算最终平滑的 FPS：去头去尾取平均（奥运会算法优化版）
                float finalFps = 0;
                int count = olyHistory.Count;
                if (count >= 4)
                {
                    float min = float.MaxValue;
                    float max = float.MinValue;
                    float sum = 0;
                    foreach (var f in olyHistory)
                    {
                        if (f < min) min = f;
                        if (f > max) max = f;
                        sum += f;
                    }
                    // 去掉一个最高值和一个最低值，计算剩余值的平均值
                    finalFps = (sum - min - max) / (count - 2);
                }
                else
                {
                    // 数据不足时直接取平均
                    float sum = 0;
                    foreach (var f in olyHistory) sum += f;
                    finalFps = sum / count;
                }

                // 结果存储与清理
                if (finalFps < 1.0f) 
                {
                    // FPS 过低，移除该进程
                    _calculatedProcessFps.TryRemove(pid, out _);
                    _processNameCache.TryRemove(pid, out _); // ★★★ 同步清理进程名缓存 ★★★
                    if (currentCount == 0 && totalCount == 0) 
                    {
                        // 完全没有帧数据，清理历史记录
                        _accumulatorHistory.TryRemove(pid, out _);
                        _olympicHistory.TryRemove(pid, out _);
                    }
                }
                else 
                {
                    // 更新该进程的最终 FPS
                    _calculatedProcessFps[pid] = finalFps;
                }
            }
        }

        /// <summary>
        /// 获取进程名称（带缓存优化）
        /// </summary>
        /// <param name="pid">进程 PID</param>
        /// <returns>进程名称，失败时返回 "Unknown"</returns>
        private string GetProcessName(int pid)
        {
            if (_processNameCache.TryGetValue(pid, out string? cachedName)) return cachedName;

            try 
            { 
                string name = Process.GetProcessById(pid).ProcessName; 
                _processNameCache.TryAdd(pid, name);
                return name;
            } 
            catch { return "Unknown"; }
        }

        /// <summary>
        /// 清理 PresentMon 僵尸进程和会话
        /// </summary>
        public static void ForceKillZombies()
        {
            // 如果是系统关机，跳过清理以避免 logman.exe 报错 (0xc0000142)
            if (_isSystemShuttingDown) return;

            try
            {
                // 1. 优先终止所有名为 LiteMonitorFPS 的进程 (最重要)
                foreach (var p in Process.GetProcessesByName("LiteMonitorFPS")) { try { p.Kill(); } catch { } }
                
                // 2. 停止 LiteMonitorFPS 会话
                // 仅在 CLR 未关闭时尝试调用 logman，避免在程序退出极晚期引发异常
                if (!Environment.HasShutdownStarted)
                {
                    Process.Start(new ProcessStartInfo { FileName = "logman", Arguments = $"stop {SESSION_NAME} -ets", UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(100);
                }
            }
            catch { }
        }

        /// <summary>
        /// 检查当前进程是否有管理员权限
        /// </summary>
        /// <returns>有管理员权限返回 true，否则返回 false</returns>
        public static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent()) {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            try {
                // [Fix BF6] 标记主动停止，避免 Kill 被误计为 PresentMon 崩溃
                _intentionalStop = true;
                // 终止 PresentMon 进程并清理僵尸进程
                _presentMonProc?.Kill();
                ForceKillZombies();
                _isRunning = false; // 同步重置状态
                // [Fix BF6] 清空本会话的帧数据，防止残留 PID 计数影响下次会话
                _processFrameCounts.Clear();
                _calculatedProcessFps.Clear();
            } catch { }
        }
    }
}