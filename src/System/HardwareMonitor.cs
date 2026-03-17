using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices; // ★★★ 新增：引用用于内存修剪的库
using System.Reflection; // ★★★ 新增：用于反射关闭历史记录
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices; // ★★★ [MTT] 摩尔线程支持
using System.Linq;
using System.Threading;

namespace LiteMonitor.src.SystemServices
{
    public sealed class HardwareMonitor : IDisposable
    {
        #region Singleton & Events
        public static HardwareMonitor? Instance { get; private set; }
        public event Action? OnValuesUpdated;
        public event Action? OnHardwareNamesCached; // ★★★ [新增] 硬件名称缓存完成事件 ★★★
        #endregion

        #region Private Fields
        private readonly Settings _cfg;
        private readonly Computer _computer;
        private readonly object _lock = new object();

        // 拆分出的子服务
        private readonly SensorMap _sensorMap;
        private readonly NetworkManager _networkManager;
        private readonly DiskManager _diskManager;
        private readonly FpsCounter _fpsCounter;
        private readonly DriverInstaller _driverInstaller;
        private readonly HardwareValueProvider _valueProvider;

        // 性能计数器管理器
        private readonly PerformanceCounterManager _perfCounterManager;
        
        // ★★★ [新增] 摩尔线程 GPU 监控器 ★★★
        private readonly MttGpuMonitor _mttGpuMonitor;

        private readonly Dictionary<string, float> _lastValidMap = new();
        
        // 状态标记 (防止并发重载)
        private volatile bool _isReloading = false;
        private volatile bool _initialized = false; // ★★★ [新增] 标记硬件是否已初始化 ★★★

        // 计时器相关
        private long _tickCounter = 0;
        private double _secondAccumulator = 0;
        private long _secondsCounter = 0;
        private DateTime _lastTrafficTime = DateTime.Now;
        #endregion

        #region Public Properties
        // [新增] 允许 UI 层访问原始硬件树（用于硬件信息面板）
        public IComputer ComputerInstance => _computer;
        public object SyncLock => _lock;
        #endregion

        #region Constructor
        public HardwareMonitor(Settings cfg)
        {
            _cfg = cfg;
            Instance = this;

            // 1. 初始化 Computer
            _computer = new Computer()
            {
                // ★★★ 修正：强制开启 CPU ★★★
                // 必须始终为 true。如果依赖 IsAnyEnabled("")，当用户未开启任何 CPU 监控项时，
                // LHM 将不会初始化 CPU 节点。后续即使通过热切换开启了风扇监控，
                // 由于 RefreshHardwareConfig 仅切换 IsControllerEnabled 而不更新 IsCpuEnabled，
                // 依然无法读取到依赖 CPU 节点的传感器数据。
                IsCpuEnabled = true, 
                
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsNetworkEnabled = true,
                IsStorageEnabled = true,
                IsMotherboardEnabled = true,
                
                // ★★★ 优化 T0：动态开启控制器扫描 ★★★
                // 默认关闭以避免 USB 冲突，仅当需要风扇/水泵/主板温度时开启
                IsControllerEnabled = ShouldEnableController(), 

                // 开启电池监控
                IsBatteryEnabled = true,

                // 顺便确保 PSU 也关闭（通常不需要监控电源模块，除非是高端 Corsair 电源）
                IsPsuEnabled = false
            };

            // ★★★ [新增] 1. 初始化计数器管理器 (必须在 ValueProvider 之前) ★★★
            _perfCounterManager = new PerformanceCounterManager();
            
            // ★★★ [新增] 初始化摩尔线程 GPU 监控器 ★★★
            _mttGpuMonitor = new MttGpuMonitor();

            // 2. 初始化服务
            _sensorMap = new SensorMap();
            _networkManager = new NetworkManager(_perfCounterManager);
            _diskManager = new DiskManager();
            _driverInstaller = new DriverInstaller(cfg, _computer, ReloadComputerSafe);
            _fpsCounter = new FpsCounter(_driverInstaller); // <--- 新增

            // ★★★ [修改] 2. 将 Manager 注入给 ValueProvider ★★★
            _valueProvider = new HardwareValueProvider(
                _computer, 
                cfg, 
                _sensorMap, 
                _networkManager, 
                _diskManager, 
                _fpsCounter, 
                _perfCounterManager, 
                _lock, 
                _lastValidMap
            );
            
            // ★★★ [新增] 设置摩尔线程 GPU 监控器引用 ★★★
            _valueProvider.SetMttGpuMonitor(_mttGpuMonitor);

            // 3. 异步启动 (唯一优化：不卡UI)
            InitializeAsync();
        }
        #endregion

