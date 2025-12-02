using System;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using System.Text.Json; // 引入这个用于克隆对象

namespace LiteMonitor
{
    public class ThresholdForm : Form
    {
        private Settings _cfg;          // 这里去掉 readonly，因为我们要给它赋新值（替身）
        private Settings _sourceCfg;    // 新增：用于保存原始配置（真身）
        private float _scale = 1.0f;

        // === 🎨 现代深色主题 (Modern Dark Theme) ===
        // 1. 层级配色：背景最深 -> 卡片稍亮 -> 输入框最亮
        private readonly Color C_Background = Color.FromArgb(60, 60, 60);    // 窗体底色
        private readonly Color C_Card       = Color.FromArgb(46, 46, 46);    // 卡片背景
        private readonly Color C_Button_Bar = Color.FromArgb(50, 50, 50);    // 按钮栏背景
        private readonly Color C_InputBack = Color.FromArgb(55, 55, 55);    // 输入框背景
        private readonly Color C_Separator  = Color.FromArgb(60, 60, 60);    // 分割线
        
        // 2. 文字配色
        private readonly Color C_TextMain   = Color.FromArgb(240, 240, 240); // 主要文字
        private readonly Color C_TextSub    = Color.FromArgb(160, 160, 160); // 次要/说明文字
        private readonly Color C_TextTitle  = Color.FromArgb(255, 255, 255); // 卡片标题
        
        // 3. 功能色
        private readonly Color C_Warn       = Color.FromArgb(255, 180, 0);   // 警告 (橙)
        private readonly Color C_Crit       = Color.FromArgb(255, 80, 80);   // 严重 (红)
        private readonly Color C_Action     = Color.FromArgb(0, 120, 215);   // 按钮 (蓝)

        // 字体缓存
        private Font F_Title;   // 卡片标题
        private Font F_Label;   // 普通标签
        private Font F_Value;   // 数字输入

        public ThresholdForm(Settings cfg)
        {
            // 1. 记住真身
            _sourceCfg = cfg;

            // 2. 制造替身 (克隆)
            // 原理：把配置转成文本再转回来，就得到了一个一模一样的新对象，但和原来的没关系
            var json = JsonSerializer.Serialize(cfg);
            _cfg = JsonSerializer.Deserialize<Settings>(json);
            
            // DPI 适配
            using (Graphics g = this.CreateGraphics())
            {
                _scale = g.DpiX / 96.0f;
            }

            // 字体初始化
            F_Title = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            F_Label = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular);
            F_Value = new Font("Consolas", 10.5F, FontStyle.Bold);

            // 窗体属性
            this.Text = "告警阈值设置 (Threshold Settings)";
            this.Size = new Size(S(584), S(960));
            this.MinimumSize = new Size(S(584), S(960));
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.BackColor = C_Background;
            this.ForeColor = C_TextMain;

            BuildUI();
        }

        private int S(int pixel) => (int)(pixel * _scale);

        private void BuildUI()
        {
            // 1. 主滚动容器
            var mainScroll = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(S(20), S(20), S(20), S(100)), // 底部留白给按钮栏
                BackColor = C_Background
            };

            // 2. 添加功能卡片 (Cards)
             // ★★★ [新增] 卡片: 频率与功耗最大值 ★★★
            // 为了数据准确性，单位使用 MHz (对应 Settings 中的存储单位)，如果用 GHz 需要小数位支持
            mainScroll.Controls.Add(CreateCard("最大频率与功耗 (Max Limits) -- 仅在开启频率/功耗显示时设置", p => {
                AddHardwareHeaderRow(p); // 添加 CPU/GPU 表头
                
                // 最大频率行
                AddMaxLimitRow(p, "最大频率 / Max Clock (MHz)", 
                    _cfg.RecordedMaxCpuClock, _cfg.RecordedMaxGpuClock,
                    v => _cfg.RecordedMaxCpuClock = v,
                    v => _cfg.RecordedMaxGpuClock = v);

                // 最大功耗行
                AddMaxLimitRow(p, "最大功耗 / Max Power (W)", 
                    _cfg.RecordedMaxCpuPower, _cfg.RecordedMaxGpuPower,
                    v => _cfg.RecordedMaxCpuPower = v,
                    v => _cfg.RecordedMaxGpuPower = v);
            
              // ★★★ 新增：添加多行说明文本 ★★★
                AddDescriptionRow(p, "⚠️ 请填写硬件的实际最大值，若不填写，将在高负载时动态学习并更新。");
            }));

            
            // 卡片 A: 通用硬件 (CPU/Temp)
            mainScroll.Controls.Add(CreateCard("⚠️通用硬件 (General Hardware)", p => {
                AddHeaderRow(p); // 表头
                AddConfigRow(p, "负载 / Load (%)", _cfg.Thresholds.Load);
                AddConfigRow(p, "温度 / Temp (°C)", _cfg.Thresholds.Temp);
            }));

