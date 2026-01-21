using System;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core; // 确保引用了 UIUtils
using LiteMonitor.src.UI.SettingsPage;

namespace LiteMonitor.src.UI.Controls
{
    public static class UIColors
    {
        public static Color MainBg = Color.FromArgb(243, 243, 243);
        public static Color SidebarBg = Color.FromArgb(240, 240, 240);
        public static Color CardBg = Color.White;
        public static Color Border = Color.FromArgb(220, 220, 220);
        public static Color Primary = Color.FromArgb(0, 120, 215);
        public static Color TextMain = Color.FromArgb(32, 32, 32);
        public static Color TextSub = Color.FromArgb(90, 90, 90);
        public static Color GroupHeader = Color.FromArgb(248, 249, 250); 
        
        public static Color NavSelected = Color.FromArgb(230, 230, 230); 
        public static Color NavHover = Color.FromArgb(235, 235, 235);

        public static Color TextWarn = Color.FromArgb(215, 145, 0); 
        public static Color TextCrit = Color.FromArgb(220, 50, 50); 
    }

    public class LiteActionRow : Panel
    {
        public Label Label { get; private set; }
        public Control RightControl { get; private set; }

        public LiteActionRow(string title, Control rightControl)
        {
            this.Height = UIUtils.S(40);
            this.Margin = new Padding(0); // Full width item
            this.Padding = new Padding(0);

            Label = new Label { 
                Text = title, 
                AutoSize = true, 
                Font = new Font("Microsoft YaHei UI", 9F), 
                ForeColor = UIColors.TextSub, // Slightly gray for descriptions/tips
                TextAlign = ContentAlignment.MiddleLeft 
            };

            RightControl = rightControl;
            
            this.Controls.Add(RightControl);
            this.Controls.Add(Label);

            this.Layout += (s, e) => {
                int mid = this.Height / 2;
                Label.Location = new Point(UIUtils.S(0), mid - Label.Height / 2); // Indent slightly
                
                // Position RightControl on the right
                if (RightControl.Dock != DockStyle.Fill && RightControl.Dock != DockStyle.Top && RightControl.Dock != DockStyle.Bottom)
                {
                    RightControl.Location = new Point(this.Width - RightControl.Width - UIUtils.S(5), mid - RightControl.Height / 2);
                }
            };
        }
    }

    public class LiteThresholdRow : Panel
    {
        public LiteThresholdRow(SettingsPageBase page, string title, string unit, ValueRange range)
        {
            this.Height = UIUtils.S(40);
            this.Margin = new Padding(0);
            this.Padding = new Padding(0);

            // Title
            var lblTitle = new Label {
                Text = title, AutoSize = true, 
                Font = new Font("Microsoft YaHei UI", 9F), ForeColor = UIColors.TextMain,
                TextAlign = ContentAlignment.MiddleLeft
            };
            this.Controls.Add(lblTitle);

            // Right Container
            var rightBox = new FlowLayoutPanel {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false, 
                BackColor = Color.Transparent, Padding = new Padding(0)
            };

            // Inputs
            var inputWarn = new LiteNumberInput(range.Warn.ToString(), unit, LanguageManager.T("Menu.ValueWarnColor"), 140, UIColors.TextWarn);
            inputWarn.Padding = UIUtils.S(new Padding(0, 5, 0, 1));
            
            var arrow = new Label { Text = "➜", AutoSize = true, ForeColor = Color.LightGray, Font = new Font("Microsoft YaHei UI", 9F), Margin = UIUtils.S(new Padding(5, 4, 5, 0)) };
            
            var inputCrit = new LiteNumberInput(range.Crit.ToString(), unit, LanguageManager.T("Menu.ValueCritColor"), 140, UIColors.TextCrit);
            inputCrit.Padding = UIUtils.S(new Padding(0, 5, 0, 1));

            // Deferred Bindings
            page.RegisterDelaySave(() => {
                range.Warn = inputWarn.ValueDouble;
                range.Crit = inputCrit.ValueDouble;
            });
            page.RegisterRefresh(() => {
                inputWarn.Inner.Text = range.Warn.ToString();
                inputCrit.Inner.Text = range.Crit.ToString();
            });

            rightBox.Controls.Add(inputWarn);
            rightBox.Controls.Add(arrow);
            rightBox.Controls.Add(inputCrit);
            this.Controls.Add(rightBox);

            // Layout
            this.Layout += (s, e) => {
                lblTitle.Location = new Point(UIUtils.S(5), (this.Height - lblTitle.Height) / 2);
                rightBox.Location = new Point(this.Width - rightBox.Width - UIUtils.S(5), (this.Height - rightBox.Height) / 2);
            };
            
            // Separator Line
            this.Paint += (s, e) => {
                using(var p = new Pen(Color.FromArgb(240, 240, 240))) e.Graphics.DrawLine(p, 0, Height-1, Width, Height-1);
            };
        }
    }

