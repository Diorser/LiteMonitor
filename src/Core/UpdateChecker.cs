using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiteMonitor
{
    public static class UpdateChecker
    {
        // 版本号文件建议放在 GitHub 仓库根目录
        private const string VersionUrl = "https://raw.githubusercontent.com/Diorser/LiteMonitor/master/resources/version.json";
        private const string ReleasePage = "https://github.com/Diorser/LiteMonitor/releases";

        public static async Task CheckForUpdatesAsync(bool showWhenUpToDate = true)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                string json = await http.GetStringAsync(VersionUrl);
                using var doc = JsonDocument.Parse(json);
                string? latest = doc.RootElement.GetProperty("version").GetString();

                string current = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
                if (IsNewer(latest, current))
                {
                    if (MessageBox.Show(
                        $"发现新版本：{latest}\n当前版本：{current}\n是否前往下载？",
                        "LiteMonitor 更新",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information) == DialogResult.Yes)
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ReleasePage)
                        {
                            UseShellExecute = true
                        });
                    }
                }
                else if (showWhenUpToDate)
                {
                    MessageBox.Show($"当前已是最新版本 ({current})。", "LiteMonitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                if (showWhenUpToDate)
                    MessageBox.Show($"检查更新失败：{ex.Message}", "LiteMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static bool IsNewer(string? latest, string current)
        {
            if (string.IsNullOrWhiteSpace(latest)) return false;
            try
            {
                Version v1 = new Version(latest);
                Version v2 = new Version(current);
                return v1 > v2;
            }
            catch { return false; }
        }
    }
}
