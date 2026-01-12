using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers; // 引入头文件处理
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Security.Principal;

namespace LiteMonitor
{
    public partial class UpdateDialog : Form
    {
        private readonly string _url;
        private readonly string _version;
        private readonly string _targetZipPath;
        private readonly Settings _settings;
        
        // 用于支持取消操作
        private CancellationTokenSource? _cts;
        
        // 记录开始时间用于计算速度
        private Stopwatch _speedWatch;

        public UpdateDialog(string version, string changelog, string releaseDate, string downloadUrl, Settings settings)
        {
            InitializeComponent();

            _version = version;
            _url = downloadUrl;
            _settings = settings;
            
            // 规范路径处理
            _targetZipPath = Path.Combine(AppContext.BaseDirectory, "resources", "update.zip");

            // 根据语言设置初始化界面文本
            InitializeLanguage();

            // UI 初始化
            lblVersion.Text = $"⚡️LiteMonitor_v{version}";

            // 设置RichTextBox的padding效果
            rtbChangelog.SelectionIndent = 10;  // 左侧padding
            rtbChangelog.SelectionRightIndent = 10;  // 右侧padding
            
            // 启用链接检测
            rtbChangelog.DetectUrls = true;
            rtbChangelog.LinkClicked += RtbChangelog_LinkClicked;
            
            // 更新日志内容保持不变，只改界面语言
            rtbChangelog.Text = $"更新日志：\n{changelog} \n更新日期：\n{releaseDate}\n\n官网：https://litemonitor.cn \nGitHub：https://github.com/Diorser/LiteMonitor";
            
            // 设置窗口为置顶
            this.TopMost = true;
        }

        private void InitializeLanguage()
        {
            bool isChinese = _settings?.Language?.ToLower() == "zh";
            
            if (isChinese)
            {
                // 中文界面
                this.Text = "⚡️LiteMonitor 更新";
                label1.Text = "发现新版本！";
                btnUpdate.Text = "立即更新";
                btnCancel.Text = "取消";
                lblStatus.Text = "准备就绪";
            }
            else
            {
                // 英文界面
                this.Text = "⚡️LiteMonitor Update";
                label1.Text = "New Version!";
                btnUpdate.Text = "Update";
                btnCancel.Text = "Cancel";
                lblStatus.Text = "Ready";
            }
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
                MessageBox.Show($"无法打开链接：{e.LinkText}\n错误：{ex.Message}", "错误", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                if (MessageBox.Show("正在下载更新，确定要取消吗？", "提示", 
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No)
                {
                    e.Cancel = true;
                    return;
                }
                _cts.Cancel();
            }
            base.OnFormClosing(e);
        }

        private async void btnUpdate_Click(object sender, EventArgs e)
        {
            // 1. UI 状态锁定
            btnUpdate.Enabled = false;
            btnUpdate.Text = "下载中...";
            btnCancel.Text = "取消"; // 将“关闭”改为“取消”
            
            // 2. 初始化取消令牌
            _cts = new CancellationTokenSource();
            _speedWatch = Stopwatch.StartNew();

            try
            {
                // 3. 确保目录存在
                string? dir = Path.GetDirectoryName(_targetZipPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // 4. 配置 HttpClient (专业点：设置超时和 UserAgent)
                using var http = new HttpClient 
                { 
                    Timeout = TimeSpan.FromMinutes(10) // 设置较长超时防止大文件中断
                };
                // 某些 CDN 拒绝没有 User-Agent 的请求
                http.DefaultRequestHeaders.UserAgent.ParseAdd("LiteMonitor-Updater/1.0");

                // 5. 发起请求 (支持取消)
                using var resp = await http.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                
                if (!resp.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"服务器返回错误: {resp.StatusCode}");
                }

                long totalBytes = resp.Content.Headers.ContentLength ?? -1;
                
                // 6. 流式下载与进度报告
                using (var fs = new FileStream(_targetZipPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var stream = await resp.Content.ReadAsStreamAsync(_cts.Token))
                {
                    byte[] buffer = new byte[81920]; // 增大缓冲区到 80KB 提升 I/O 性能
                    long totalRead = 0;
                    int bytesRead;
                    
                    // 用于平滑速度计算
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
                            // 避免频繁刷新 UI 导致卡顿
                            if (progress.Value != progressPct) progress.Value = progressPct;
                        }
                        else
                        {
                            // 如果服务器没返回总大小，就设为跑马灯模式
                            if (progress.Style != ProgressBarStyle.Marquee) 
                                progress.Style = ProgressBarStyle.Marquee;
                        }

                        // 更新状态文本 (每 200ms 更新一次，避免闪烁)
                        long now = _speedWatch.ElapsedMilliseconds;
                        if (now - lastSpeedCheckTime > 200)
                        {
                            double speed = (totalRead - lastSpeedCheckBytes) / 1024.0 / 1024.0 / ((now - lastSpeedCheckTime) / 1000.0); // MB/s
                            string speedStr = speed.ToString("F1");
                            
                            string downloadedStr = (totalRead / 1024.0 / 1024.0).ToString("F1");
                            string totalStr = totalBytes > 0 ? (totalBytes / 1024.0 / 1024.0).ToString("F1") : "??";

                            // 需要在 Designer 里加一个 Label 叫 lblStatus
                            lblStatus.Text = $"{downloadedStr}MB / {totalStr}MB  -  {speedStr} MB/s";

                            lastSpeedCheckBytes = totalRead;
                            lastSpeedCheckTime = now;
                        }
                    }
                }

                // 7. 下载完成，启动更新
                StartUpdater();
            }
            catch (OperationCanceledException)
            {
                // 用户取消，清理残留文件
                CleanupPartialFile();
                lblStatus.Text = "已取消下载";
                btnUpdate.Enabled = true;
                btnUpdate.Text = "立即更新";
                btnCancel.Text = "关闭";
                progress.Value = 0;
                progress.Style = ProgressBarStyle.Blocks;
            }
            catch (Exception ex)
            {
                CleanupPartialFile();
                MessageBox.Show($"更新下载失败：\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                
                // 重置 UI
                btnUpdate.Enabled = true;
                btnUpdate.Text = "重试";
                btnCancel.Enabled = true;
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
                MessageBox.Show($"找不到更新程序：\n{updater}\n请尝试重新安装软件。", 
                    "文件丢失", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = updater,
                Arguments = $"\"{_targetZipPath}\"", // 传递 ZIP 路径
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
                btnUpdate.Enabled = true;
                btnUpdate.Text = "立即更新";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动更新程序失败：\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                // 恢复按钮状态
                btnUpdate.Enabled = true;
                btnUpdate.Text = "重试";
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
                if (File.Exists(_targetZipPath))
                    File.Delete(_targetZipPath);
            }
            catch { /* 忽略清理错误 */ }
        }
    }
}
