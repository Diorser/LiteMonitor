using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using Debug = System.Diagnostics.Debug;
using LiteMonitor; // 引用 DownloadContext

namespace LiteMonitor.src.SystemServices
{
    public class DriverInstaller
    {
        private static readonly Version RequiredPawnIOVersion = new Version(2, 2, 0, 0);
        private static readonly Version SafeInPlaceUpgradeVersion = new Version(2, 1, 0, 0);
        private const int ErrorAlreadyExists = 183;
        private const int ErrorSuccessRebootRequired = 3010;
        private const int ErrorServiceMarkedForDelete = 1072;

        private readonly Settings _cfg;
        private readonly Action? _onReloadRequired; // 回调：通知主程序重载
        private readonly Action? _onPrepareDriverInstall; // 回调：安装驱动前释放硬件句柄
        private bool _shouldExitAfterDriverOperation;

        // 完整发布包优先使用随包组件；缺失时从当前仓库对应版本恢复。
        private readonly string[] _driverPackageUrls = new[]
        {
            $"https://raw.githubusercontent.com/{ReleaseManifest.Repository}/v{UpdateChecker.GetCurrentVersion()}/resources/assets/driver.zip"
        };

        // ★★★ [新增] PresentMon 下载源 (FPS 单独下载用) ★★★
        private readonly string[] _presentMonUrls = new[]
        {
            $"https://raw.githubusercontent.com/{ReleaseManifest.Repository}/v{UpdateChecker.GetCurrentVersion()}/resources/assets/LiteMonitorFPS.exe"
        };
        
        // 缓存当前的下载任务，避免并发重复弹窗
        private static Task<bool>? _activeDownloadTask;
        private static readonly SemaphoreSlim _downloadLock = new SemaphoreSlim(1, 1);
        private static bool _pawnIORestartRequiredThisSession;
        private static bool _startupPawnIOCheckCompletedThisSession;
        private static bool _startupPawnIOCheckSawPendingDriver;
        private static bool _runtimeLegacyPawnIOPromptShown;

        private bool IsChinese => _cfg?.Language?.ToLower() == "zh";

        public DriverInstaller(Settings cfg, Action? onReloadRequired, Action? onPrepareDriverInstall)
        {
            _cfg = cfg;
            _onReloadRequired = onReloadRequired;
            _onPrepareDriverInstall = onPrepareDriverInstall;
        }

        public static bool CheckPawnIOBeforeHardware(Settings cfg)
        {
            PrepareLanguageForDialog(cfg);
            var installer = new DriverInstaller(cfg, null, null);

            try
            {
                return installer.CheckPawnIOBeforeHardwareAsync().GetAwaiter().GetResult();
            }
            finally
            {
                _startupPawnIOCheckCompletedThisSession = true;
            }
        }

        private static void PrepareLanguageForDialog(Settings cfg)
        {
            if (string.IsNullOrEmpty(cfg.Language))
            {
                string sysLang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLower();
                string langPath = Path.Combine(AppContext.BaseDirectory, "resources/lang", $"{sysLang}.json");
                cfg.Language = File.Exists(langPath) ? sysLang : "en";
            }

            LanguageManager.Load(cfg.Language);
            cfg.SyncToLanguage();
        }

        private async Task<bool> CheckPawnIOBeforeHardwareAsync()
        {
            var pawnIOCheck = CheckPawnIORequirement();
            _startupPawnIOCheckSawPendingDriver = pawnIOCheck.NeedsInstall;
            if (!pawnIOCheck.NeedsInstall) return true;

            await InstallDriverPackage(needFPS: false, pawnIOCheck, DriverInstallMode.StartupBeforeHardware).ConfigureAwait(false);
            return !_shouldExitAfterDriverOperation;
        }

        // ================================================================
        // ★★★ 公共入口 1：PawnIO 驱动检查 (启动时调用) ★★★
        // ================================================================
        public async Task SmartCheckDriver()
        {
            await EnsureComponents(forceFpsCheck: false);
        }
        