    public class LiteHintRow : Panel
    {
        public LiteHintRow(string text, int indent = 0)
        {
            this.Dock = DockStyle.Top;
            this.Margin = new Padding(0);
            this.Padding = UIUtils.S(new Padding(indent, 5, 5, 5));
            this.AutoSize = true;

            var lbl = new Label { 
                Text = text, 
                AutoSize = true, 
                MaximumSize = new Size(UIUtils.S(500), 0), // Limit width to trigger wrap? Actual width set in Layout
                Font = new Font("Microsoft YaHei UI", 8F), 
                ForeColor = Color.Gray,
                Dock = DockStyle.Fill
            };
            this.Controls.Add(lbl);
            
            // Dynamic Height based on text
            this.Resize += (s, e) => {
                lbl.MaximumSize = new Size(this.Width - this.Padding.Horizontal, 0);
            };
        }
    }

    public static class UIFonts 
    {
        public static Font Regular(float size) => new Font("Microsoft YaHei UI", size, FontStyle.Regular);
        public static Font Bold(float size) => new Font("Microsoft YaHei UI", size, FontStyle.Bold);
    }

    // =======================================================================
    // 1. 容器组件
    // =======================================================================

    public class LiteSettingsGroup : Panel
    {
        private TableLayoutPanel _layout;
        private Panel _header; // ★★★ 提升为成员变量
        private int _colTracker = 0;

        public LiteSettingsGroup(string title)
        {
            this.AutoSize = true;
            this.Dock = DockStyle.Top;
            this.Padding = new Padding(1); 
            this.BackColor = UIColors.Border; 
            // ★★★ 修改：Margin 缩放
            this.Margin = new Padding(0, 0, 0, UIUtils.S(15)); 

            var inner = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, AutoSize = true };
            
            // ★★★ 修改：Height 缩放
            _header = new Panel { Dock = DockStyle.Top, Height = UIUtils.S(40), BackColor = UIColors.GroupHeader, Padding = new Padding(0, 0, UIUtils.S(10), 0) }; // 增加右侧Padding
            // ★★★ 修改：Location 缩放
            var lbl = new Label { 
                Text = title, Location = new Point(UIUtils.S(15), UIUtils.S(10)), AutoSize = true, 
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold), ForeColor = UIColors.TextMain 
            };
            _header.Controls.Add(lbl);
            // ★★★ 修改：绘图线坐标动态化 (header.Height - 1)
            _header.Paint += (s, e) => { using(var p = new Pen(UIColors.Border)) e.Graphics.DrawLine(p, 0, _header.Height - 1, _header.Width, _header.Height - 1); };