        #region Public Methods

        public float? Get(string key) => _valueProvider.GetValue(key);

        public string GetNetworkIP() => _networkManager.GetCurrentIP();
        
        // ★★★ [新增] 获取摩尔线程 GPU 数据 ★★★
        public MttGpuData? GetMttGpuData() => _mttGpuMonitor?.Update();
        
        // ★★★ [新增] 判断是否存在摩尔线程 GPU ★★★
        public bool HasMttGpu => _mttGpuMonitor?.HasMttGpu ?? false;
        
        // ★★★ [新增] 缓存 CPU/GPU 名称，避免重复获取 ★★★
        private string _cachedCpuName = "";
        private string _cachedGpuName = "";
        private bool _namesCached = false;
        
        /// <summary>
        /// ★★★ [新增] 缓存硬件名称（在初始化完成后调用）★★★
        /// </summary>
        private void CacheHardwareNames()
        {
            try
            {
                // CPU
                var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                _cachedCpuName = cpu?.Name ?? "";
                
                // GPU
                if (_mttGpuMonitor?.HasMttGpu == true && !string.IsNullOrEmpty(_mttGpuMonitor.GpuName))
                {
                    _cachedGpuName = "Moore Threads " + _mttGpuMonitor.GpuName; // ★★★ 添加前缀 ★★★
                }
                else
                {
                    var gpu = _sensorMap.CachedGpu;
                    _cachedGpuName = gpu?.Name ?? "";
                }
                
                _namesCached = true;
                
                // ★★★ 触发事件，通知 UI 刷新 ★★★
                OnHardwareNamesCached?.Invoke();
            }
            catch { }
        }
        
        /// <summary>
        /// ★★★ [新增] 获取 CPU 名称 ★★★
        /// </summary>
        public string GetCpuName()
        {
            // 优先使用缓存
            if (_namesCached && !string.IsNullOrEmpty(_cachedCpuName))
                return _cachedCpuName;
            
            try
            {
                var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                if (cpu != null && !string.IsNullOrEmpty(cpu.Name))
                {
                    _cachedCpuName = cpu.Name;
                    return cpu.Name;
                }
            }
            catch { }
            return "";
        }
        
        /// <summary>
        /// ★★★ [新增] 获取 GPU 名称 ★★★
        /// </summary>
        public string GetGpuName()
        {
            // 优先使用缓存
            if (_namesCached && !string.IsNullOrEmpty(_cachedGpuName))
                return _cachedGpuName;
            
            try
            {
                // 优先：摩尔线程 GPU
                if (_mttGpuMonitor?.HasMttGpu == true && !string.IsNullOrEmpty(_mttGpuMonitor.GpuName))
                {
                    _cachedGpuName = "Moore Threads " + _mttGpuMonitor.GpuName; // ★★★ 添加前缀 ★★★
                    return _cachedGpuName;
                }
                
                // 其次：LHM 检测到的 GPU
                var gpu = _sensorMap.CachedGpu;
                if (gpu != null && !string.IsNullOrEmpty(gpu.Name))
                {
                    _cachedGpuName = gpu.Name;
                    return gpu.Name;
                }
            }
            catch { }
            return "";
        }

        // ★★★ 新增：允许主程序手动触发驱动检查 (用于解决启动弹窗冲突) ★★★
        public Task SmartCheckDriver() => _driverInstaller.SmartCheckDriver();