        /// <summary>
        /// 核心统一检查逻辑：防止并发弹窗，智能判断下载策略
        /// </summary>
        private async Task<bool> EnsureComponents(bool forceFpsCheck)
        {
            await _downloadLock.WaitAsync();
            try
            {
                if (_activeDownloadTask != null && !_activeDownloadTask.IsCompleted)
                {
                    return await _activeDownloadTask;
                }

                var pawnIOCheck = CheckPawnIORequirement();
                string fpsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "LiteMonitorFPS.exe");
                bool isFpsEnabled = _cfg.IsAnyEnabled("FPS");
                bool isFpsValid = IsValidExecutable(fpsPath);
                
                // 只要 FPS 开启且文件无效，或者被强制检查且文件无效，就需要处理
                bool needFPS = forceFpsCheck || (isFpsEnabled && !isFpsValid);

                if (pawnIOCheck.NeedsInstall && _startupPawnIOCheckSawPendingDriver)
                {
                    if (needFPS && !isFpsValid)
                    {
                        _activeDownloadTask = InstallFpsComponent();
                        return await _activeDownloadTask;
                    }

                    return false;
                }

                bool blockRuntimePawnIO = IsLegacyPawnIOUpgrade(pawnIOCheck);
                if (blockRuntimePawnIO && _startupPawnIOCheckCompletedThisSession)
                {
                    if (!_runtimeLegacyPawnIOPromptShown && !_startupPawnIOCheckSawPendingDriver)
                    {
                        _runtimeLegacyPawnIOPromptShown = true;
                        ShowMessageBox(IsChinese
                                ? "检测到 PawnIO 驱动版本过旧，当前运行状态下不会卸载驱动。\n请重启 LiteMonitor，并在启动时完成驱动升级。"
                                : "An older PawnIO driver was detected. LiteMonitor will not uninstall the driver while running.\nPlease restart LiteMonitor and complete the driver update during startup.",
                            IsChinese ? "需要重启软件" : "Restart LiteMonitor Required",
                            MessageBoxIcon.Information);
                    }

                    if (needFPS && !isFpsValid)
                    {
                        _activeDownloadTask = InstallFpsComponent();
                        return await _activeDownloadTask;
                    }

                    return false;
                }

                if (pawnIOCheck.NeedsInstall && _pawnIORestartRequiredThisSession)
                    return false;

                if (pawnIOCheck.NeedsInstall || (needFPS && !isFpsValid))
                {
                    _activeDownloadTask = pawnIOCheck.NeedsInstall
                        ? InstallDriverPackage(needFPS, pawnIOCheck, DriverInstallMode.Runtime)
                        : InstallFpsComponent();
                    
                    return await _activeDownloadTask;
                }
                
                return true;
            }
            finally
            {
                _downloadLock.Release();
            }
        }

        // ================================================================
        // ★★★ 公共入口 2：PresentMon 检查 (FPS功能调用/启动调用) ★★★
        // ================================================================
        public async Task<bool> CheckAndDownloadPresentMon(bool silent = false)
        {
            return await EnsureComponents(forceFpsCheck: true);
        }

        // ----------------------------------------------------------------
        // 模块化安装流程
        // ----------------------------------------------------------------

        /// <summary>
        /// 流程 A: 仅安装 FPS 组件
        /// </summary>
        private async Task<bool> InstallFpsComponent()
        {
            string targetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "LiteMonitorFPS.exe");
            
            var context = new DownloadContext
            {
                Title = IsChinese ? "检测到缺失FPS组件" : "FPS Monitor Component Missing",
                Description = IsChinese 
                    ? "您开启了FPS监控，但检测到 FPS 监控组件缺失。\n需要安装该组件来支持 FPS 帧率显示功能。\n点击“立即安装”自动获取并安装。"
                    : "FPS Monitor Component Missing.\nLiteMonitor needs to download an additional component to support FPS monitoring.\nClick 'Install' to proceed.",
                Urls = _presentMonUrls,
                SavePath = targetPath,
                ActionButtonText = "Install",
                VersionLabel = IsChinese ? "请安装FPS组件" : "Install FPS Component"
            };

            if (!await ShowDownloadDialog(context)) return false;

