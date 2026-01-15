using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers; // 引入头文件处理
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Security.Principal;
using System.Net.Security; // For SslClientAuthenticationOptions
using LiteMonitor.src.Core; // 修复: 引用 Settings 类

namespace LiteMonitor
{
    public class DownloadContext
    {
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string[] Urls { get; set; } = Array.Empty<string>();
        public string SavePath { get; set; } = "";
        
        // UI 配置
        public string VersionLabel { get; set; } = "";
        public string ActionButtonText { get; set; } = "Install";
        public bool AutoExitOnSuccess { get; set; } = false; // 更新模式下为 true
    }

    public partial class UpdateDialog : Form
    {
        private readonly List<string> _sortedUrls;
        private readonly DownloadContext _context;
        private readonly Settings _settings;
        
        private CancellationTokenSource? _cts;
        private Stopwatch _speedWatch;
        private bool IsChinese => _settings?.Language?.ToLower() == "zh";

        public UpdateDialog(DownloadContext context, Settings settings)
        {
            InitializeComponent();
            this.StartPosition = FormStartPosition.CenterScreen;
            this.TopMost = true;
            
            _context = context;
            _settings = settings;
            _sortedUrls = new List<string>(_context.Urls);

            // 初始化 UI
            this.Text = IsChinese ? $"⚡️LiteMonitor - {_context.Title}" : $"⚡️LiteMonitor - {_context.Title}";
            lblVersion.Text = _context.VersionLabel;
            
            // 富文本设置 (必须在设置文本之前/之间正确处理)
            rtbChangelog.Text = _context.Description;
            rtbChangelog.SelectAll();
            rtbChangelog.SelectionIndent = 6; // 减小左缩进 (原为10)
            rtbChangelog.SelectionRightIndent = 5; // 减小右缩进 (原为10)
            rtbChangelog.DeselectAll();
            rtbChangelog.SelectionStart = 0;
            
            rtbChangelog.DetectUrls = true;
            rtbChangelog.LinkClicked += RtbChangelog_LinkClicked;
            // 强制显示垂直滚动条
            rtbChangelog.ScrollBars = RichTextBoxScrollBars.Vertical; 
            
            // 移除动态居中逻辑，恢复 Designer 中预设的"靠右"布局
            // rtbChangelog.Width = ... 
            // rtbChangelog.Left = ...
            
            // 设置按钮文本
            btnUpdate.Text = IsChinese ? 
                (_context.ActionButtonText == "Update" ? "立即更新" : "立即安装") : 
                _context.ActionButtonText;
                
            label1.Text = _context.Title; // 修复: 直接使用 Context 中的标题，避免英文环境下被错误覆盖
            btnCancel.Text = IsChinese ? "取消" : "Cancel";
            lblStatus.Text = IsChinese ? "准备就绪" : "Ready";

            // 防止自动聚焦文本框 (将焦点设为按钮)
            this.ActiveControl = btnUpdate;

            // 启动测速
            if (_context.Urls.Length > 1) Task.Run(TestMirrors);
        }