        // ★★★ [新增] 动态刷新硬件配置 (热切换) ★★★
        public void RefreshHardwareConfig()
        {
            // 1. 计算期望状态
            bool targetState = ShouldEnableController();

            // 2. 检查当前状态 (无需加锁，bool读写原子且 IsControllerEnabled 只是个属性)
            bool currentState = _computer.IsControllerEnabled;

            // 3. 如果状态一致，无需操作
            if (targetState == currentState) return;

            // ★★★ [需求变更] 关闭风扇/水泵不需要重载，仅首次开启时重载 ★★★
            // 避免关闭监控项时触发重载导致软件短暂卡顿或设备重连
            // 如果当前已开启 (true) 且目标是关闭 (false)，则保持开启状态，不执行重载
            if (currentState && !targetState)
            {
                System.Diagnostics.Debug.WriteLine($"[HotSwap] Controller disable requested but ignored to avoid reload (Latch Mode).");
                return;
            }

            // 4. 状态变更 (False -> True)，触发异步重载
            System.Diagnostics.Debug.WriteLine($"[HotSwap] Controller State Change: {currentState} -> {targetState}");

            // Fire-and-forget 异步任务，避免阻塞 UI
            Task.Run(() =>
            {
                // 更新配置属性
                _computer.IsControllerEnabled = targetState;

                // 触发安全重载
                // 由于 ReloadComputerSafe 全程持有 _lock，
                // 而 UpdateAll 和 GetValue 都改用了 TryEnter，
                // 所以这里会独占 _lock 几秒钟，期间 UI 不会卡死（只会显示空数据）。
                ReloadComputerSafe();
            });
        }

        public void UpdateAll()
        {
            // [Fix #290] 标记是否需要触发重载（因硬件变更或故障）
            bool needsReload = false;

            try
            {
                // 1. 更新计时器
                double timeDelta = UpdateTiming();

                // 2. 计算更新需求
                var requirements = CheckUpdateRequirements();

                lock (_lock)
                {
                    // [Optimization] 极简维护
                    // 仅每10分钟做一次兜底检查 (内部自动 Rebuild)，无需关心返回值。
                    // 即使重建了，ValueProvider 会在下一次取值时懒加载或直接从 Map 取，也不需要手动 PreCache。
                    _sensorMap.EnsureFresh(_computer, _cfg);

                    // 3秒一次 (慢速扫描)
                    bool isSlowScanTick = (_secondsCounter % 3 == 0); 
                    // 10秒一次 (磁盘后台扫描)
                    bool needDiskBgScan = (_secondsCounter % 10 == 0);

                    foreach (var hw in _computer.Hardware)
                    {
                        if (hw.HardwareType == HardwareType.Cpu && requirements.NeedCpu) { hw.Update(); continue; }
                        
                        // [Fix #290] 显卡防闪退保护：双显卡切换时 Update 可能抛出异常
                        if (IsGpu(hw) && requirements.NeedGpu) 
                        { 
                            try { hw.Update(); }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[GPU Error] Update failed: {ex.Message}");
                                needsReload = true;
                            }
                            continue; 
                        }

                        if (hw.HardwareType == HardwareType.Memory && requirements.NeedMem) { hw.Update(); continue; }
                        if (hw.HardwareType == HardwareType.Battery && requirements.NeedBat) { hw.Update(); continue; }

                        if (hw.HardwareType == HardwareType.Network && requirements.NeedNet)
                        {
                            // ★★★ 修复：使用正确的 timeDelta，而不是 requirements.TimeDelta (它是0) ★★★
                            _networkManager.ProcessUpdate(hw, _cfg, timeDelta, isSlowScanTick);
                            continue;
                        }
                        if (hw.HardwareType == HardwareType.Storage && requirements.NeedDisk)
                        {
                            _diskManager.ProcessUpdate(hw, _cfg, isSlowScanTick, needDiskBgScan);
                            continue;
                        }
                        
                        // 递归更新主板 (Motherboard / SuperIO)
                        if (IsMoboOrCooler(hw) && requirements.NeedMobo)
                        {
                             // ★★★ [优化] 降低更新频率：主板传感器每 3 秒更新一次，减少 I/O 阻塞 ★★★
                             // [Fix] 无论是否 ForceAll (WebServer)，只要是 SuperIO 这种慢速设备，
                             // 都必须强制跟随慢速扫描周期 (isSlowScanTick)，禁止高频更新。
                             if (isSlowScanTick)
                             {
                                 UpdateWithSubHardware(hw);
                             }
                             continue;
                        }
                    }
                }

                _valueProvider.OnUpdateTickStarted();
                
                // ★★★ [新增] 更新摩尔线程 GPU 数据 ★★★
                if (_mttGpuMonitor?.HasMttGpu == true)
                {
                    _mttGpuMonitor.Update();
                }
                
                // 任务错峰执行 (调用 SystemOptimizer)
                SystemOptimizer.RunMaintenanceTasks(_secondsCounter);
                
                OnValuesUpdated?.Invoke();
            }
            catch { }