            _layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1,
                // ★★★ 修改：Padding 缩放
                Padding = UIUtils.S(new Padding(25, 10, 25, 15)), BackColor = Color.White
            };
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

            inner.Controls.Add(_layout);
            inner.Controls.Add(_header);
            this.Controls.Add(inner);
        }

        public void AddHeaderAction(Control action)
        {
            // 创建一个包装容器来控制垂直位置和边距
            var wrapper = new Panel 
            { 
                Dock = DockStyle.Right, 
                Width = action.Width + UIUtils.S(10), // 额外间距
                Padding = new Padding(0)
            };
            
            // 手动垂直居中 (Gap on Left)
            action.Location = new Point(UIUtils.S(10), (_header.Height - action.Height) / 2);
            
            // ★★★ Fix: Draw Bottom Line in Wrapper ★★★
            wrapper.Paint += (s, e) => {
                 using(var p = new Pen(UIColors.Border))
                     e.Graphics.DrawLine(p, 0, wrapper.Height - 1, wrapper.Width, wrapper.Height - 1);
            };

            wrapper.Controls.Add(action);
            _header.Controls.Add(wrapper);
            
            wrapper.BringToFront(); // 确保在最右侧
        }

        public void AddItem(Control item)
        {
            _layout.Controls.Add(item);
            item.Dock = DockStyle.Fill;
            // ★★★ 修改：Margin 缩放
            if (_colTracker == 0) { item.Margin = UIUtils.S(new Padding(0, 2, 30, 2)); _colTracker = 1; }
            else { item.Margin = UIUtils.S(new Padding(30, 2, 0, 2)); _colTracker = 0; }
        }

        public void AddFullItem(Control item)
        {
            _layout.Controls.Add(item);
            _layout.SetColumnSpan(item, 2);
            item.Dock = DockStyle.Fill;
            item.Margin = new Padding(0, 0, 0, 0); 
            _colTracker = 0; 
        }
    }

    public class LiteSettingsItem : Panel
    {
        public Label Label { get; private set; }

        public LiteSettingsItem(string text, Control ctrl)
        {
            // ★★★ 修改：Height/Margin 缩放
            this.Height = UIUtils.S(40);
            this.Margin = UIUtils.S(new Padding(0, 2, 40, 2)); 
            Label = new Label { 
                Text = text, AutoSize = true, 
                Font = new Font("Microsoft YaHei UI", 9F), ForeColor = UIColors.TextMain,
                TextAlign = ContentAlignment.MiddleLeft 
            };
            // ★★★ 修改：Height 缩放
            if (ctrl is LiteCheck) ctrl.Height = UIUtils.S(22); 
            this.Controls.Add(Label);
            this.Controls.Add(ctrl);
            this.Layout += (s, e) => {
                int mid = this.Height / 2;
                Label.Location = new Point(0, mid - Label.Height / 2);
                ctrl.Location = new Point(this.Width - ctrl.Width, mid - ctrl.Height / 2);
            };
            this.Paint += (s, e) => {
                using(var p = new Pen(Color.FromArgb(240, 240, 240))) 
                    e.Graphics.DrawLine(p, 0, Height-1, Width, Height-1);
            };
        }
    }


    public class LiteCard : Panel
    {
        public LiteCard() { BackColor = UIColors.CardBg; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Dock = DockStyle.Top; Padding = new Padding(1); }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using (var p = new Pen(UIColors.Border)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); }
    }

    // =======================================================================
    // 2. 交互组件
    // =======================================================================

    // ★★★ [优化版] 下划线输入框：支持前置标签 ★★★
    public class LiteUnderlineInput : Panel
    {
        public TextBox Inner;
        private Label _lblUnit;   // 单位 (右侧)
        private Label _lblLabel;  // 标签 (左侧)

        private const int EM_SETCUEBANNER = 0x1501;
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern Int32 SendMessage(IntPtr hWnd, int msg, int wParam, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string lParam);

        public string Placeholder
        {
            set
            {
                if (Inner != null && !Inner.IsDisposed)
                {
                    SendMessage(Inner.Handle, EM_SETCUEBANNER, 0, value);
                }
            }
        }

        public LiteUnderlineInput(string text, string unit = "", string labelPrefix = "", int width = 160, Color? fontColor = null, HorizontalAlignment align = HorizontalAlignment.Left) 
        {
            // ★★★ 修改：Size/Padding 缩放
            this.Size = new Size(UIUtils.S(width), UIUtils.S(26)); 
            this.BackColor = Color.Transparent;
            this.Padding = UIUtils.S(new Padding(0, 2, 0, 3)); 
            this.Cursor = Cursors.IBeam;

            // 1. 创建并添加输入框 (垫底)
            Inner = new TextBox {
                Text = text,
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular),
                ForeColor = fontColor ?? UIColors.TextSub,
                TextAlign = align 
            };
            // this.Controls.Add(Inner); // Moved to end to ensure correct Dock order (Inner should be last Docked => Front of Z-Order)

            // 2. 添加单位 (Dock Right, 浮在右边)
            if (!string.IsNullOrEmpty(unit))
            {
                _lblUnit = new Label {
                    Text = unit,
                    AutoSize = true, 
                    Dock = DockStyle.Right,
                    Font = new Font("Microsoft YaHei UI", 8F), 
                    ForeColor = Color.Gray, 
                    TextAlign = ContentAlignment.BottomRight, 
                    Padding = new Padding(0, 0, 0, 4) 
                };
                this.Controls.Add(_lblUnit); 
                _lblUnit.Click += (s, e) => Inner.Focus();
            }

            // 3. 添加前置标签 (Dock Left, 浮在左边)
            if (!string.IsNullOrEmpty(labelPrefix))
            {
                _lblLabel = new Label {
                    Text = labelPrefix, 
                    AutoSize = true, 
                    Dock = DockStyle.Left,
                    Font = new Font("Microsoft YaHei UI", 9F), 
                    ForeColor = fontColor ?? UIColors.TextSub,
                    TextAlign = ContentAlignment.BottomLeft, 
                    Padding = new Padding(0, 0, 4, 3) 
                };
                this.Controls.Add(_lblLabel); 
                _lblLabel.Click += (s, e) => Inner.Focus();
            }

            // Add Inner last so it is at the Front of Z-Order (Index 0),
            // which means it is docked LAST (filling remaining space).
            this.Controls.Add(Inner);

            // 事件转发
            Inner.Enter += (s, e) => this.Invalidate();
            Inner.Leave += (s, e) => this.Invalidate();
            this.Click += (s, e) => Inner.Focus();

            // ★★★ Fix: Ensure Inner is at the top of Z-Order so it docks LAST (filling remaining space)
            // ensuring it respects the space taken by previously docked controls (Unit/Label)
            Inner.BringToFront(); 
        }

        public void SetTextColor(Color c) => Inner.ForeColor = c;
        public void SetBg(Color c) { Inner.BackColor = c; }
        
        protected override void OnPaint(PaintEventArgs e) {
            var c = Inner.Focused ? UIColors.Primary : Color.LightGray; 
            int h = Inner.Focused ? 2 : 1;

            // 画线逻辑：如果有左侧标签，线条从标签右侧开始画
            int startX = 0;
            if (_lblLabel != null) startX = _lblLabel.Width; 
            int drawWidth = this.Width - startX;

            // 线条画在底部 (Height - h)
            using (var b = new SolidBrush(c)) 
                e.Graphics.FillRectangle(b, startX, Height - h, drawWidth, h);
        }
    }

    // =======================================================================
    // 新增：专门的数字输入框 (继承自下划线输入框)
    // =======================================================================
    public class LiteNumberInput : LiteUnderlineInput
    {
        public LiteNumberInput(
            string text, 
            string unit = "", 
            string label = "",      
            int width = 160, 
            Color? color = null,    
            int maxLength = 10) 
            : base(text, unit, label, width, color, HorizontalAlignment.Center) 
        {
            this.Inner.MaxLength = maxLength;

            this.Inner.KeyPress += (s, e) =>
            {
                if (char.IsControl(e.KeyChar) || char.IsDigit(e.KeyChar)) return;
                if (e.KeyChar == '.' && !this.Inner.Text.Contains(".")) return;
                if (e.KeyChar == '-' && this.Inner.SelectionStart == 0 && !this.Inner.Text.Contains("-")) return;
                e.Handled = true;
            };

            this.Inner.Leave += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(this.Inner.Text) || this.Inner.Text == "." || this.Inner.Text == "-")
                {
                    this.Inner.Text = "0";
                }
            };
        }
        
        public int ValueInt => int.TryParse(Inner.Text, out int v) ? v : 0;
        public double ValueDouble => double.TryParse(Inner.Text, out double v) ? v : 0.0;
    }

    public class LiteColorInput : Panel
    {
        public LiteUnderlineInput Input;
        public LiteColorPicker Picker;
        public string HexValue { get => Input.Inner.Text; set { Input.Inner.Text = value; Picker.SetHex(value); } }

        public LiteColorInput(string initialHex)
        {
            // ★★★ 修改：Size/Location 缩放
            this.Size = new Size(UIUtils.S(95), UIUtils.S(26)); 
            Picker = new LiteColorPicker(initialHex) { Size = new Size(UIUtils.S(26), UIUtils.S(22)), Location = new Point(this.Width - UIUtils.S(26), UIUtils.S(3)) };
            
            // 适配新构造函数
            Input = new LiteUnderlineInput(initialHex, "", "", 60) { Location = new Point(0, 0) };
            
            Picker.ColorChanged += (s, e) => Input.Inner.Text = $"#{Picker.Value.R:X2}{Picker.Value.G:X2}{Picker.Value.B:X2}";
            Input.Inner.TextChanged += (s, e) => Picker.SetHex(Input.Inner.Text);
            this.Controls.Add(Input);
            this.Controls.Add(Picker);
        }
    }

    public class LiteColorPicker : Control
    {
        private Color _color;
        public event EventHandler? ColorChanged;
        public Color Value { get => _color; set { _color = value; Invalidate(); } }
        // ★★★ 修改：Size 缩放
        public LiteColorPicker(string initialHex) { SetHex(initialHex); this.Size = new Size(UIUtils.S(24), UIUtils.S(24)); this.Cursor = Cursors.Hand; this.DoubleBuffered = true; this.Click += (s, e) => PickColor(); }
        public void SetHex(string hex) { try { _color = ColorTranslator.FromHtml(hex); Invalidate(); } catch {} }
        private void PickColor() { using (var cd = new ColorDialog()) { cd.Color = _color; cd.FullOpen = true; if (cd.ShowDialog() == DialogResult.OK) { _color = cd.Color; ColorChanged?.Invoke(this, EventArgs.Empty); Invalidate(); } } }
        protected override void OnPaint(PaintEventArgs e) { using (var b = new SolidBrush(_color)) e.Graphics.FillRectangle(b, 0, 0, Width - 1, Height - 1); using (var p = new Pen(Color.Gray)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); }
    }

    // 其他原有组件
    public class LiteNote : Panel { public LiteNote(string text, int indent = 0) { this.Dock = DockStyle.Top; this.Height = UIUtils.S(32); this.Margin = new Padding(0); var lbl = new Label { Text = text, AutoSize = true, Font = new Font("Microsoft YaHei UI", 8F), ForeColor = Color.Gray, Location = new Point(UIUtils.S(indent), UIUtils.S(10)) }; this.Controls.Add(lbl); } }
    public class LiteComboItem 
    { 
        public string Text { get; set; } 
        public string Value { get; set; } 
        public override string ToString() => Text;
    }

    public class NoScrollComboBox : ComboBox
    {
        protected override void WndProc(ref Message m)
        {
            // WM_MOUSEWHEEL = 0x020A
            if (m.Msg == 0x020A && !this.DroppedDown) return;
            base.WndProc(ref m);
        }
    }

    public class LiteComboBox : Panel 
    { 
        public ComboBox Inner; 
        
        public LiteComboBox() 
        { 
            this.Size = new Size(UIUtils.S(110), UIUtils.S(28)); 
            this.BackColor = Color.White; 
            this.Padding = new Padding(1); 
            
            Inner = new NoScrollComboBox 
            { 
                DropDownStyle = ComboBoxStyle.DropDownList, 
                FlatStyle = FlatStyle.Flat, 
                ForeColor = UIColors.TextSub, 
                Font = new Font("Microsoft YaHei UI", 9F), 
                // ★★★ 核心修复：移除 Dock.Fill，改为 None，以便我们可以手动居中它
                Dock = DockStyle.None, 
                BackColor = Color.White, 
                Margin = new Padding(0) 
            }; 
            this.Controls.Add(Inner); 

            // ★★★ 新增：手动布局以实现垂直居中 ★★★
            this.Layout += (s, e) => {
                Inner.Width = this.Width - 2; // 减去边框宽度
                Inner.Location = new Point(1, (this.Height - Inner.Height) / 2);
            };

            this.Paint += (s, e) => { 
                using (var p = new Pen(UIColors.Border)) 
                    e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); 
            }; 
        } 
        
        public object SelectedItem { get => Inner.SelectedItem; set => Inner.SelectedItem = value; } 
        public int SelectedIndex { get => Inner.SelectedIndex; set => Inner.SelectedIndex = value; } 
        public ComboBox.ObjectCollection Items => Inner.Items; 
        public override string Text { get => Inner.Text; set => Inner.Text = value; } 

        // Helper methods for Key-Value pairs
        public void AddItem(string text, string value)
        {
             Inner.Items.Add(new LiteComboItem { Text = text, Value = value });
             Inner.DisplayMember = "Text";
             Inner.ValueMember = "Value";
        }
        
        public void SelectValue(string value)
        {
             for(int i=0; i<Inner.Items.Count; i++)
             {
                 if (Inner.Items[i] is LiteComboItem item && item.Value == value)
                 {
                     Inner.SelectedIndex = i;
                     return;
                 }
             }
             if (Inner.Items.Count > 0) Inner.SelectedIndex = 0;
        }

        public string SelectedValue 
        {
            get => (Inner.SelectedItem as LiteComboItem)?.Value;
        }
    }
    public class LiteLink : Label
    {
        private Color _normalColor = Color.DodgerBlue;
        private Color _hoverColor = Color.FromArgb(0, 100, 200);

        public LiteLink(string text, Action onClick = null)
        {
            this.Text = text;
            this.AutoSize = true;
            this.Cursor = Cursors.Hand;
            this.ForeColor = _normalColor;
            this.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Underline);
            
            if (onClick != null)
                this.Click += (s, e) => { if (this.Enabled) onClick(); };

            this.MouseEnter += (s, e) => { if (this.Enabled) this.ForeColor = _hoverColor; };
            this.MouseLeave += (s, e) => { if (this.Enabled) this.ForeColor = _normalColor; };
        }

        public void SetColor(Color normal, Color hover)
        {
            _normalColor = normal;
            _hoverColor = hover;
            if (Enabled) this.ForeColor = normal;
        }

        public new bool Enabled
        { 
            get => base.Enabled; 
            set 
            { 
                base.Enabled = value;
                this.Cursor = value ? Cursors.Hand : Cursors.Default;
                this.ForeColor = value ? _normalColor : Color.Gray;
            } 
        }
    }

    // =======================================================================
    // 3. 组合/高级组件 (New Standard Components)
    // =======================================================================


    public class LiteCheck : CheckBox { public LiteCheck(bool val, string text = "") { Checked = val; AutoSize = true; Cursor = Cursors.Hand; Text = text; Padding = UIUtils.S(new Padding(2)); ForeColor = UIColors.TextSub; Font = new Font("Microsoft YaHei UI", 9F); } }
    
    public class LiteButton : Button 
    { 
        private bool _dashed;
        
        public LiteButton(string t, bool p = false, bool dashed = false) 
        { 
            Text = t; 
            _dashed = dashed;
            Size = new Size(UIUtils.S(80), UIUtils.S(32)); 
            FlatStyle = FlatStyle.Flat; 
            Cursor = Cursors.Hand; 
            Font = new Font("Microsoft YaHei UI", 9F); 
            
            if (p) 
            { 
                BackColor = UIColors.Primary; 
                ForeColor = Color.White; 
                FlatAppearance.BorderSize = 0; 
            } 
            else if (dashed)
            {
                BackColor = Color.Transparent;
                ForeColor = UIColors.TextSub;
                FlatAppearance.BorderSize = 0;
            }
            else 
            { 
                BackColor = Color.White; 
                ForeColor = UIColors.TextMain; 
                FlatAppearance.BorderColor = UIColors.Border; 
            } 
        } 

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_dashed)
            {
                using (var pen = new Pen(Color.LightGray, 1.5f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                }
            }
        }
    }
    public class LiteNavBtn : Button { private bool _isActive; public bool IsActive { get => _isActive; set { _isActive = value; Invalidate(); } } public LiteNavBtn(string text) { Text = "  " + text; Size = new Size(UIUtils.S(150), UIUtils.S(40)); FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; TextAlign = ContentAlignment.MiddleLeft; Font = new Font("Microsoft YaHei UI", 10F); Cursor = Cursors.Hand; Margin = UIUtils.S(new Padding(5, 2, 5, 2)); BackColor = UIColors.SidebarBg; ForeColor = UIColors.TextMain; } protected override void OnPaint(PaintEventArgs e) { Color bg = _isActive ? UIColors.NavSelected : (ClientRectangle.Contains(PointToClient(Cursor.Position)) ? UIColors.NavHover : UIColors.SidebarBg); using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, ClientRectangle); if (_isActive) { using (var b = new SolidBrush(UIColors.Primary)) e.Graphics.FillRectangle(b, 0, UIUtils.S(8), UIUtils.S(3), Height - UIUtils.S(16)); Font = new Font(Font, FontStyle.Bold); } else { Font = new Font(Font, FontStyle.Regular); } TextRenderer.DrawText(e.Graphics, Text, Font, new Point(UIUtils.S(12), UIUtils.S(9)), UIColors.TextMain); } protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); } protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); } }
    public class LiteSortBtn : Button { public LiteSortBtn(string txt) { Text = txt; Size = new Size(UIUtils.S(24), UIUtils.S(24)); FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; BackColor = Color.FromArgb(245, 245, 245); ForeColor = Color.DimGray; Cursor = Cursors.Hand; Font = new Font("Microsoft YaHei UI", 7F, FontStyle.Bold); Margin = new Padding(0); } }
    
    // ★★★ New: Header Button (Variable Width) ★★★
    public class LiteHeaderBtn : Button 
    { 
        public LiteHeaderBtn(string txt) 
        { 
            Text = txt; 
            FlatStyle = FlatStyle.Flat; 
            FlatAppearance.BorderSize = 0; 
            BackColor = Color.FromArgb(225, 225, 225); 
            ForeColor = Color.DimGray; 
            Cursor = Cursors.Hand; 
            Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold); 
            Margin = new Padding(0);
            
            // Auto Width
            int w = TextRenderer.MeasureText(txt, Font).Width + UIUtils.S(16);
            Size = new Size(w, UIUtils.S(24));
        } 
        public void SetColor(Color c) { ForeColor = c; }
    }

    
    /// <summary>
    /// 终极防闪烁面板
    /// 1. 开启双缓冲合成
    /// 2. 拦截背景擦除 (消除白屏闪烁的关键)
    /// </summary>
    public class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            // 开启所有标准的双缓冲标志
            this.DoubleBuffered = true;
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | 
                          ControlStyles.UserPaint | 
                          ControlStyles.OptimizedDoubleBuffer | 
                          ControlStyles.ResizeRedraw |
                          ControlStyles.ContainerControl, true); // 确保它作为容器被优化
            this.UpdateStyles();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
                return cp;
            }
        }

        // ★★★ 核心修复：拦截 WM_ERASEBKGND ★★★
        // Windows 默认会先用背景色清除窗口，这会导致一瞬间的“白屏”或“黑屏”。
        // 我们直接返回 1 (true)，告诉 Windows“我已经擦除过了，你别管”，从而消灭闪烁。
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0014) // WM_ERASEBKGND
            {
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }
    }
}