            // ★★★ [新增] 下载后校验 ★★★
            if (!IsValidExecutable(targetPath))
            {
                try { File.Delete(targetPath); } catch { }
                ShowMessageBox(IsChinese ? "下载的文件校验失败(大小或格式错误)，可能是下载源失效。" : "Downloaded file verification failed.",
                               IsChinese ? "校验失败" : "Verification Failed", MessageBoxIcon.Error);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 流程 B: 安装完整驱动包 (包含 PawnIO 和可选的 FPS)
        /// </summary>
        private async Task<bool> InstallDriverPackage(bool needFPS, PawnIOCheckResult pawnIOCheck, DriverInstallMode mode)
        {
            string tempZip = Path.Combine(Path.GetTempPath(), $"LiteMonitor_Drivers_{Guid.NewGuid()}.zip");
            string targetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources");

            if (IsValidExecutable(Path.Combine(targetDir, "PawnIO_setup.exe")))
                return await ProcessPawnIOInstallation(targetDir, pawnIOCheck, mode);
            
            try 
            {
                // 1. 准备并下载
                var context = new DownloadContext
                {
                    Title = GetPawnIODialogTitle(pawnIOCheck),
                    Description = GetPawnIODialogDescription(pawnIOCheck),
                    Urls = _driverPackageUrls,
                    SavePath = tempZip,
                    ActionButtonText = "Install",
                    VersionLabel = GetPawnIOVersionLabel(pawnIOCheck)
                };

                if (!await ShowDownloadDialog(context)) return false;

                // 2. 解压
                if (!ExtractZipPackage(tempZip, targetDir)) return false;

                // 3. 安装 PawnIO，FPS 组件已在解压步骤中处理完成
                return await ProcessPawnIOInstallation(targetDir, pawnIOCheck, mode);
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        }

        // ----------------------------------------------------------------
        // 基础服务方法 (原子操作)
        // ----------------------------------------------------------------

        private bool ExtractZipPackage(string zipPath, string targetDir)
        {
            try
            {
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                
                using (ZipArchive archive = ZipFile.OpenRead(zipPath))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string destPath = Path.Combine(targetDir, entry.FullName);
                        
                        // 智能跳过逻辑：FPS 组件如果存在且有效则不覆盖
                        if (entry.Name.Equals("LiteMonitorFPS.exe", StringComparison.OrdinalIgnoreCase) && IsValidExecutable(destPath))
                        {
                            Debug.WriteLine($"[安装程序] 跳过 {entry.Name} (文件存在且有效)");
                            continue; 
                        }

                        // 确保父目录存在
                        string? entryDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(entryDir) && !Directory.Exists(entryDir))
                            Directory.CreateDirectory(entryDir);

                        try
                        {
                            entry.ExtractToFile(destPath, true);
                        }
                        catch (IOException) when (entry.Name.Equals("LiteMonitorFPS.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            // 忽略 FPS 文件锁定错误
                            Debug.WriteLine($"[安装程序] 无法覆盖 {entry.Name} (文件被锁定)。保留现有版本。");
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                ShowMessageBox(IsChinese ? $"驱动包解压失败: {ex.Message}" : $"Failed to extract driver package: {ex.Message}", 
                               IsChinese ? "解压失败" : "Extraction Failed", MessageBoxIcon.Error);
                return false;
            }
        }

        private async Task<bool> ProcessPawnIOInstallation(string searchDir, PawnIOCheckResult pawnIOCheck, DriverInstallMode mode)
        {
            // 查找安装包
            string[] possibleNames = { "PawnIO_setup.exe", "pawnio.exe", "PawnIO.exe" };
            string setupPath = "";
            foreach(var name in possibleNames)
            {
                 string p = Path.Combine(searchDir, name);
                 if(File.Exists(p)) { setupPath = p; break; }
            }

            if (string.IsNullOrEmpty(setupPath)) return false;

            // 校验
            if (!IsValidExecutable(setupPath))
            {
                ShowMessageBox(IsChinese ? "PawnIO 安装包校验失败(文件损坏)。" : "PawnIO installer verification failed.", 
                               IsChinese ? "校验失败" : "Verification Failed", MessageBoxIcon.Error);
                try { File.Delete(setupPath); } catch { }
                return false;
            }

            bool isLegacyUpgrade = IsLegacyPawnIOUpgrade(pawnIOCheck);
            bool legacyUninstalled = false;

            if (isLegacyUpgrade)
            {
                if (mode != DriverInstallMode.StartupBeforeHardware)
                {
                    Debug.WriteLine("[安装程序] 运行中检测到过旧 PawnIO，跳过卸载升级。");
                    return false;
                }

                DriverOperationResult uninstallResult = await PrepareLegacyPawnIOUpgrade(pawnIOCheck).ConfigureAwait(false);
                if (uninstallResult == DriverOperationResult.RestartRequired ||
                    uninstallResult == DriverOperationResult.ServiceMarkedForDelete)
                {
                    _pawnIORestartRequiredThisSession = true;
                    _shouldExitAfterDriverOperation = true;
                    try { File.Delete(setupPath); } catch { }
                    ShowPawnIORestartRequiredMessage(IsChinese
                        ? "旧版 PawnIO 驱动已开始卸载，但 Windows 需要重启后才能继续安装新版驱动。"
                        : "The old PawnIO driver started uninstalling, but Windows needs a restart before installing the new driver.");
                    return false;
                }

                if (uninstallResult != DriverOperationResult.Success)
                {
                    ShowMessageBox(IsChinese
                            ? "旧版 PawnIO 驱动自动卸载失败，本次不会继续安装新版驱动。\n请重启电脑后再打开 LiteMonitor 重试。"
                            : "Failed to uninstall the old PawnIO driver. The new driver will not be installed now.\nPlease restart Windows and open LiteMonitor again.",
                        IsChinese ? "驱动更新失败" : "Driver Update Failed",
                        MessageBoxIcon.Warning);
                    return false;
                }

                legacyUninstalled = true;
                await Task.Delay(1500).ConfigureAwait(false);
            }
            else if (mode == DriverInstallMode.Runtime)
            {
                // 安装驱动前先关闭硬件监控树，减少当前进程占用旧驱动句柄的概率。
                _onPrepareDriverInstall?.Invoke();
                await Task.Delay(1000).ConfigureAwait(false);
            }

            // 尝试静默安装
            DriverOperationResult installResult = await RunPawnIOInstaller(setupPath, silent: true);
            
            if (installResult == DriverOperationResult.RestartRequired)
            {
                _pawnIORestartRequiredThisSession = true;
                if (mode == DriverInstallMode.StartupBeforeHardware)
                    _shouldExitAfterDriverOperation = true;
                try { File.Delete(setupPath); } catch { }
                ShowPawnIORestartRequiredMessage(IsChinese
                    ? "PawnIO 驱动更新已完成，但需要重启电脑后才能生效。"
                    : "PawnIO driver update completed, but Windows needs a restart before it takes effect.");
                return true;
            }

            if (installResult == DriverOperationResult.ServiceMarkedForDelete)
            {
                _pawnIORestartRequiredThisSession = true;
                if (mode == DriverInstallMode.StartupBeforeHardware)
                    _shouldExitAfterDriverOperation = true;
                ShowPawnIORestartRequiredMessage(IsChinese
                    ? "PawnIO 驱动仍处于上一次更新未完成的状态，Windows 需要重启后才能继续安装新版驱动。"
                    : "PawnIO is still in a pending driver update state. Windows needs a restart before installing the new driver.");
                return false;
            }

            if (installResult == DriverOperationResult.AlreadyExists)
            {
                if (legacyUninstalled || mode == DriverInstallMode.StartupBeforeHardware)
                {
                    _pawnIORestartRequiredThisSession = true;
                    _shouldExitAfterDriverOperation = true;
                    ShowPawnIORestartRequiredMessage(IsChinese
                        ? "旧版 PawnIO 驱动仍被 Windows 保留，暂时无法覆盖安装新版驱动。"
                        : "The old PawnIO driver is still retained by Windows, so the new driver cannot be installed yet.");
                }
                else
                {
                    ShowMessageBox(IsChinese
                            ? "检测到旧版 PawnIO 驱动仍存在，当前运行状态下无法覆盖升级。\n请重启软件，让 LiteMonitor 在启动前完成驱动升级。"
                            : "An older PawnIO driver already exists and cannot be upgraded while LiteMonitor is running.\nPlease restart LiteMonitor so the driver can be updated before hardware monitoring starts.",
                        IsChinese ? "需要重新启动软件" : "Restart LiteMonitor Required",
                        MessageBoxIcon.Warning);
                }

                return false;
            }

            if (installResult == DriverOperationResult.Failed)
            {
                if (legacyUninstalled)
                {
                    _shouldExitAfterDriverOperation = true;
                    ShowMessageBox(IsChinese
                            ? "旧版 PawnIO 驱动已卸载，但新版驱动安装失败。\n请重启电脑后再打开 LiteMonitor 重试。"
                            : "The old PawnIO driver was uninstalled, but the new driver installation failed.\nPlease restart Windows and open LiteMonitor again.",
                        IsChinese ? "驱动更新未完成" : "Driver Update Incomplete",
                        MessageBoxIcon.Warning);
                    return false;
                }

                // 失败处理：引导手动安装
                string msg = IsChinese
                    ? "PawnIO 驱动自动安装失败。\n是否立即启动手动安装？"
                    : "PawnIO driver installation failed.\nLaunch manual installation now?";
                
                if (MessageBox.Show(msg, IsChinese ? "需要协助" : "Help", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    DriverOperationResult manualResult = await RunPawnIOInstaller(setupPath, silent: false);
                    
                    if (manualResult == DriverOperationResult.RestartRequired)
                    {
                        _pawnIORestartRequiredThisSession = true;
                        if (mode == DriverInstallMode.StartupBeforeHardware)
                            _shouldExitAfterDriverOperation = true;
                        try { File.Delete(setupPath); } catch { }
                        ShowPawnIORestartRequiredMessage(IsChinese
                            ? "PawnIO 驱动安装已完成，但需要重启电脑后才能生效。"
                            : "PawnIO driver installation completed, but Windows needs a restart before it takes effect.");
                        return true;
                    }

                    if (manualResult == DriverOperationResult.ServiceMarkedForDelete)
                    {
                        _pawnIORestartRequiredThisSession = true;
                        if (mode == DriverInstallMode.StartupBeforeHardware)
                            _shouldExitAfterDriverOperation = true;
                        ShowPawnIORestartRequiredMessage(IsChinese
                            ? "PawnIO 驱动仍处于上一次更新未完成的状态，Windows 需要重启后才能继续安装新版驱动。"
                            : "PawnIO is still in a pending driver update state. Windows needs a restart before installing the new driver.");
                        return false;
                    }

                    if (manualResult == DriverOperationResult.Success || IsPawnIOReadyAfterInstall())
                    {
                        ShowMessageBox(IsChinese ? "PawnIO 驱动已安装。请重启软件。" : "PawnIO driver installed. Please restart.", 
                                       IsChinese ? "安装成功" : "Success", MessageBoxIcon.Information);
                        try { File.Delete(setupPath); } catch { }
                        _onReloadRequired?.Invoke();
                        return true;
                    }
                    else
                    {
                        ShowMessageBox(IsChinese ? "未检测到驱动安装。请重启尝试。" : "Driver not detected. Please restart.", 
                                       IsChinese ? "未完成" : "Not Completed", MessageBoxIcon.Warning);
                    }
                }

                _onReloadRequired?.Invoke();
                return false;
            }
            else
            {
                // 成功处理
                try { File.Delete(setupPath); } catch { }
                ShowMessageBox(IsChinese ? "PawnIO 驱动安装成功！\n 稍后软件将自动恢复CPU温度等数据监控" : "PawnIO driver installed!\nCPU temperature monitoring will be restored automatically.", 
                               IsChinese ? "成功" : "Success", MessageBoxIcon.Information);
                _onReloadRequired?.Invoke();
                return true;
            }
        }

        private bool IsPawnIORequiredByConfig()
        {
            return _cfg.IsAnyEnabled("CPU");
        }

        private bool IsLegacyPawnIOUpgrade(PawnIOCheckResult pawnIOCheck)
        {
            return pawnIOCheck.Status == PawnIOCheckStatus.Outdated &&
                   pawnIOCheck.Installation.Version != null &&
                   pawnIOCheck.Installation.Version < SafeInPlaceUpgradeVersion;
        }

        private PawnIOCheckResult CheckPawnIORequirement()
        {
            var info = GetPawnIOInstallationInfo();
            if (!IsPawnIORequiredByConfig())
                return new PawnIOCheckResult(PawnIOCheckStatus.NotRequired, info);

            if (!info.Installed)
                return new PawnIOCheckResult(PawnIOCheckStatus.Missing, info);

            if (info.Version != null && info.Version < RequiredPawnIOVersion)
                return new PawnIOCheckResult(PawnIOCheckStatus.Outdated, info);

            return new PawnIOCheckResult(PawnIOCheckStatus.Ready, info);
        }

        private PawnIOInstallationInfo GetPawnIOInstallationInfo()
        {
            bool installed = false;
            string? firstVersionText = null;

            try
            {
                string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(keyPath);
                    if (key == null) continue;

                    installed = true;
                    string? versionText = key.GetValue("DisplayVersion") as string;
                    string? quietUninstall = key.GetValue("QuietUninstallString") as string;
                    string? uninstall = key.GetValue("UninstallString") as string;
                    if (string.IsNullOrWhiteSpace(firstVersionText)) firstVersionText = versionText;

                    if (TryParseVersion(versionText, out var version))
                        return new PawnIOInstallationInfo(true, version, versionText, quietUninstall, uninstall);
                }
            }
            catch { }

            return new PawnIOInstallationInfo(installed, null, firstVersionText, null, null);
        }

        private bool IsPawnIOReadyAfterInstall()
        {
            var info = GetPawnIOInstallationInfo();
            if (!info.Installed) return false;

            // 版本号缺失时沿用旧逻辑，避免把已安装驱动误判为失败。
            return info.Version == null || info.Version >= RequiredPawnIOVersion;
        }

        private bool TryParseVersion(string? versionText, out Version? version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(versionText)) return false;
            return Version.TryParse(versionText.Trim(), out version);
        }

        private async Task<DriverOperationResult> PrepareLegacyPawnIOUpgrade(PawnIOCheckResult pawnIOCheck)
        {
            string? uninstallCommand = pawnIOCheck.Installation.QuietUninstallString ?? pawnIOCheck.Installation.UninstallString;
            if (string.IsNullOrWhiteSpace(uninstallCommand)) return DriverOperationResult.Failed;

            return await RunPawnIOUninstaller(uninstallCommand).ConfigureAwait(false);
        }

        private async Task<DriverOperationResult> RunPawnIOUninstaller(string commandLine)
        {
            if (!TrySplitCommandLine(commandLine, out string fileName, out string arguments))
                return DriverOperationResult.Failed;

            if (!arguments.Contains("-silent", StringComparison.OrdinalIgnoreCase))
                arguments = string.IsNullOrWhiteSpace(arguments) ? "-silent" : $"{arguments} -silent";

            return await RunPawnIOProcess(fileName, arguments, silent: true, checkReadyAfterExit: false).ConfigureAwait(false);
        }

        private static bool TrySplitCommandLine(string commandLine, out string fileName, out string arguments)
        {
            fileName = "";
            arguments = "";
            commandLine = commandLine.Trim();
            if (string.IsNullOrWhiteSpace(commandLine)) return false;

            if (commandLine[0] == '"')
            {
                int endQuote = commandLine.IndexOf('"', 1);
                if (endQuote <= 1) return false;

                fileName = commandLine.Substring(1, endQuote - 1);
                arguments = commandLine.Substring(endQuote + 1).Trim();
                return !string.IsNullOrWhiteSpace(fileName);
            }

            int firstSpace = commandLine.IndexOf(' ');
            if (firstSpace < 0)
            {
                fileName = commandLine;
                return true;
            }

            fileName = commandLine.Substring(0, firstSpace);
            arguments = commandLine.Substring(firstSpace + 1).Trim();
            return !string.IsNullOrWhiteSpace(fileName);
        }

        private string GetPawnIODialogTitle(PawnIOCheckResult check)
        {
            if (check.Status == PawnIOCheckStatus.Outdated)
                return IsChinese ? "检测到 PawnIO 驱动版本较旧" : "PawnIO Driver Update Available";

            return IsChinese ? "检测到电脑缺失CPU依赖驱动" : "PawnIO Driver Missing";
        }

        private string GetPawnIODialogDescription(PawnIOCheckResult check)
        {
            if (check.Status == PawnIOCheckStatus.Outdated)
            {
                string currentVersion = GetInstalledPawnIOVersionText(check);
                return IsChinese
                    ? $"当前 PawnIO 驱动版本为 {currentVersion}，建议更新到 {RequiredPawnIOVersion}。\n旧版驱动可能导致 CPU 温度/频率/功耗等数据异常。\n点击“立即安装”自动获取并更新（约3MB）。\n\n安装完成后即可自动恢复CPU监控等功能。"
                    : $"Current PawnIO driver version is {currentVersion}. Please update to {RequiredPawnIOVersion}.\nOlder drivers may cause abnormal CPU temperature, frequency, or power readings.\nClick 'Install' to update.";
            }

            return IsChinese
                ? "电脑缺失CPU的PawnIO驱动\n软件将无法获取CPU的温度/频率/功耗等数据 \n点击“立即安装”自动获取并安装（约3MB）\n\n安装完成后即可自动恢复CPU监控等功能。"
                : "LiteMonitor needs to install the driver to monitor CPU temperature, frequency, and power consumption.\nClick 'Install' to install.";
        }

        private string GetPawnIOVersionLabel(PawnIOCheckResult check)
        {
            if (check.Status == PawnIOCheckStatus.Outdated)
                return IsChinese ? $"更新 PawnIO 驱动至 {RequiredPawnIOVersion}" : $"Update PawnIO Driver to {RequiredPawnIOVersion}";

            return IsChinese ? "请安装PawnIO驱动" : "Install PawnIO Driver";
        }

        private string GetInstalledPawnIOVersionText(PawnIOCheckResult check)
        {
            if (!string.IsNullOrWhiteSpace(check.Installation.VersionText)) return check.Installation.VersionText!;
            if (check.Installation.Version != null) return check.Installation.Version.ToString();
            return IsChinese ? "未知" : "Unknown";
        }

        private bool IsValidExecutable(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                var info = new FileInfo(path);
                if (info.Length < 300 * 1024) return false;
                
                // 简单的 PE 头检查 "MZ"
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < 64) return false;
                var buffer = new byte[2];
                fs.Read(buffer, 0, 2);
                return buffer[0] == 0x4D && buffer[1] == 0x5A;
            }
            catch { return false; }
        }