            // [Fix #290] 硬件故障自动恢复
            // 如果检测到 GPU 丢失或驱动崩溃，触发安全重载以刷新硬件列表
            if (needsReload && !_isReloading)
            {
                _isReloading = true;
                System.Diagnostics.Debug.WriteLine("[HardwareMonitor] Hardware change detected, triggering reload...");
                
                // 异步延迟重载，给硬件切换留出缓冲时间
                Task.Run(async () => 
                {
                    try
                    {
                        await Task.Delay(2000); // 等待 2秒让系统硬件列表稳定
                        ReloadComputerSafe();
                    }
                    finally
                    {
                        _isReloading = false;
                    }
                });
            }
        }

        public void CleanMemory(Action<int>? onProgress = null) => SystemOptimizer.CleanMemory(onProgress);

        public void Dispose()
        {
            // ★★★ 核心修复：加锁！防止与正在运行的 UpdateAll() 冲突 ★★★
            lock (_lock)
            {
                try 
                {
                    // 将 Close 操作放入锁内，确保此时 UpdateAll 不在运行
                    _computer.Close(); 
                }
                catch (Exception ex)
                {
                    // 吞掉关闭时的异常，防止弹窗报错困扰用户
                    System.Diagnostics.Debug.WriteLine($"Dispose Error: {ex.Message}");
                }
            }

            _valueProvider.Dispose();
            _perfCounterManager.Dispose(); // ★★★ [新增] 释放计数器资源 ★★★
            _fpsCounter.Dispose(); // <--- 新增
            _mttGpuMonitor?.Dispose(); // ★★★ [新增] 释放摩尔线程 GPU 监控器 ★★★
            _networkManager.ClearCache();
            _diskManager.ClearCache(); // 漏掉的，补上
        }
        #endregion

        #region Private Helpers (Logic)

        private void InitializeAsync()
        {
            Task.Run(async () =>
            {
                try
                {
                    // ★★★ [新增] 启动计数器预热 (不阻塞主 UI) ★★★
                    _perfCounterManager.InitializeAsync();
                    
                    // ★★★ [新增] 初始化摩尔线程 GPU 监控器 ★★★
                    _mttGpuMonitor.Initialize();
                    
                    // 这句耗时 4-5 秒，但在执行过程中，硬件会陆续添加到 _computer.Hardware
                    _computer.Open();

                    // ★★★ T0+级修复：彻底禁用历史记录，解决 SensorValue[] 飙升 ★★★
                    // 必须在 Open() 之后调用，此时传感器才被创建
                    DisableSensorHistory();

                    // 只有全部扫描完，才建立高速 Map
                    lock (_lock)
                    {
                        // 1. 先进行一次全量更新 (预热)
                        // 这一步至关重要！它确保了随后 Rebuild 时，SensorMap 能读到传感器的数值
                        foreach (var hw in _computer.Hardware) hw.Update();

                        // 2. 数据有了，再建立映射
                        _sensorMap.Rebuild(_computer, _cfg);
                        
                        // 3. ★★★ [新增] 缓存硬件名称 ★★★
                        CacheHardwareNames();
                        
                        // 4. 静态化预热：将所有传感器对象存入 Provider 缓存
                        _valueProvider.PreCacheAllSensors(_sensorMap);
                        
                        _initialized = true; // ★★★ [新增] 标记初始化完成 ★★★
                    }

                    // 优化 T1：启动后大扫除
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    SystemOptimizer.TrimWorkingSet();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Init Error: {ex.Message}");
                }
            });
        }

        // ★★★ [新增] 动态判断是否需要开启控制器 ★★★
        private bool ShouldEnableController()
        {
            // 检查是否开启了任何需要读取 SuperIO/USB 控制器的监控项
            // 通常是风扇、水泵、主板温度
            if (_cfg.IsAnyEnabled("CPU.Fan")) return true;
            if (_cfg.IsAnyEnabled("CPU.Pump")) return true;
            if (_cfg.IsAnyEnabled("CASE.Fan")) return true;
            // 注意：MOBO.Temp 有些可能通过 WMI 读取，不一定需要 Controller，但为了保险起见，如果开了 MOBO 也开启
            // if (_cfg.IsAnyEnabled("MOBO")) return true; 
            return false;
        }