            // 卡片 B: 磁盘与网络 (Speed)
            mainScroll.Controls.Add(CreateCard("⚠️传输速率 (Transfer Speed)", p => {
                AddHeaderRow(p);
                AddConfigRow(p, "磁盘读写 / Disk IO (MB/s)", _cfg.Thresholds.DiskIOMB);
                AddConfigRow(p, "上传速率 / Net Up (MB/s)", _cfg.Thresholds.NetUpMB);
                AddConfigRow(p, "下载速率 / Net Down (MB/s)", _cfg.Thresholds.NetDownMB);
            }));

            // 卡片 C: 流量统计 (Data)
            mainScroll.Controls.Add(CreateCard("⚠️每日流量 (Daily Data Usage)", p => {
                AddHeaderRow(p);
                AddConfigRow(p, "上传总量 / Upload (MB)", _cfg.Thresholds.DataUpMB);
                AddConfigRow(p, "下载总量 / Download (MB)", _cfg.Thresholds.DataDownMB);
            }));

           
            // 卡片 D: 弹窗通知 (Notification)
            mainScroll.Controls.Add(CreateCard("⚠️弹窗通知 (Popup Alert)", p => {
                // 单行特殊布局
                AddSingleRow(p, "高温报警触发线 / High Temp Limit (°C)", _cfg.AlertTempThreshold, v => _cfg.AlertTempThreshold = v);
            }));

            this.Controls.Add(mainScroll);

            // 3. 底部按钮栏 (悬浮在底部)
            var bottomPanel = new Panel 
            { 
                Dock = DockStyle.Bottom, 
                Height = S(66), 
                BackColor = C_Button_Bar
            };
            // 顶部分割线
            bottomPanel.Controls.Add(new Label { Dock = DockStyle.Top, Height = 1, BackColor = C_Separator });

            var btnSave = CreateButton("保存 (Save)", C_Action, true);
            btnSave.Location = new Point(this.ClientSize.Width - S(240), S(15));
            // ★★★ 修改这里：点击保存时，把替身的数据覆盖回真身 ★★★
            btnSave.Click += (s, e) => { 
                // 1. 覆盖阈值设置
                _sourceCfg.Thresholds = _cfg.Thresholds;
                _sourceCfg.AlertTempThreshold = _cfg.AlertTempThreshold;
                
                // 2. ★★★ 新增：覆盖最大值记录 ★★★
                _sourceCfg.RecordedMaxCpuClock = _cfg.RecordedMaxCpuClock;
                _sourceCfg.RecordedMaxGpuClock = _cfg.RecordedMaxGpuClock;
                _sourceCfg.RecordedMaxCpuPower = _cfg.RecordedMaxCpuPower;
                _sourceCfg.RecordedMaxGpuPower = _cfg.RecordedMaxGpuPower;

                _sourceCfg.Save(); // 保存真身
                
                this.DialogResult = DialogResult.OK; 
                this.Close(); 
            };
            var btnCancel = CreateButton("取消 (Cancel)", Color.FromArgb(70, 70, 70), false);
            btnCancel.Location = new Point(this.ClientSize.Width - S(120), S(15));
            btnCancel.Click += (s, e) => this.Close();

