using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Ukti
{
    sealed class ShortcutRecorder : ActionControl
    {
        Shortcut _value = Shortcut.Default;
        bool _recording;
        string _preview;
        public event EventHandler ValueChanged;
        public event Action<string> HintChanged;
        public bool IsRecording { get { return _recording; } }
        public Shortcut Value
        {
            get { return _value; }
            set { _value = value ?? Shortcut.Default; AccessibleDescription = _value + ". Press to change."; Invalidate(); }
        }
        public ShortcutRecorder() { AccessibleName = "Dictation shortcut"; Value = Shortcut.Default; }
        protected override void OnClick(EventArgs e)
        {
            Focus();
            _recording = true;
            _preview = "Press your shortcut\u2026";
            Hint("Press Ctrl or Alt with a key. Esc cancels; Tab moves on.");
            Invalidate();
            base.OnClick(e);
        }
        void Hint(string text) { if (HintChanged != null) HintChanged(text); }
        void StopRecording()
        {
            _recording = false;
            _preview = null;
            Hint("Hold to speak. Release to insert. Esc cancels dictation.");
            Invalidate();
        }
        protected override void OnLostFocus(EventArgs e) { StopRecording(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys keyData)
        { return _recording || (keyData & Keys.KeyCode) == Keys.Enter || base.IsInputKey(keyData); }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_recording) return RecordKey(keyData);
            return base.ProcessCmdKey(ref msg, keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_recording) { RecordKey(e.KeyData); e.Handled = e.SuppressKeyPress = true; return; }
            base.OnKeyDown(e);
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (_recording) { e.Handled = true; return; }
            base.OnKeyUp(e);
        }
        internal bool RecordKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.Escape) { StopRecording(); return true; }
            if (key == Keys.Tab)
            {
                StopRecording();
                if (Parent != null) Parent.SelectNextControl(this, (keyData & Keys.Shift) == 0, true, true, true);
                return true;
            }
            if (key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey ||
                key == Keys.Menu || key == Keys.LMenu || key == Keys.RMenu ||
                key == Keys.ShiftKey || key == Keys.LShiftKey || key == Keys.RShiftKey)
            {
                _preview = ((keyData & Keys.Control) != 0 ? "Ctrl + " : "") +
                    ((keyData & Keys.Alt) != 0 ? "Alt + " : "") + ((keyData & Keys.Shift) != 0 ? "Shift + " : "") + "\u2026";
                Invalidate();
                return true;
            }
            Shortcut candidate;
            string error;
            if (!Shortcut.TryCreate(key, keyData & Keys.Modifiers, out candidate, out error))
            { Hint(error); return true; }
            Value = candidate;
            StopRecording();
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            return true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.DrawSurface(g, bounds, Hovered || _recording ? Theme.Layer02 : Theme.Layer01, Theme.ControlRadius);
            if (_recording)
                using (GraphicsPath path = Theme.RoundedRect(bounds, Theme.ControlRadius))
                using (Pen p = new Pen(Theme.Ink)) g.DrawPath(p, path);
            string text = _recording ? _preview : Value.ToString().Replace("+", " + ");
            Theme.DrawText(g, text, 10f, Theme.Ink, new Rectangle(14, 0, Width - 28, Height), false);
            DrawFocus(g);
        }
    }
}