        private async Task<DriverOperationResult> RunPawnIOInstaller(string installerPath, bool silent = true)
        {
            string arguments = silent ? "-install -silent" : "";
            return await RunPawnIOProcess(installerPath, arguments, silent, checkReadyAfterExit: true);
        }

        private async Task<DriverOperationResult> RunPawnIOProcess(string fileName, string arguments, bool silent, bool checkReadyAfterExit)
        {
            int maxRetries = 3;
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = silent ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
                    };

                    var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        await proc.WaitForExitAsync();
                        Debug.WriteLine($"[安装程序] {Path.GetFileName(fileName)} 退出码: {proc.ExitCode}");

                        if (silent && proc.ExitCode == ErrorAlreadyExists)
                            return DriverOperationResult.AlreadyExists;
                        
                        if (silent && proc.ExitCode == ErrorSuccessRebootRequired)
                            return DriverOperationResult.RestartRequired;

                        if (silent && proc.ExitCode == ErrorServiceMarkedForDelete)
                            return DriverOperationResult.ServiceMarkedForDelete;

                        // 1. 优先判断 ExitCode。新版 PawnIO CLI 返回 DOS 错误码，0 表示操作成功。
                        if (silent && proc.ExitCode == 0 && !checkReadyAfterExit)
                            return DriverOperationResult.Success;

