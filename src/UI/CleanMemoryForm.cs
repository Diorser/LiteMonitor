using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor.src.UI
{
    public class CleanMemoryForm : Form
    {
        private Label _lblTitle;
        private Label _lblPercent;
        private CustomProgressBar _bar; // Reusing CustomProgressBar from SpeedTestForm
        private Theme _currentTheme;
        private Point _dragOffset;
        
        // GDI+ Resources to dispose
        private Font _fontTitle;
        private Font _fontPercent;

        public CleanMemoryForm()
        {
            _currentTheme = ThemeManager.Current;
            
            // 1. 基础窗口设置
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterScreen;
            // ★★★ 修复：使用动态 DPI 缩放计算尺寸 ★★★
            // 基础尺寸：260x160 (96 DPI)
            this.Size = new Size(ScaleDPI(260), ScaleDPI(160));
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.DoubleBuffered = true;
            
            // 使用主题背景色，避免 TransparencyKey 造成的锯齿
            this.BackColor = ThemeManager.ParseColor(_currentTheme.Color.Background);

            // 保持与主界面一致的透明度
            this.Opacity = Settings.Load().Opacity;
            
            // 2. UI 初始化
            InitializeUI();
            
            // 3. 应用圆角
            ApplyRounded();

            // 4. 拖拽支持
            MakeMovable(this);
            foreach (Control c in Controls) MakeMovable(c);
        }

        // ★★★ 新增：DPI 缩放辅助方法 (与 SpeedTestForm 保持一致) ★★★
        private int ScaleDPI(int value)
        {
            using (Graphics g = this.CreateGraphics())
            {
                float dpiScale = g.DpiX / 96f; 
                return (int)(value * dpiScale);
            }
        }

        // 显式释放资源，确保无内存泄漏
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fontTitle?.Dispose();
                _fontPercent?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeUI()
        {
            int padding = ScaleDPI(20);
            int currentY = padding;

            // 预创建字体
            _fontTitle = new Font(_currentTheme.Font.Family, 11f, FontStyle.Bold);
            _fontPercent = new Font(_currentTheme.Font.Family, 28f, FontStyle.Bold);

            // 标题
            _lblTitle = new Label
            {
                Text = LanguageManager.T("Menu.CleanMemory") + "...",
                AutoSize = false,
                Width = this.Width,
                Height = ScaleDPI(24),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = _fontTitle,
                ForeColor = ThemeManager.ParseColor(_currentTheme.Color.TextGroup),
                Top = currentY,
                Left = 0
            };
            this.Controls.Add(_lblTitle);
            currentY += _lblTitle.Height + ScaleDPI(10);

            // 百分比大数字
            _lblPercent = new Label
            {
                Text = "0%",
                AutoSize = false,
                Width = this.Width,
                Height = ScaleDPI(50),
                TextAlign = ContentAlignment.MiddleCenter,
                // 使用超大字体
                Font = _fontPercent,
                ForeColor = ThemeManager.ParseColor(_currentTheme.Color.ValueSafe), // 使用绿色/安全色
                Top = currentY,
                Left = 0
            };
            this.Controls.Add(_lblPercent);
            currentY += _lblPercent.Height + ScaleDPI(15);

            // 进度条
            _bar = new CustomProgressBar
            {
                Width = this.Width - (padding * 3), // 左右留空
                Height = ScaleDPI(6),
                Left = (padding * 3) / 2,
                Top = currentY,
                Maximum = 100,
                Value = 0,
                BackColor = ThemeManager.ParseColor(_currentTheme.Color.BarBackground),
                ForeColor = ThemeManager.ParseColor(_currentTheme.Color.BarLow)
            };
            this.Controls.Add(_bar);
        }

        public async Task StartCleaningAsync()
        {
            try
            {
                this.Show();
                this.Refresh();

                // 异步执行真实清理
                await Task.Run(() => 
                {
                    // 限频变量：避免过于频繁刷新 UI 导致卡顿
                    long lastTick = 0;

                    HardwareMonitor.Instance?.CleanMemory(progress => 
                    {
                        long now = DateTime.Now.Ticks;
                        // 如果进度未完成且距离上次刷新不足 15ms (约 60FPS)，则跳过刷新
                        if (progress < 100 && now - lastTick < 150000) return; 
                        
                        lastTick = now;

                        // 必须 Invoke 到 UI 线程
                        try 
                        {
                            this.Invoke(new Action(() => UpdateProgress(progress)));
                        }
                        catch { } // 防止窗口关闭后调用异常
                    });
                });

                // 确保显示 100%
                UpdateProgress(100);

                // 显示完成状态并关闭
                _lblPercent.Text = "OK";
                _lblTitle.Text = LanguageManager.T("Menu.CleanMemorySuccess");
                await Task.Delay(800); // 停留0.8秒展示结果
            }
            finally
            {
                this.Close();
            }
        }

        private void UpdateProgress(int val)
        {
            if (val > 100) val = 100;
            _bar.Value = val;
            _lblPercent.Text = $"{val}%";
        }

        // 复用 SpeedTestForm 的圆角逻辑
        private void ApplyRounded()
        {
            try
            {
                using (var gp = new GraphicsPath())
                {
                    int r = Math.Max(ScaleDPI(8), _currentTheme.Layout.CornerRadius); // 至少8px圆角
                    int d = r * 2;
                    gp.AddArc(0, 0, d, d, 180, 90);
                    gp.AddArc(Width - d, 0, d, d, 270, 90);
                    gp.AddArc(Width - d, Height - d, d, d, 0, 90);
                    gp.AddArc(0, Height - d, d, d, 90, 90);
                    gp.CloseFigure();
                    Region = new Region(gp);
                }
            }
            catch { }
        }

        private void MakeMovable(Control control)
        {
            control.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) _dragOffset = e.Location; };
            control.MouseMove += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    if (Math.Abs(e.X - _dragOffset.X) + Math.Abs(e.Y - _dragOffset.Y) < 1) return;
                    Location = new Point(Left + e.X - _dragOffset.X, Top + e.Y - _dragOffset.Y);
                }
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // 绘制一个淡边框以增强立体感
            using (var p = new Pen(Color.FromArgb(40, 128, 128, 128), 1))
            {
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }
        }
    }
}
