using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Ukti
{
    // The preferences window, styled after Hex's settings surfaces: a dark
    // single-temperature palette, layered cards, and text by opacity.
    //
    // Stock WinForms controls cannot look like this, so the interactive pieces
    // are custom-drawn: a switch, radio rows, and a select. They keep keyboard
    // and accessibility behaviour by wrapping real controls underneath.
    sealed class SettingsForm : Form
    {
        public int SelectedIndex { get; private set; }
        public bool EnabledNow { get; private set; }
        public bool StartWithWindows { get; private set; }
        public bool KeepRunning { get; private set; }

        readonly ToggleSwitch _enabled;
        readonly DeviceRow _device;
        readonly RadioRow _startAuto;
        readonly RadioRow _startManual;
        readonly RadioRow _keepRunning;
        readonly RadioRow _quitOnClose;
        readonly HexButton _save;
        readonly HexButton _cancel;
        readonly List<RadioRow> _startGroup = new List<RadioRow>();
        readonly List<RadioRow> _closeGroup = new List<RadioRow>();

        public SettingsForm(Settings current)
        {
            Text = "Ukti";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(496, 560);
            Font = new Font("Segoe UI", 9f);
            BackColor = Theme.BgBase;
            ShowInTaskbar = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            int y = 62;

            // ---- enable ----
            _enabled = new ToggleSwitch();
            _enabled.Checked = current.Enabled;
            _enabled.Bounds = new Rectangle(430, y + 2, 44, 24);
            Controls.Add(_enabled);
            _enableTop = y;

            y += 48;
            _micLabelTop = y;
            y += 24;

            _device = new DeviceRow();
            _device.Bounds = new Rectangle(20, y, 456, 40);
            _device.SelectedIndex = current.MicrophoneIndex;
            _device.SelectionChanged += delegate { Invalidate(); };
            Controls.Add(_device);
            _deviceTop = y;

            y += 62;
            _startLabelTop = y;
            y += 24;

            _startAuto = new RadioRow();
            _startAuto.Text = "Start with Windows";
            _startAuto.Bounds = new Rectangle(20, y, 456, 36);
            _startAuto.Checked = current.RunAtStartup;
            _startAuto.Click += delegate { SelectRadio(_startGroup, _startAuto); };
            Controls.Add(_startAuto);
            _startGroup.Add(_startAuto);

            y += 40;
            _startManual = new RadioRow();
            _startManual.Text = "Only when I open it";
            _startManual.Bounds = new Rectangle(20, y, 456, 36);
            _startManual.Checked = !current.RunAtStartup;
            _startManual.Click += delegate { SelectRadio(_startGroup, _startManual); };
            Controls.Add(_startManual);
            _startGroup.Add(_startManual);

            y += 56;
            _closeLabelTop = y;
            y += 24;

            _keepRunning = new RadioRow();
            _keepRunning.Text = "Keep running in the background";
            _keepRunning.Bounds = new Rectangle(20, y, 456, 36);
            _keepRunning.Checked = current.RunInBackground;
            _keepRunning.Click += delegate { SelectRadio(_closeGroup, _keepRunning); };
            Controls.Add(_keepRunning);
            _closeGroup.Add(_keepRunning);

            y += 40;
            _quitOnClose = new RadioRow();
            _quitOnClose.Text = "Quit Ukti";
            _quitOnClose.Bounds = new Rectangle(20, y, 456, 36);
            _quitOnClose.Checked = !current.RunInBackground;
            _quitOnClose.Click += delegate { SelectRadio(_closeGroup, _quitOnClose); };
            Controls.Add(_quitOnClose);
            _closeGroup.Add(_quitOnClose);

            int buttonsTop = y + 58;
            ClientSize = new Size(496, buttonsTop + 52);

            _save = new HexButton();
            _save.Text = "Save";
            _save.Primary = true;
            _save.Bounds = new Rectangle(292, buttonsTop, 88, 32);
            _save.Click += delegate { Accept(); };
            Controls.Add(_save);

            _cancel = new HexButton();
            _cancel.Text = "Cancel";
            _cancel.Bounds = new Rectangle(388, buttonsTop, 88, 32);
            _cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_cancel);

            AcceptButton = null;
            KeyPreview = true;
            KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
        }

        int _enableTop, _micLabelTop, _deviceTop, _startLabelTop, _closeLabelTop;

        void SelectRadio(List<RadioRow> group, RadioRow chosen)
        {
            foreach (RadioRow row in group) row.Checked = row == chosen;
            Invalidate();
        }

        void Accept()
        {
            SelectedIndex = _device.SelectedIndex;
            EnabledNow = _enabled.Checked;
            StartWithWindows = _startAuto.Checked;
            KeepRunning = _keepRunning.Checked;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.BgBase);

            // Title row: the Kannada mark beside the product name.
            Theme.DrawMark(g, new RectangleF(20, 18, 24, 20), Theme.TextBase);

            using (Font title = new Font("Segoe UI", 11f, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(Theme.TextBase))
                g.DrawString("Ukti", title, b, 52, 19);

            // Close button.
            using (Font f = new Font("Segoe UI", 10f))
            using (SolidBrush b = new SolidBrush(Theme.TextMuted))
                g.DrawString("\u2715", f, b, 458, 20);

            using (Pen p = new Pen(Theme.BorderWeak, 1f))
                g.DrawLine(p, 20, 50, 476, 50);

            // Enable row.
            using (Font bold = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(Theme.TextBase))
                g.DrawString("Enable Ukti", bold, b, 20, _enableTop + 4);

            using (Font small = new Font("Segoe UI", 8.25f))
            using (SolidBrush b = new SolidBrush(Theme.TextMuted))
                g.DrawString("Hold Ctrl+Space to dictate. Release to insert.",
                             small, b, 20, _enableTop + 24);

            DrawSection(g, "MICROPHONE", _micLabelTop);
            DrawSection(g, "WHEN WINDOWS STARTS", _startLabelTop);
            DrawSection(g, "WHEN THE WINDOW IS CLOSED", _closeLabelTop);

            // Device note, under the select.
            if (_device.Note != null)
            {
                using (Font small = new Font("Segoe UI", 8.25f))
                using (SolidBrush b = new SolidBrush(_device.NoteIsWarning ? Theme.WarmMic : Theme.TextFaint))
                    g.DrawString(_device.Note, small, b, 22, _deviceTop + 46);
            }
        }

        static void DrawSection(Graphics g, string title, int y)
        {
            using (Font f = new Font("Segoe UI", 7.5f, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(Theme.TextFaint))
            {
                // Slight tracking is what makes these read as small caps labels.
                g.DrawString(Spaced(title), f, b, 21, y);
            }
        }

        static string Spaced(string s)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                sb.Append(s[i]);
                if (i < s.Length - 1) sb.Append(' ');
            }
            return sb.ToString();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // Frameless window: allow dragging by the title band.
            if (e.Y < 46 && e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)0x2, IntPtr.Zero);
            }
            base.OnMouseDown(e);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    // Hex's contrast switch: a track that fills with the contrast colour and a
    // knob that slides, not a checkbox.
    sealed class ToggleSwitch : Control
    {
        bool _checked;
        float _phase;

        public bool Checked
        {
            get { return _checked; }
            set { _checked = value; _phase = value ? 1f : 0f; Invalidate(); }
        }

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        protected override void OnClick(EventArgs e)
        {
            _checked = !_checked;
            Animate();
            base.OnClick(e);
        }

        void Animate()
        {
            // A short settle, matching Hex's spring duration rather than a snap.
            Timer t = new Timer();
            t.Interval = 15;
            float target = _checked ? 1f : 0f;
            t.Tick += delegate
            {
                _phase += (target - _phase) * 0.32f;
                if (Math.Abs(target - _phase) < 0.02f) { _phase = target; t.Stop(); t.Dispose(); }
                Invalidate();
            };
            t.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = Theme.Blend(Theme.Alpha(Color.White, 0.10f), Theme.BgContrast, _phase);

            using (GraphicsPath path = Theme.RoundedRect(track, Height / 2))
            using (SolidBrush b = new SolidBrush(fill))
                g.FillPath(b, path);

            int knob = Height - 6;
            float x = 3 + (Width - knob - 6) * _phase;
            Color knobColor = Theme.Blend(Theme.TextMuted, Theme.TextContrast, _phase);

            using (SolidBrush b = new SolidBrush(knobColor))
                g.FillEllipse(b, x, 3, knob, knob);
        }
    }

    // A single radio row: label plus a ring that fills when chosen.
    sealed class RadioRow : Control
    {
        bool _checked;

        public bool Checked
        {
            get { return _checked; }
            set { _checked = value; Invalidate(); }
        }

        public RadioRow()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            ForeColor = Theme.TextBase;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int cy = Height / 2;
            Rectangle ring = new Rectangle(2, cy - 7, 15, 15);

            using (SolidBrush bg = new SolidBrush(Theme.Alpha(Color.White, 0.05f)))
                g.FillEllipse(bg, ring);
            using (Pen p = new Pen(_checked ? Theme.BgContrast : Theme.BorderStrong, 1.3f))
                g.DrawEllipse(p, ring);

            if (_checked)
            {
                using (SolidBrush dot = new SolidBrush(Theme.BgContrast))
                    g.FillEllipse(dot, ring.X + 4, ring.Y + 4, 7, 7);
            }

            using (Font f = new Font("Segoe UI", 9.5f))
            using (SolidBrush b = new SolidBrush(_checked ? Theme.TextBase : Theme.TextMuted))
                g.DrawString(Text, f, b, 28, cy - 9);
        }
    }

    // The microphone select: a custom closed control that opens a themed list.
    sealed class DeviceRow : Control
    {
        readonly List<int> _indices = new List<int>();
        int _selected = -1;
        string _note;
        bool _warning;

        public event EventHandler SelectionChanged;

        // -1 means "System default".
        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                _selected = value;
                UpdateNote();
                Invalidate();
            }
        }

        public string Note { get { return _note; } }
        public bool NoteIsWarning { get { return _warning; } }

        public DeviceRow()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        static string LabelFor(int index)
        {
            if (index < 0) return "System default";
            return AudioCapture.DeviceName(index);
        }

        void UpdateNote()
        {
            string name = LabelFor(_selected);
            bool bluetooth = name.IndexOf("bluetooth", StringComparison.OrdinalIgnoreCase) >= 0
                          || name.IndexOf("hands-free", StringComparison.OrdinalIgnoreCase) >= 0
                          || name.IndexOf("headset", StringComparison.OrdinalIgnoreCase) >= 0;

            if (bluetooth)
            {
                _note = "Headsets often capture narrowband audio, which transcribes less accurately.";
                _warning = true;
            }
            else if (_selected < 0)
            {
                _note = "Follows whatever Windows has set as the default input.";
                _warning = false;
            }
            else
            {
                _note = null;
                _warning = false;
            }
        }

        protected override void OnClick(EventArgs e)
        {
            ShowMenu();
            base.OnClick(e);
        }

        void ShowMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new HexMenuRenderer();
            menu.Font = new Font("Segoe UI", 9.5f);
            menu.ShowImageMargin = false;
            menu.BackColor = Theme.Layer02;
            menu.ForeColor = Theme.TextBase;

            int count = AudioCapture.DeviceCount();
            List<int> options = new List<int>();
            options.Add(-1);
            for (int i = 0; i < count; i++) options.Add(i);

            foreach (int index in options)
            {
                int captured = index;
                ToolStripMenuItem item = new ToolStripMenuItem(LabelFor(index));
                item.Checked = index == _selected;
                item.ForeColor = Theme.TextBase;
                item.Click += delegate
                {
                    _selected = captured;
                    UpdateNote();
                    Invalidate();
                    EventHandler handler = SelectionChanged;
                    if (handler != null) handler(this, EventArgs.Empty);
                };
                menu.Items.Add(item);
            }

            menu.Show(this, new Point(0, Height + 4));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.RoundedRect(bounds, Theme.ControlRadius))
            {
                using (SolidBrush b = new SolidBrush(Theme.Layer01))
                    g.FillPath(b, path);
                using (Pen p = new Pen(Theme.BorderWeak, 1f))
                    g.DrawPath(p, path);
            }

            using (Font f = new Font("Segoe UI", 9.5f))
            using (SolidBrush b = new SolidBrush(Theme.TextBase))
                g.DrawString(LabelFor(_selected), f, b, 14, Height / 2f - 10);

            // Chevron.
            using (Pen p = new Pen(Theme.TextMuted, 1.5f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                float cx = Width - 20, cy = Height / 2f;
                g.DrawLine(p, cx - 4, cy - 2, cx, cy + 2);
                g.DrawLine(p, cx, cy + 2, cx + 4, cy - 2);
            }
        }
    }

    // Hex's pill button: filled contrast for the primary action, a quiet layer
    // for the secondary.
    sealed class HexButton : Control
    {
        public bool Primary;

        public HexButton()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            ForeColor = Theme.TextBase;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = Primary ? Theme.BgContrast : Theme.Alpha(Color.White, 0.07f);
            Color ink = Primary ? Theme.TextContrast : Theme.TextBase;

            using (GraphicsPath path = Theme.RoundedRect(bounds, Theme.ControlRadius))
            using (SolidBrush b = new SolidBrush(fill))
                g.FillPath(b, path);

            using (Font f = new Font("Segoe UI", 9f, Primary ? FontStyle.Bold : FontStyle.Regular))
            using (SolidBrush b = new SolidBrush(ink))
            {
                SizeF size = g.MeasureString(Text, f);
                g.DrawString(Text, f, b, (Width - size.Width) / 2f, (Height - size.Height) / 2f);
            }
        }
    }

    // Dark menu rendering so the device list matches the window.
    sealed class HexMenuRenderer : ToolStripProfessionalRenderer
    {
        public HexMenuRenderer() : base(new HexColours()) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Rectangle r = new Rectangle(Point.Empty, e.Item.Size);
            using (SolidBrush b = new SolidBrush(e.Item.Selected ? Theme.Alpha(Color.White, 0.10f) : Theme.Layer02))
                e.Graphics.FillRectangle(b, r);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Theme.TextBase;
            base.OnRenderItemText(e);
        }
    }

    sealed class HexColours : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Theme.Alpha(Color.White, 0.10f); } }
        public override Color MenuItemBorder { get { return Theme.BorderWeak; } }
        public override Color ToolStripDropDownBackground { get { return Theme.Layer02; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Layer02; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Layer02; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Layer02; } }
    }
}