        private double UpdateTiming()
        {
            // 1. 统一心跳计数 (假设 UpdateAll 约 1秒调用一次)
            _tickCounter++;
            
            // 2. 计算精确时间差 (仅用于网速计算)
            DateTime now = DateTime.Now;
            double timeDelta = (now - _lastTrafficTime).TotalSeconds;
            _lastTrafficTime = now;
            if (timeDelta > 5.0) timeDelta = 0; // 防止休眠唤醒后的数据突刺

            // ★★★ [智能处理] 刷新率自适应逻辑 ★★★
            // 无论 UpdateAll 是 300ms 调一次还是 2s 调一次，
            // 我们都将时间累加，直到凑满 1秒，才增加 _secondsCounter。
            // 这样后续的 % 60, % 600 逻辑就是基于"真实时间"，而非"调用次数"。
            _secondAccumulator += timeDelta;
            while (_secondAccumulator >= 1.0)
            {
                _secondAccumulator -= 1.0;
                _secondsCounter++;
            }

            return timeDelta;
        }

        private (bool ForceAll, bool NeedCpu, bool NeedGpu, bool NeedMem, bool NeedNet, bool NeedDisk, bool NeedBat, bool NeedMobo, double TimeDelta) CheckUpdateRequirements()
        {
            // === [优化开始] 精细化判断更新需求 ===
        
            // 1. 获取计数器状态
            bool useCounter = _cfg.UseWinPerCounters && _perfCounterManager.IsInitialized;
            
            // ★★★ [优化] 全量更新判断 ★★★
            // 如果开启了 WebServer，则需要强制更新所有硬件，因为网页端可能会查看主界面未开启的项目
            bool forceAll = _cfg.WebServerEnabled;

            // 2. CPU: 总是需要 (因为 LHM 要读温度)
            bool needCpu = forceAll || _cfg.IsAnyEnabled("CPU");
            
            // 3. 显卡: 总是需要
            bool needGpu = forceAll || _cfg.IsAnyEnabled("GPU");
            
            // 4. ★★★ [优化 1] 内存: 如果走了计数器，就不需要 LHM 轮询了 ★★★
            bool needMem = forceAll || (_cfg.IsAnyEnabled("MEM") && !useCounter);
            
            // 5. 网络: 保持不变
            bool needNet = forceAll || _cfg.IsAnyEnabled("NET") || _cfg.IsAnyEnabled("DATA");
            
            // 7. 电池: 只有在开启时才更新
            bool needBat = forceAll || _cfg.IsAnyEnabled("BAT");

            // 6. ★★★ [优化 2] 磁盘: 智能判断 ★★★
            // 只有当 (没开计数器 OR 指定了特定盘 OR 需要看温度) 时，才需要 LHM 介入
            bool needDiskTemp = _cfg.IsAnyEnabled("DISK.Temp");
            bool hasSpecificDisk = !string.IsNullOrEmpty(_cfg.PreferredDisk);
            bool needDiskSpeed = _cfg.IsAnyEnabled("DISK") && (!useCounter || hasSpecificDisk);
            bool needDisk = forceAll || needDiskSpeed || needDiskTemp;

            // 判断主板更新需求
            bool needMobo = forceAll ||
                _cfg.IsAnyEnabled("MOBO") ||
                _cfg.IsAnyEnabled("CPU.Fan") ||
                _cfg.IsAnyEnabled("CPU.Pump") ||
                _cfg.IsAnyEnabled("CASE.Fan");
            
            return (forceAll, needCpu, needGpu, needMem, needNet, needDisk, needBat, needMobo, 0);
        }