                        if (silent && proc.ExitCode == 0 && IsPawnIOReadyAfterInstall())
                            return DriverOperationResult.Success;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[安装程序] 第 {i + 1} 次尝试失败: {ex.Message}");
                }

                // 2. 等待 2 秒后检查注册表 (解决安装延迟或ExitCode不准的问题)
                await Task.Delay(2000);
                if (checkReadyAfterExit && IsPawnIOReadyAfterInstall())
                {
                    Debug.WriteLine("[安装程序] 通过注册表验证安装成功。");
                    return DriverOperationResult.Success;
                }
                
                // 如果是手动模式，只尝试一次，不进行循环重试（用户关闭窗口算结束）
                if (!silent) break;

                // 准备重试
                if (i < maxRetries - 1)
                {
                    Debug.WriteLine("[安装程序] 正在重试安装...");
                }
            }
            return DriverOperationResult.Failed;
        }

        private void ShowPawnIORestartRequiredMessage(string description)
        {
            ShowMessageBox(description + (IsChinese
                    ? "\n请重启电脑后再打开 LiteMonitor。"
                    : "\nPlease restart Windows before opening LiteMonitor again."),
                IsChinese ? "需要重启" : "Restart Required",
                MessageBoxIcon.Information);
        }

        private enum DriverOperationResult
        {
            Failed,
            Success,
            AlreadyExists,
            RestartRequired,
            ServiceMarkedForDelete
        }

        private enum DriverInstallMode
        {
            Runtime,
            StartupBeforeHardware
        }

        private enum PawnIOCheckStatus
        {
            NotRequired,
            Missing,
            Outdated,
            Ready
        }

        private sealed class PawnIOCheckResult
        {
            public PawnIOCheckResult(PawnIOCheckStatus status, PawnIOInstallationInfo installation)
            {
                Status = status;
                Installation = installation;
            }

            public PawnIOCheckStatus Status { get; }
            public PawnIOInstallationInfo Installation { get; }
            public bool NeedsInstall => Status == PawnIOCheckStatus.Missing || Status == PawnIOCheckStatus.Outdated;
        }

        private sealed class PawnIOInstallationInfo
        {
            public PawnIOInstallationInfo(bool installed, Version? version, string? versionText, string? quietUninstallString, string? uninstallString)
            {
                Installed = installed;
                Version = version;
                VersionText = versionText;
                QuietUninstallString = quietUninstallString;
                UninstallString = uninstallString;
            }

            public bool Installed { get; }
            public Version? Version { get; }
            public string? VersionText { get; }
            public string? QuietUninstallString { get; }
            public string? UninstallString { get; }
        }

        private void ShowMessageBox(string msg, string title, MessageBoxIcon icon)
        {
            MessageBox.Show(msg, title, MessageBoxButtons.OK, icon);
        }

        /// <summary>
        /// 在 UI 线程显示下载对话框
        /// </summary>
        private Task<bool> ShowDownloadDialog(DownloadContext context)
        {
            var tcs = new TaskCompletionSource<bool>();

            void Show()
            {
                try
                {
                    using var dlg = new UpdateDialog(context, _cfg);
                    var result = dlg.ShowDialog();
                    tcs.TrySetResult(result == DialogResult.OK);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }

            // ... (STA 线程处理逻辑保持不变，只需调用新的 Show)
            if (Application.OpenForms.Count > 0)
            {
                var form = Application.OpenForms[0];
                if (form != null && !form.IsDisposed && form.IsHandleCreated)
                {
                    if (form.InvokeRequired) form.Invoke(new Action(Show));
                    else Show();
                    return tcs.Task;
                }
            }
            
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                Show();
            }
            else
            {
                var thread = new Thread(() => Show());
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
            }

            return tcs.Task;
        }


    }
}