            bottomPanel.Controls.Add(btnSave);
            bottomPanel.Controls.Add(btnCancel);
            this.Controls.Add(bottomPanel);
            bottomPanel.BringToFront();
        }

        // === UI 构建核心逻辑 ===

        /// <summary>
        /// 创建一个现代风格的卡片容器
        /// </summary>
        private Panel CreateCard(string title, Action<TableLayoutPanel> contentBuilder)
        {
            // 卡片容器
            var card = new Panel
            {
                Width = S(540), // 固定宽度
                AutoSize = true,
                BackColor = C_Card,
                Margin = new Padding(0, 0, 0, S(15)), // 卡片间距
                Padding = new Padding(1) // 边框效果 (配合内部 Panel)
            };

            // 标题栏
            var lblTitle = new Label
            {
                Text = title,
                Font = F_Title,
                ForeColor = C_TextTitle,
                Location = new Point(S(15), S(15)),
                AutoSize = true
            };
            card.Controls.Add(lblTitle);

            // 内容表格布局
            var table = new TableLayoutPanel
            {
                Location = new Point(S(15), S(45)),
                Width = S(510),
                Height = 0,     // <--- 核心修改：初始高度设为0，让它自动"长"大，而不是从默认的100缩小
                AutoSize = true,
                //AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3,
                BackColor = Color.Transparent,
            };
            // 列宽：标签 50% | Warn 25% | Crit 25%
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));

            // 填充内容
            contentBuilder(table);

            // 调整卡片高度 (标题 + 内容 + 底部留白)
            card.Height = table.Bottom + S(10);
            
            // 装饰线 (标题下方)
            var line = new Label { 
                BackColor = C_Separator, 
                Height = 1, 
                Width = S(510), 
                Location = new Point(S(15), S(40)) 
            };
            card.Controls.Add(line);

            card.Controls.Add(table);
            return card;
        }

        // 添加表头行 (Warn / Crit)
        private void AddHeaderRow(TableLayoutPanel t)
        {
            t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            
            // 空第一列
            t.Controls.Add(new Label(), 0, t.RowCount - 1);

            // Warn 表头
            var lblWarn = new Label { Text = "注意 (Warn)", ForeColor = C_Warn, Font = new Font(F_Label, FontStyle.Bold), AutoSize = true, Anchor = AnchorStyles.Left };
            t.Controls.Add(lblWarn, 1, t.RowCount - 1);

            // Crit 表头
            var lblCrit = new Label { Text = "重视 (Crit)", ForeColor = C_Crit, Font = new Font(F_Label, FontStyle.Bold), AutoSize = true, Anchor = AnchorStyles.Left };
            t.Controls.Add(lblCrit, 2, t.RowCount - 1);
        }

        // ★★★ [新增] 辅助方法：添加硬件表头 (CPU / GPU) ★★★
        private void AddHardwareHeaderRow(TableLayoutPanel t)
        {
            t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            
            // 空第一列
            t.Controls.Add(new Label(), 0, t.RowCount - 1);

            // CPU 表头
            var lblCpu = new Label { Text = "CPU (Max)", ForeColor = C_Action, Font = new Font(F_Label, FontStyle.Bold), AutoSize = true, Anchor = AnchorStyles.Left };
            t.Controls.Add(lblCpu, 1, t.RowCount - 1);

            // GPU 表头
            var lblGpu = new Label { Text = "GPU (Max)", ForeColor = C_Action, Font = new Font(F_Label, FontStyle.Bold), AutoSize = true, Anchor = AnchorStyles.Left };
            t.Controls.Add(lblGpu, 2, t.RowCount - 1);
        }

        // ★★★ [新增] 辅助方法：添加说明文本行 (跨列显示) ★★★
        private void AddDescriptionRow(TableLayoutPanel t, string text)
        {
            t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            int row = t.RowCount - 1;

            var lbl = new Label
            {
                Text = text,
                ForeColor = C_TextSub, // 使用次要文字颜色 (灰色)，避免喧宾夺主
                Font = new Font(F_Label.FontFamily, 9F, FontStyle.Bold), // 字体稍小一点
                AutoSize = true,
                Margin = new Padding(0, 0, 0, S(10)) // 底部增加 15px 间距，与下方表头隔开
            };
            t.Controls.Add(lbl, 0, row);
            t.SetColumnSpan(lbl, 3); // 关键：设置该控件跨越 3 列
        }


        // 添加配置行
        private void AddConfigRow(TableLayoutPanel t, string name, ValueRange range)
        {
            t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            int row = t.RowCount - 1;

            // 1. 标签
            var lbl = new Label
            {
                Text = name,
                ForeColor = C_TextMain,
                Font = F_Label,
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, S(10), 0, S(10)) // 垂直间距
            };
            t.Controls.Add(lbl, 0, row);

            // 2. 输入框 Warn
            var numWarn = CreateModernNum(range.Warn, C_Warn);
            numWarn.ValueChanged += (s, e) => range.Warn = (double)numWarn.Value;
            t.Controls.Add(numWarn, 1, row);

            // 3. 输入框 Crit
            var numCrit = CreateModernNum(range.Crit, C_Crit);
            numCrit.ValueChanged += (s, e) => range.Crit = (double)numCrit.Value;
            t.Controls.Add(numCrit, 2, row);
        }

        // ★★★ [新增] 辅助方法：添加最大值配置行 ★★★
        private void AddMaxLimitRow(TableLayoutPanel t, string name, float cpuVal, float gpuVal, Action<float> setCpu, Action<float> setGpu)
        {
            t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            int row = t.RowCount - 1;

            // 1. 标签
            var lbl = new Label
            {
                Text = name,
                ForeColor = C_TextMain,
                Font = F_Label,
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, S(10), 0, S(10)) 
            };
            t.Controls.Add(lbl, 0, row);

            // 2. CPU 输入框 (使用 C_Action 蓝色以示区分)
            var numCpu = CreateModernNum(cpuVal, C_Action);
            numCpu.ValueChanged += (s, e) => setCpu((float)numCpu.Value);
            t.Controls.Add(numCpu, 1, row);

            // 3. GPU 输入框
            var numGpu = CreateModernNum(gpuVal, C_Action);
            numGpu.ValueChanged += (s, e) => setGpu((float)numGpu.Value);
            t.Controls.Add(numGpu, 2, row);
        }

        // 添加单行配置 (用于弹窗阈值)
        private void AddSingleRow(TableLayoutPanel t, string name, int val, Action<int> setter)
        {
            t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            int row = t.RowCount - 1;

            // 标签
            var lbl = new Label
            {
                Text = name,
                ForeColor = C_TextMain,
                Font = F_Label,
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, S(10), 0, S(10))
            };
            t.Controls.Add(lbl, 0, row);

            // 输入框 (红色，放在 Crit 列以示重要)
            var num = CreateModernNum(val, C_Crit);
            num.ValueChanged += (s, e) => setter((int)num.Value);
            t.Controls.Add(num, 2, row);
        }

        // 创建美化后的数字输入框
        private NumericUpDown CreateModernNum(double val, Color accent)
        {
            var num = new NumericUpDown
            {
                Width = S(100),
                BackColor = C_InputBack,
                ForeColor = accent, // 数字颜色跟随列 (橙/红)
                BorderStyle = BorderStyle.FixedSingle, // 扁平边框
                Minimum = 0,
                Maximum = 999999,
                DecimalPlaces = 0,
                Value = (decimal)val,
                Font = F_Value,
                TextAlign = HorizontalAlignment.Center,
                Margin = new Padding(0, S(8), 0, S(8))
            };
            return num;
        }

        private Button CreateButton(string text, Color bg, bool isPrimary)
        {
            return new Button
            {
                Text = text,
                Size = new Size(S(110), S(36)),
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Microsoft YaHei UI", 10F, isPrimary ? FontStyle.Bold : FontStyle.Regular),
                FlatAppearance = { BorderSize = 0 } // 无边框按钮
            };
        }
    }
}