        private void ReloadComputerSafe()
        {
            try
            {
                lock (_lock)
                {
                    // 1. 清理业务缓存
                    _networkManager.ClearCache();
                    _diskManager.ClearCache();
                    _sensorMap.Clear();
                    
                    _valueProvider.ClearCache();
                    
                    // 清理扫描器缓存
                    HardwareScanner.ClearCache();

                    // 2. 清理字符串池
                    UIUtils.ClearStringPool();

                    // 3. 关闭旧硬件服务
                    if (_computer != null)
                    {
                        _computer.Accept(new HardwareVisitor(h => { }));
                        _computer.Close();
                        // ★★★ 核心修复：手动清空硬件列表 ★★★
                        // LHM 的 Close() 不会清空列表，必须手动 Clear，否则再次 Open 会追加重复硬件
                        _computer.Hardware.Clear();
                    }
                    
                    _computer.Open();

                    DisableSensorHistory();
                }

                _sensorMap.Rebuild(_computer, _cfg);
                _valueProvider.PreCacheAllSensors(_sensorMap);

                // 4. 优化 T1：重置后再次修剪内存
                GC.Collect();
                SystemOptimizer.TrimWorkingSet();
            }
            catch { }
        }

        // =========================================================
        // ★★★ 核心修复：禁用所有传感器的历史记录 ★★★
        // 这将阻止 LibreHardwareMonitor 在内存中保留 24 小时的数据缓存
        // =========================================================
        private void DisableSensorHistory()
        {
            try { _computer.Accept(new DisableHistoryVisitor()); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MemoryFix] Failed: {ex.Message}"); }
        }

        // 递归更新子硬件，确保 SuperIO 刷新
        private void UpdateWithSubHardware(IHardware hw)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) UpdateWithSubHardware(sub);
        }

        private static bool IsGpu(IHardware hw)
        {
            return hw.HardwareType == HardwareType.GpuNvidia || 
                   hw.HardwareType == HardwareType.GpuAmd || 
                   hw.HardwareType == HardwareType.GpuIntel;
        }

        private static bool IsMoboOrCooler(IHardware hw)
        {
            return hw.HardwareType == HardwareType.Motherboard || 
                   hw.HardwareType == HardwareType.SuperIO || 
                   hw.HardwareType == HardwareType.Cooler;
        }
        #endregion

        #region Static UI Helpers (Delegated to HardwareScanner)
        public static string GenerateSmartName(ISensor sensor, IHardware hardware) => 
            HardwareScanner.GenerateSmartName(sensor, hardware, Instance!._computer);

        public static List<string> ListAllNetworks() => HardwareScanner.ListAllNetworks(Instance!._computer);

        public static List<string> ListAllDisks() => HardwareScanner.ListAllDisks(Instance!._computer);

        public static List<string> ListAllFans() => HardwareScanner.ListAllFans(Instance!._computer, Instance!._lock);

        public static List<string> ListAllMoboTemps() => HardwareScanner.ListAllMoboTemps(Instance!._computer, Instance!._lock);
        #endregion

        #region Inner Visitors
        // ★★★ 核心修复：禁用所有传感器的历史记录 ★★★
        /// <summary>
        /// 专用访问器：将所有 Sensor 的 ValuesTimeWindow 设为 0
        /// </summary>
        private class DisableHistoryVisitor : IVisitor
        {
            public void VisitComputer(IComputer computer) => computer.Traverse(this);
            public void VisitHardware(IHardware hardware)
            {
                foreach (var sub in hardware.SubHardware) sub.Accept(this);
                foreach (var sensor in hardware.Sensors) VisitSensor(sensor);
            }
            public void VisitSensor(ISensor sensor)
            {
                // ★ 关键：通过反射设置 ValuesTimeWindow = TimeSpan.Zero
                // 因为 ISensor 接口通常不暴露这个属性，它属于具体的 Sensor 类
                try
                {
                    var prop = sensor.GetType().GetProperty("ValuesTimeWindow");
                    if (prop != null && prop.CanWrite) prop.SetValue(sensor, TimeSpan.Zero);
                }
                catch { }
            }
            public void VisitParameter(IParameter parameter) { }
        }

        // 内部 Visitor 类，用于触发 LHM 清理逻辑
        private class HardwareVisitor : IVisitor
        {
            private readonly Action<IHardware> _action;
            public HardwareVisitor(Action<IHardware> action) { _action = action; }
            public void VisitComputer(IComputer computer) => computer.Traverse(this);
            public void VisitHardware(IHardware hardware)
            {
                _action(hardware);
                foreach (var sub in hardware.SubHardware) sub.Accept(this);
            }
            public void VisitSensor(ISensor sensor) { }
            public void VisitParameter(IParameter parameter) { }
        }
        #endregion
    }
}