        private void RtbChangelog_LinkClicked(object? sender, LinkClickedEventArgs e)
        {
            try
            {
                // 使用默认浏览器打开链接
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.LinkText,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                string title = IsChinese ? "错误" : "Error";
                string msg = IsChinese ? $"无法打开链接：{e.LinkText}\n错误：{ex.Message}" : $"Cannot open link: {e.LinkText}\nError: {ex.Message}";
                
                MessageBox.Show(msg, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            // 如果正在下载，则触发取消
            if (_cts != null)
            {
                _cts.Cancel();
            }
            else
            {
                this.Close();
            }
        }

        //防止窗口在下载时被强制关闭
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_cts != null && e.CloseReason == CloseReason.UserClosing)
            {
                string msg = IsChinese ? "正在下载更新，确定要取消吗？" : "Download in progress. Are you sure you want to cancel?";
                string title = IsChinese ? "提示" : "Confirmation";
                
                if (MessageBox.Show(msg, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No)
                {
                    e.Cancel = true;
                    return;
                }
                _cts.Cancel();
            }
            base.OnFormClosing(e);
        }

        private async Task TestMirrors()
        {
            if (_context.Urls.Length <= 1) return;

            try
            {
                var tasks = _context.Urls.Select(async url =>
                {
                    try
                    {
                        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                        var sw = Stopwatch.StartNew();
                        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                        sw.Stop();
                        return response.IsSuccessStatusCode ? (url, sw.ElapsedMilliseconds) : (url, long.MaxValue);
                    }
                    catch
                    {
                        return (url, long.MaxValue);
                    }
                });

                var results = await Task.WhenAll(tasks);
                
                var sorted = results
                    .Where(x => x.Item2 != long.MaxValue)
                    .OrderBy(x => x.Item2)
                    .Select(x => x.url)
                    .ToList();

                if (sorted.Count > 0)
                {
                    lock (_sortedUrls)
                    {
                        _sortedUrls.Clear();
                        _sortedUrls.AddRange(sorted);
                        foreach (var url in _context.Urls)
                        {
                            if (!_sortedUrls.Contains(url)) _sortedUrls.Add(url);
                        }
                    }
                    Debug.WriteLine($"[更新弹窗] 镜像源排序完成: {string.Join(", ", _sortedUrls)}");
                }
            }
            catch { }
        }

        private async void btnUpdate_Click(object sender, EventArgs e)
        {
            btnUpdate.Enabled = false;
            btnCancel.Enabled = true; // 允许取消
            
            _cts = new CancellationTokenSource();
            _speedWatch = Stopwatch.StartNew();
            
            // 进度条模式切换
            progress.Style = ProgressBarStyle.Continuous;
            progress.Value = 0;

            bool downloadSuccess = false;
            Exception? lastError = null;

            // 获取当前的 URL 列表
            List<string> downloadList;
            lock (_sortedUrls)
            {
                downloadList = new List<string>(_sortedUrls);
            }

            try
            {
                // 1. 尝试所有镜像源
                foreach (var url in downloadList)
                {
                    if (_cts.IsCancellationRequested) break;

                    try
                    {
                        lblStatus.Text = _settings?.Language?.ToLower() == "zh" 
                            ? $"正在连接下载服务器..." 
                            : $"Connecting to server...";

                        // 4. 配置 HttpClient (支持 SSL Bypass 和超时)
                        var handler = new SocketsHttpHandler
                        {
                            SslOptions = new SslClientAuthenticationOptions
                            {
                                RemoteCertificateValidationCallback = delegate { return true; }
                            }
                        };

                        using var http = new HttpClient(handler)
                        { 
                            Timeout = TimeSpan.FromMinutes(10) 
                        };
                        http.DefaultRequestHeaders.UserAgent.ParseAdd("LiteMonitor-Updater/1.0");

                        // 5. 发起请求
                        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                        
                        if (!resp.IsSuccessStatusCode)
                        {
                            throw new HttpRequestException($"HTTP Error: {resp.StatusCode}");
                        }

                        long totalBytes = resp.Content.Headers.ContentLength ?? -1;
                        
                        // 6. 流式下载
                        using (var fs = new FileStream(_context.SavePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                        using (var stream = await resp.Content.ReadAsStreamAsync(_cts.Token))
                        {
                            byte[] buffer = new byte[81920]; 
                            long totalRead = 0;
                            int bytesRead;
                            
                            long lastSpeedCheckBytes = 0;
                            long lastSpeedCheckTime = 0;

                            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token)) > 0)
                            {
                                await fs.WriteAsync(buffer, 0, bytesRead, _cts.Token);
                                totalRead += bytesRead;

                                // 更新进度条
                                if (totalBytes > 0)
                                {
                                    int progressPct = (int)((double)totalRead / totalBytes * 100);
                                    if (progress.Value != progressPct) progress.Value = progressPct;
                                }
                                else
                                {
                                    if (progress.Style != ProgressBarStyle.Marquee) 
                                        progress.Style = ProgressBarStyle.Marquee;
                                }

                                // 更新状态文本
                                long now = _speedWatch.ElapsedMilliseconds;
                                if (now - lastSpeedCheckTime > 200)
                                {
                                    double speed = (totalRead - lastSpeedCheckBytes) / 1024.0 / 1024.0 / ((now - lastSpeedCheckTime) / 1000.0);
                                    string speedStr = speed.ToString("F1");
                                    string downloadedStr = (totalRead / 1024.0 / 1024.0).ToString("F1");
                                    string totalStr = totalBytes > 0 ? (totalBytes / 1024.0 / 1024.0).ToString("F1") : "??";

                                    lblStatus.Text = $"{downloadedStr}MB / {totalStr}MB  -  {speedStr} MB/s";
                                    lastSpeedCheckBytes = totalRead;
                                    lastSpeedCheckTime = now;
                                }
                            }
                        }
                        
                        // 下载成功
                        downloadSuccess = true;
                        break; 
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        if (ex is OperationCanceledException) throw;
                        lblStatus.Text = _settings?.Language?.ToLower() == "zh" ? "切换下载源..." : "Switching mirror...";
                        continue; 
                    }
                }

                if (!downloadSuccess)
                {
                     throw lastError ?? new Exception("Download failed from all mirrors.");
                }

                // 7. 下载完成
                // ★★★ Fix: Explicitly clear CTS so OnFormClosing doesn't prompt user ★★★
                var ctsToDispose = _cts;
                _cts = null; 
                ctsToDispose?.Dispose();

                if (_context.AutoExitOnSuccess)
                {
                    StartUpdater();
                }
                else
                {
                    // 组件下载模式：直接返回成功，由调用方负责后续安装逻辑
                    this.DialogResult = DialogResult.OK;
                    this.Close();
                }
            }
            catch (OperationCanceledException)
            {
                // 用户取消，清理残留文件
                CleanupPartialFile();
                bool isZh = _settings?.Language?.ToLower() == "zh";
                lblStatus.Text = isZh ? "已取消下载" : "Download Canceled";
                btnUpdate.Enabled = true;
                btnUpdate.Text = IsChinese ? 
                    (_context.ActionButtonText == "Update" ? "立即更新" : "立即安装") : 
                    _context.ActionButtonText;
                btnCancel.Enabled = true; // 确保取消按钮可用
                btnCancel.Text = isZh ? "关闭" : "Close";
                progress.Value = 0;
                progress.Style = ProgressBarStyle.Blocks;
            }
            catch (Exception ex)
            {
                CleanupPartialFile();
                bool isZh = _settings?.Language?.ToLower() == "zh";
                string errTitle = isZh ? "错误" : "Error";
                string errMsg = isZh ? $"下载失败：\n{ex.Message}" : $"Download failed:\n{ex.Message}";
                
                MessageBox.Show(errMsg, errTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                
                // 重置 UI
                btnUpdate.Enabled = true;
                btnUpdate.Text = isZh ? "重试" : "Retry";
                btnCancel.Enabled = true;
                btnCancel.Text = isZh ? "关闭" : "Close"; // 失败时也应显示"关闭"
                progress.Value = 0;
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void StartUpdater()
        {
            string updater = Path.Combine(AppContext.BaseDirectory, "resources", "Updater.exe");

            if (!File.Exists(updater))
            {
                bool isZh = _settings?.Language?.ToLower() == "zh";
                string msg = isZh ? $"找不到更新程序：\n{updater}\n请尝试重新安装软件。" : $"Updater not found:\n{updater}\nPlease reinstall the software.";
                string title = isZh ? "文件丢失" : "File Missing";
                
                MessageBox.Show(msg, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // ★★★ 新增：主程序抢先更新 Updater.exe ★★★
            // 防止 Updater 运行时无法自我覆盖
            UpdateChecker.PreUpdateUpdater(_context.SavePath);

            var psi = new ProcessStartInfo
            {
                FileName = updater,
                Arguments = $"\"{_context.SavePath}\"", // 传递 ZIP 路径
                WorkingDirectory = AppContext.BaseDirectory // 显式指定工作目录
            };

            // ★★★ 智能提权逻辑 ★★★
            if (IsRunningAsAdmin())
            {
                // 已经是管理员，直接继承权限，无需再弹窗
                psi.UseShellExecute = false; 
            }
            else
            {
                // 普通用户，申请提权
                psi.UseShellExecute = true;
                psi.Verb = "runas"; 
            }

            try
            {
                Process.Start(psi);
                Application.Exit(); // 启动成功，退出主程序
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ★★★ 捕获 Error 1223: 操作已被用户取消 ★★★
                // 用户在 UAC 弹窗点了“否”，我们要静默处理，不要弹错误窗
                // 恢复界面状态，允许用户再次点击
                bool isZh = _settings?.Language?.ToLower() == "zh";
                btnUpdate.Enabled = true;
                btnUpdate.Text = isZh ? "立即更新" : "Update";
            }
            catch (Exception ex)
            {
                bool isZh = _settings?.Language?.ToLower() == "zh";
                string msg = isZh ? $"启动更新程序失败：\n{ex.Message}" : $"Failed to start updater:\n{ex.Message}";
                string title = isZh ? "错误" : "Error";
                
                MessageBox.Show(msg, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                // 恢复按钮状态
                btnUpdate.Enabled = true;
                btnUpdate.Text = isZh ? "重试" : "Retry";
            }
        }

        // 辅助方法：检查当前是否为管理员
        private bool IsRunningAsAdmin()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private void CleanupPartialFile()
        {
            try
            {
                if (File.Exists(_context.SavePath))
                    File.Delete(_context.SavePath);
            }
            catch { /* 忽略清理错误 */ }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            CenterTitles();
        }

        private void CenterTitles()
        {
            // 确保控件已创建
            if (label1 != null)
            {
                // 计算水平居中位置: (窗口宽度 - 控件宽度) / 2
                label1.Left = (this.ClientSize.Width - label1.Width) / 2;
            }

            if (lblVersion != null)
            {
                lblVersion.Left = (this.ClientSize.Width - lblVersion.Width) / 2;
            }
        }
    }
}