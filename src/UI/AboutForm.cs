using LiteMonitor.src.Core;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace LiteMonitor
{
    public class AboutForm : Form
    {
        public AboutForm()
        {
            // === 基础外观 ===
            Text = "About LiteMonitor";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;   // ✅ 居中在屏幕（不会被主窗体挡住）
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;                                   // ✅ 确保显示在最前
            ClientSize = new Size(360, 240);

            var theme = ThemeManager.Current;
            BackColor = ThemeManager.ParseColor(theme.Color.GroupBackground);

            // === 标题 ===
            var lblTitle = new Label
            {
                Text = "⚡️ LiteMonitor",
                Font = new Font(theme.Font.Family, 14, FontStyle.Bold),
                ForeColor = ThemeManager.ParseColor(theme.Color.TextTitle),
                AutoSize = true,
                Location = new Point(30, 28)
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
                Location = new Point(32, 68),
                AutoSize = true
            };

            // === 简介 ===
            var lblDesc = new Label
            {
                Text = "A lightweight desktop hardware monitor.\n© 2025 Diorser / LiteMonitor Project",
                ForeColor = ThemeManager.ParseColor(theme.Color.TextPrimary),
                Location = new Point(32, 98),
                AutoSize = true
            };

            // === GitHub 链接 ===
            var link = new LinkLabel
            {
                Text = "GitHub: github.com/Diorser/LiteMonitor",
                LinkColor = Color.SkyBlue,
                ActiveLinkColor = Color.LightSkyBlue,
                VisitedLinkColor = Color.DeepSkyBlue,
                Location = new Point(32, 150),
                AutoSize = true
            };
            link.LinkClicked += (_, __) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("https://github.com/Diorser/LiteMonitor")
                    { UseShellExecute = true });
                }
                catch { }
            };

            // === 关闭按钮（扁平风格） ===
            var btnClose = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Size = new Size(70, 30),
                Location = new Point(ClientSize.Width - 90, ClientSize.Height - 45),
                FlatStyle = FlatStyle.Flat,
                BackColor = ThemeManager.ParseColor(theme.Color.BarBackground),
                ForeColor = ThemeManager.ParseColor(theme.Color.TextPrimary),
                Font = new Font(theme.Font.Family, 9.5f, FontStyle.Regular)
            };
            btnClose.FlatAppearance.BorderSize = 0;           // ✅ 移除白边框
            btnClose.FlatAppearance.MouseOverBackColor = ThemeManager.ParseColor(theme.Color.BarLow);

            Controls.AddRange(new Control[] { lblTitle, lblVer, lblDesc, link, btnClose });
        }
    }
}
