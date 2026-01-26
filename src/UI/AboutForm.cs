using LiteMonitor.src.Core;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using System.Net.Http;
using System.Threading.Tasks;

namespace LiteMonitor
{
    public class AboutForm : Form
    {
        public AboutForm()
        {
            // === 1. 语言判断与文案准备 ===
            bool isZh = LanguageManager.CurrentLang == "zh";

            string strTitle = isZh ? "关于 LiteMonitor" : "About LiteMonitor";
            string strDesc = isZh ? "一款开源的轻量级硬件监控软件。\n© 2025 Diorser / LiteMonitor Project" 
                                  : "A lightweight desktop hardware monitor.\n© 2025 Diorser / LiteMonitor Project";
            string strWebPrefix = isZh ? "官网" : "Website";
            string strUpdate = isZh ? "检查更新" : "Update?";
            string strClose = isZh ? "确定" : "OK";
            string strBug = isZh ? "反馈问题" : "Report Bug";

            // === 基础外观 ===
            Text = strTitle;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;   // ✅ 居中在屏幕（不会被主窗体挡住）
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;                                   // ✅ 确保显示在最前
            
            // ★★★ DPI 修复：计算缩放系数并应用 ★★★
            UIUtils.ScaleFactor = this.DeviceDpi / 96f;
            ClientSize = UIUtils.S(new Size(360, 280));

            var theme = ThemeManager.Current;
            BackColor = ThemeManager.ParseColor(theme.Color.Background);

            // === 标题 ===
            var lblTitle = new Label
            {
                Text = "⚡️ LiteMonitor",
                Font = new Font(theme.Font.Family, 14, FontStyle.Bold),
                ForeColor = ThemeManager.ParseColor(theme.Color.TextTitle),
                AutoSize = true,
                Location = new Point(UIUtils.S(30), UIUtils.S(28))
            };

            // === 简洁版本号 ===
            string version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "1.0.2";
            // ✅ 自动清理 Git 哈希后缀（如 1.0+abc123 → 1.0）

            int plus = version.IndexOf('+');
            if (plus > 0) version = version[..plus];

            var lblVer = new Label
            {
                Text = $"Version {version}",
                ForeColor = ThemeManager.ParseColor(theme.Color.TextPrimary),
                Location = new Point(UIUtils.S(32), UIUtils.S(68)),
                AutoSize = true
            };

            // === 简介 ===
            var lblDesc = new Label
            {
                Text = strDesc,
                ForeColor = ThemeManager.ParseColor(theme.Color.TextPrimary),
                Location = new Point(UIUtils.S(32), UIUtils.S(98)),
                AutoSize = true
            };

            // === 官网链接 ===
            var websiteLink = new LinkLabel
            {
                Text = $"{strWebPrefix}: LiteMonitor.cn",
                LinkColor = Color.SkyBlue,
                ActiveLinkColor = Color.LightSkyBlue,
                VisitedLinkColor = Color.DeepSkyBlue,
                Location = new Point(UIUtils.S(32), UIUtils.S(150)),
                AutoSize = true
            };
            websiteLink.LinkClicked += (_, __) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("https://LiteMonitor.cn")
                    { UseShellExecute = true });
                }
                catch { }
            };

            // === GitHub 链接 ===
            var githubLink = new LinkLabel
            {
                Text = "GitHub: github.com/Diorser/LiteMonitor",
                LinkColor = Color.SkyBlue,
                ActiveLinkColor = Color.LightSkyBlue,
                VisitedLinkColor = Color.DeepSkyBlue,
                Location = new Point(UIUtils.S(32), UIUtils.S(175)),
                AutoSize = true
            };
            githubLink.LinkClicked += (_, __) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("https://github.com/Diorser/LiteMonitor")
                    { UseShellExecute = true });
                }
                catch { }
            };

            // === BUG 反馈按钮 (新增) ===
            var btnBug = new Button
            {
                Text = strBug,
                Size = UIUtils.S(new Size(100, 30)),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                // ✅ 与左侧文字(x=32)对齐
                Location = new Point(UIUtils.S(32), UIUtils.S(235)),
                FlatStyle = FlatStyle.Flat,
                BackColor = ThemeManager.ParseColor(theme.Color.BarBackground),
                ForeColor = ThemeManager.ParseColor(theme.Color.TextPrimary),
                Font = new Font(theme.Font.Family, 9.5f, FontStyle.Regular)
            };
            btnBug.FlatAppearance.BorderSize = 0;
            btnBug.FlatAppearance.MouseOverBackColor = ThemeManager.ParseColor(theme.Color.Background);
            btnBug.Click += (_, __) => 
            {
                try
                {
                    Process.Start(new ProcessStartInfo("https://github.com/Diorser/LiteMonitor/issues")
                    { UseShellExecute = true });
                }
                catch { }
            };

            // === 检查更新按钮 ===
            var btnCheckUpdate = new Button
            {
                Text = strUpdate,
                Size = UIUtils.S(new Size(100, 30)),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                // 稍微往右移一点，给 Bug 按钮腾出空间 (原 150 保持不变，刚好合适)
                Location = new Point(UIUtils.S(150), UIUtils.S(235)),
                FlatStyle = FlatStyle.Flat,
                BackColor = ThemeManager.ParseColor(theme.Color.BarBackground),
                ForeColor = ThemeManager.ParseColor(theme.Color.TextPrimary),
                Font = new Font(theme.Font.Family, 9.5f, FontStyle.Regular)
            };
            btnCheckUpdate.FlatAppearance.BorderSize = 0;
            btnCheckUpdate.FlatAppearance.MouseOverBackColor = ThemeManager.ParseColor(theme.Color.Background);
            btnCheckUpdate.Click += async (_, __) => await UpdateChecker.CheckAsync(showMessage: true);

            // === 关闭按钮（扁平风格） ===
            var btnClose = new Button
            {
                Text = strClose,
                DialogResult = DialogResult.OK,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Size = UIUtils.S(new Size(70, 30)),
                Location = new Point(UIUtils.S(270), UIUtils.S(235)),
                FlatStyle = FlatStyle.Flat,
                BackColor = ThemeManager.ParseColor(theme.Color.BarBackground),
                ForeColor = ThemeManager.ParseColor(theme.Color.TextPrimary),
                Font = new Font(theme.Font.Family, 9.5f, FontStyle.Regular)
            };
            btnClose.FlatAppearance.BorderSize = 0;           // ✅ 移除白边框
            btnClose.FlatAppearance.MouseOverBackColor = ThemeManager.ParseColor(theme.Color.Background);

            // 别忘了把新按钮 btnBug 加入集合
            Controls.AddRange([lblTitle, lblVer, lblDesc, websiteLink, githubLink, btnBug, btnCheckUpdate, btnClose]);
        }
    }
}