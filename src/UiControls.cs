using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Ukti
{
    // Shared window chrome. Closing a preferences dialog still cancels unsaved
    // edits; this class does not change Ukti's process/tray lifecycle.
    class ThemedForm : Form
    {
        public bool WindowCloseRequested { get; private set; }
        // While set, keys matching the dictation shortcut are swallowed so
        // they cannot toggle focused controls or open menus in this dialog.
        public Func<Keys, bool> SuppressKey;
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (SuppressKey != null && SuppressKey(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }
        protected ThemedForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9f);
            BackColor = Theme.BgDeep;
            ForeColor = Theme.Ink;
            DoubleBuffered = true;
            ShowInTaskbar = true;
            Icon = Theme.AppIcon;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected Label AddLabel(string text, Rectangle bounds, float size, Color ink, bool bold)
        {
            Label label = new Label();
            label.Text = text;
            label.Bounds = bounds;
            label.Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
            label.ForeColor = ink;
            label.BackColor = Color.Transparent;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.AutoEllipsis = true;
            Controls.Add(label);
            return label;
        }

        protected HexButton AddCloseButton(int x)
        {
            HexButton close = new HexButton();
            close.Text = "\u00d7";
            close.AccessibleName = "Close";
            close.Quiet = true;
            close.Bounds = new Rectangle(x, 20, 28, 28);
            close.Click += delegate { WindowCloseRequested = true; DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(close);
            return close;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
            using (GraphicsPath path = Theme.RoundedRect(ClientRectangle, 14))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.DrawSurface(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Theme.BgDeep, 14);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Y < 62 && e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x112 && (message.WParam.ToInt64() & 0xFFF0) == 0xF060)
                WindowCloseRequested = true; // Alt+F4 / taskbar Close, not Save or Cancel.
            base.WndProc(ref message);
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    // A focusable native Control, not just a painted hit target. All controls
    // share hover, pressed, keyboard and focus feedback and an accessible action.
    class ActionControl : Control
    {
        protected bool Hovered, Pressed;
        ContextMenuStrip _dropDown;

        // Closed fires before ToolStrip has finished raising the item Click.
        // Keep the menu alive until the next opening or its owner's disposal.
        protected ContextMenuStrip CreateDropDown()
        {
            if (_dropDown != null) _dropDown.Dispose();
            _dropDown = new ContextMenuStrip();
            HexMenuRenderer.Style(_dropDown);
            _dropDown.MinimumSize = new Size(Width, 0);
            return _dropDown;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && _dropDown != null) { _dropDown.Dispose(); _dropDown = null; }
            base.Dispose(disposing);
        }

        public ActionControl()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor |
                     ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            ForeColor = Theme.Ink;
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
        }

        public void ActivateControl()
        {
            if (Enabled && !IsDisposed) OnClick(EventArgs.Empty);
        }

        protected override void OnMouseEnter(EventArgs e) { Hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { Focus(); Pressed = true; Invalidate(); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { Pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseCaptureChanged(EventArgs e) { Pressed = false; Invalidate(); base.OnMouseCaptureChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Pressed = false; Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                if (!Pressed) { Pressed = true; Invalidate(); }
                // Keep KeyUp: SuppressKeyPress also suppresses it in WinForms,
                // which would leave the control pressed without activating.
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && Pressed)
            {
                Pressed = false;
                ActivateControl();
                Invalidate();
                e.Handled = true;
            }
            base.OnKeyUp(e);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (e.KeyChar == ' ' || e.KeyChar == '\r') e.Handled = true;
            base.OnKeyPress(e);
        }

        protected void DrawFocus(Graphics g)
        {
            if (!Focused || !ShowFocusCues) return;
            using (GraphicsPath path = Theme.RoundedRect(new Rectangle(1, 1, Width - 3, Height - 3), 7))
            using (Pen p = new Pen(Theme.Ink)) { p.DashStyle = DashStyle.Dot; g.DrawPath(p, path); }
        }

        protected override AccessibleObject CreateAccessibilityInstance() { return new ActionAccessibleObject(this); }

        class ActionAccessibleObject : ControlAccessibleObject
        {
            readonly ActionControl _owner;
            public ActionAccessibleObject(ActionControl owner) : base(owner) { _owner = owner; }
            public override string DefaultAction { get { return "Press"; } }
            public override void DoDefaultAction() { _owner.ActivateControl(); }
        }
    }

    sealed class HexButton : ActionControl, IButtonControl
    {
        public bool Primary, Quiet;
        bool _default;
        public DialogResult DialogResult { get; set; }
        public void NotifyDefault(bool value) { _default = value; Invalidate(); }
        public void PerformClick() { ActivateControl(); }

        protected override void OnClick(EventArgs e)
        {
            Form form = FindForm();
            if (form != null && DialogResult != DialogResult.None) form.DialogResult = DialogResult;
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = Primary ? Theme.BgContrast : Theme.Layer02;
            if (Quiet) fill = Color.Transparent;
            if (Hovered && Enabled) fill = Primary ? Color.White : Theme.Blend(Theme.Layer02, Color.White, 0.08f);
            if (Pressed) fill = Primary ? Color.FromArgb(205, 205, 205) : Theme.Layer01;
            if (!Enabled) fill = Theme.Layer02;
            using (GraphicsPath path = Theme.RoundedRect(bounds, Theme.ControlRadius))
            using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
            if (_default && !Primary)
                using (GraphicsPath path = Theme.RoundedRect(bounds, Theme.ControlRadius))
                using (Pen p = new Pen(Theme.BorderStrong)) g.DrawPath(p, path);
            using (Font font = new Font("Segoe UI", Quiet ? 15f : 9f, Primary ? FontStyle.Bold : FontStyle.Regular))
                TextRenderer.DrawText(g, Text, font, bounds,
                    !Enabled ? Theme.FaintInk : Primary ? Theme.TextContrast : Theme.Ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            DrawFocus(g);
        }
    }

    sealed class ToggleSwitch : ActionControl
    {
        bool _checked;
        float _phase;
        readonly Timer _animation = new Timer();
        public bool Checked
        {
            get { return _checked; }
            set { _checked = value; _phase = value ? 1 : 0; _animation.Stop(); Invalidate(); }
        }

        public ToggleSwitch()
        {
            AccessibleRole = AccessibleRole.CheckButton;
            _animation.Interval = 15;
            _animation.Tick += delegate
            {
                float target = _checked ? 1 : 0;
                _phase += (target - _phase) * 0.32f;
                if (Math.Abs(target - _phase) < 0.02f) { _phase = target; _animation.Stop(); }
                Invalidate();
            };
        }

        protected override void OnClick(EventArgs e)
        {
            _checked = !_checked;
            _animation.Start();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle track = new Rectangle(4, 4, Width - 9, Height - 9);
            Color fill = Theme.Blend(Theme.Layer02, Color.FromArgb(230, 230, 230), _phase);
            if (Hovered && Enabled) fill = Theme.Blend(fill, Color.White, 0.12f);
            if (!Enabled) fill = Theme.Layer02;
            using (GraphicsPath path = Theme.RoundedRect(track, track.Height / 2))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                using (Pen p = new Pen(Theme.BorderStrong)) g.DrawPath(p, path);
            }
            int knob = track.Height - 4;
            float x = track.Left + 2 + (track.Width - knob - 4) * _phase;
            using (SolidBrush b = new SolidBrush(Theme.Blend(Color.White, Theme.BgDeep, _phase)))
                g.FillEllipse(b, x, track.Top + 2, knob, knob);
            DrawFocus(g);
        }

        protected override AccessibleObject CreateAccessibilityInstance() { return new SwitchAccessibleObject(this); }
        sealed class SwitchAccessibleObject : ControlAccessibleObject
        {
            readonly ToggleSwitch _owner;
            public SwitchAccessibleObject(ToggleSwitch owner) : base(owner) { _owner = owner; }
            public override AccessibleStates State { get { return base.State | (_owner.Checked ? AccessibleStates.Checked : 0); } }
            public override string DefaultAction { get { return "Toggle"; } }
            public override void DoDefaultAction() { _owner.ActivateControl(); }
        }
        protected override void Dispose(bool disposing) { if (disposing) _animation.Dispose(); base.Dispose(disposing); }
    }

    sealed class DeviceRow : ActionControl
    {
        int _selected = -1;
        public event EventHandler SelectionChanged;
        public int SelectedIndex
        {
            get { return _selected; }
            set { _selected = value; UpdateNote(); Invalidate(); }
        }
        public string Note { get; private set; }
        public bool NoteIsWarning { get; private set; }

        public DeviceRow() { AccessibleName = "Microphone"; AccessibleRole = AccessibleRole.ButtonDropDown; UpdateNote(); }
        static string LabelFor(int index) { return index < 0 ? "System default" : AudioCapture.DeviceName(index); }
        void UpdateNote()
        {
            string name = LabelFor(_selected);
            NoteIsWarning = name.IndexOf("bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("hands-free", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("headset", StringComparison.OrdinalIgnoreCase) >= 0;
            Note = NoteIsWarning ? "Headset audio can reduce accuracy. Try a built-in microphone." :
                _selected < 0 ? "Follows the default input in Windows. Save changes to apply." : "Save changes to use this input device.";
            AccessibleDescription = name + ". " + Note;
        }
        protected override void OnClick(EventArgs e) { ShowMenu(); base.OnClick(e); }
        protected override bool IsInputKey(Keys keyData)
        {
            return (keyData & Keys.KeyCode) == Keys.Down || (keyData & Keys.KeyCode) == Keys.Up ||
                (keyData & Keys.KeyCode) == Keys.Enter || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
            { ShowMenu(); e.Handled = e.SuppressKeyPress = true; return; }
            base.OnKeyDown(e);
        }
        void ShowMenu()
        {
            ContextMenuStrip menu = CreateDropDown();
            List<int> options = new List<int>();
            options.Add(-1);
            for (int i = 0; i < AudioCapture.DeviceCount(); i++) options.Add(i);
            ToolStripMenuItem selected = null;
            foreach (int index in options)
            {
                int captured = index;
                ToolStripMenuItem item = new ToolStripMenuItem(LabelFor(index));
                item.Checked = index == _selected;
                if (item.Checked) selected = item;
                item.Click += delegate
                {
                    SelectedIndex = captured;
                    if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
                    AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
                };
                menu.Items.Add(item);
            }
            menu.Show(this, new Point(0, Height + 4));
            if (selected != null) selected.Select();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.DrawSurface(g, new Rectangle(0, 0, Width - 1, Height - 1),
                Hovered ? Theme.Layer02 : Theme.Layer01, Theme.ControlRadius);
            Theme.DrawText(g, LabelFor(_selected), 9.5f, Theme.Ink, new Rectangle(12, 0, Width - 42, Height), false);
            using (Pen p = new Pen(Theme.MutedInk, 1.4f))
            {
                float x = Width - 18, y = Height / 2f;
                g.DrawLines(p, new PointF[] { new PointF(x - 4, y - 2), new PointF(x, y + 2), new PointF(x + 4, y - 2) });
            }
            DrawFocus(g);
        }
    }

    sealed class HexMenuRenderer : ToolStripProfessionalRenderer
    {
        public HexMenuRenderer() : base(new HexColours()) { RoundedEdges = false; }
        public static void Style(ContextMenuStrip menu)
        {
            menu.Renderer = new HexMenuRenderer();
            menu.Font = new Font("Segoe UI", 9.5f);
            menu.BackColor = Theme.Layer01;
            menu.ForeColor = Theme.Ink;
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            menu.Padding = new Padding(4);
        }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Rectangle r = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            using (GraphicsPath path = Theme.RoundedRect(r, 5))
            using (SolidBrush b = new SolidBrush(e.Item.Selected && e.Item.Enabled ? Theme.Layer02 : Theme.Layer01))
                e.Graphics.FillPath(b, path);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Ink : Theme.FaintInk;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (Pen p = new Pen(Theme.BorderStrong)) e.Graphics.DrawLine(p, 8, 3, e.Item.Width - 8, 3);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            using (Pen p = new Pen(Theme.Ink, 1.5f))
                e.Graphics.DrawLines(p, new Point[] { new Point(r.Left + 2, r.Top + 7),
                    new Point(r.Left + 6, r.Top + 11), new Point(r.Left + 13, r.Top + 4) });
        }
    }
    sealed class HexColours : ProfessionalColorTable
    {
        public override Color MenuBorder { get { return Theme.Blend(Theme.Layer01, Color.White, 0.12f); } }
        public override Color ToolStripDropDownBackground { get { return Theme.Layer01; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Layer01; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Layer01; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Layer01; } }
    }
}
