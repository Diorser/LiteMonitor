using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Principal;
using System.Threading.Tasks;

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
        // private bool _isDownloading = false; // [修改] 移交给 DriverInstaller 管理
        private bool _isRunning = false;     // FPS 计数服务运行状态
        private bool _isRestarting = false;  // 服务重启状态
        
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
        
        // [删除] 不再需要硬编码的下载链接
        // private const string DownloadUrl = "https://github.com/GameTechDev/PresentMon/releases/download/v1.9.2/PresentMon-1.9.2-x64.exe";

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

            // 3秒检查一次服务健康状态
            Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(3000);
                    CheckHealth();
                }
            });
            
            // 启动 PresentMon 服务
            Task.Run(() => StartService());
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
            if (!_isRunning) return 0f;
            if (_calculatedProcessFps.IsEmpty) 
            {
                _currentFocusPid = 0;
                return 0f;
            }

            // 粘性锁定逻辑：找出全场 FPS 最高的进程
            var challenger = _calculatedProcessFps.OrderByDescending(x => x.Value).First();
            int challengerPid = challenger.Key;
            float challengerFps = challenger.Value;

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
            try { return Process.GetProcessById(pid).ProcessName.Equals("dwm", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>
        /// 检查服务健康状态
        /// </summary>
        private void CheckHealth()
        {
            if (_isRestarting) return;
            // 判断服务是否异常：超过3秒没有收到数据或进程已退出
            bool isDead = _isRunning && (DateTime.Now - _lastDataTime).TotalSeconds > 3;
            if (_presentMonProc == null || _presentMonProc.HasExited) isDead = true;
            // 异常时重启服务
            if (isDead) Task.Run(() => RestartService());
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
                // 检查是否有管理员权限（PresentMon 需要管理员权限）
                if (!IsAdministrator()) return;
                
                // 处理 PresentMon 可执行文件的重命名逻辑
                // [注意] 这里假设 resources/assets 下可能存在 pm_lite.exe 这种旧名称，保持原逻辑兼容
                string pmLite = Path.Combine(AssetDir, "pm_lite.exe");
                if (File.Exists(pmLite) && !File.Exists(ExePath)) File.Move(pmLite, ExePath);
                
                // ★★★ [修改] 如果 PresentMon 不存在，调用 DriverInstaller 自动下载 ★★★
                if (!File.Exists(ExePath))
                {
                    // 调用同步等待 (因为 StartService 是在 Task.Run 里跑的，所以不会卡死 UI)
                    // 使用 silent: true，静默下载
                    var downloadTask = _driverInstaller.CheckAndDownloadPresentMon(silent: true);
                    downloadTask.Wait(); 
                }
                
                if (!File.Exists(ExePath)) return;

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
                _lastDataTime = DateTime.Now; 
                // 忽略错误输出
                _presentMonProc.ErrorDataReceived += (s, e) => { };
                _presentMonProc.BeginErrorReadLine();

                // 监控进程退出状态
                Task.Run(async () => { await _presentMonProc.WaitForExitAsync(); _isRunning = false; });

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
                                // 更新最后数据时间
                                _lastDataTime = DateTime.Now;
                            }
                        }
                    }
                    catch { }
                });
            }
            catch { }
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
                if (line.StartsWith("Application")) return;
                // 按逗号分割数据
                var parts = line.Split(',');
                if (parts.Length < 2) return;
                // 解析 PID 并累加帧数
                if (int.TryParse(parts[1], out int pid))
                {
                    _processFrameCounts.AddOrUpdate(pid, 1, (k, v) => v + 1);
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
                if (pName == "LiteMonitor" || pName == "LiteMonitorFPS" || pName == "PresentMon" || pName == "Unknown")
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

                // 计算最终平滑的 FPS：去头去尾取平均
                float finalFps = 0;
                if (olyHistory.Count >= 4)
                {
                    var sortedList = olyHistory.OrderBy(x => x).ToList();
                    float sum = 0;
                    // 去掉最高值和最低值，计算剩余值的平均值
                    for (int i = 1; i < sortedList.Count - 1; i++) sum += sortedList[i];
                    finalFps = sum / (sortedList.Count - 2);
                }
                else
                {
                    // 数据不足时直接取平均
                    finalFps = olyHistory.Average();
                }

                // 结果存储与清理
                if (finalFps < 1.0f) 
                {
                    // FPS 过低，移除该进程
                    _calculatedProcessFps.TryRemove(pid, out _);
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
        /// 获取进程名称
        /// </summary>
        /// <param name="pid">进程 PID</param>
        /// <returns>进程名称，失败时返回 "Unknown"</returns>
        private string GetProcessName(int pid)
        {
            try { return Process.GetProcessById(pid).ProcessName; } catch { return "Unknown"; }
        }

        // [删除] 原有的 DownloadPresentMon 方法已废弃，改用 DriverInstaller

        /// <summary>
        /// 清理 PresentMon 僵尸进程和会话
        /// </summary>
        private void ForceKillZombies()
        {
            try
            {
                // 终止所有名为 PresentMon 的进程
                foreach (var p in Process.GetProcessesByName("LiteMonitorFPS")) { try { p.Kill(); } catch { } }
                // 停止 PresentMon 会话
                Process.Start(new ProcessStartInfo { FileName = "logman", Arguments = $"stop {SESSION_NAME} -ets", UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(100);
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
                // 终止 PresentMon 进程并清理僵尸进程
                _presentMonProc?.Kill(); 
                ForceKillZombies(); 
            } catch { }
        }
    }
}