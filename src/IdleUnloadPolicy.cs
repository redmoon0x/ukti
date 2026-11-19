using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Ukti
{
    static class IdleUnloadPolicy
    {
        public const int DefaultMinutes = 5;
        public static readonly int[] Choices = { 1, 2, 5, 10, 15, 30, 60, 0 };
        public static int Normalize(int minutes) { return minutes >= 0 && minutes <= 1440 ? minutes : DefaultMinutes; }
        public static bool IsDue(int minutes, TimeSpan idle)
        { return minutes > 0 && minutes <= 1440 && idle >= TimeSpan.FromMinutes(minutes); }
        public static string Label(int minutes)
        {
            if (minutes == 0) return "Never";
            return minutes + (minutes == 1 ? " minute" : " minutes") + (minutes == DefaultMinutes ? " (default)" : "");
        }
    }

    sealed class IdleTimeoutRow : ActionControl
    {
        int _minutes = IdleUnloadPolicy.DefaultMinutes;
        public int Minutes
        {
            get { return _minutes; }
            set { _minutes = IdleUnloadPolicy.Normalize(value); AccessibleDescription = IdleUnloadPolicy.Label(_minutes); Invalidate(); }
        }
        public IdleTimeoutRow() { AccessibleName = "Unload model after inactivity"; AccessibleRole = AccessibleRole.ButtonDropDown; Minutes = _minutes; }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Down || key == Keys.Up || key == Keys.Enter || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
            { ShowMenu(); e.Handled = e.SuppressKeyPress = true; return; }
            base.OnKeyDown(e);
        }
        protected override void OnClick(EventArgs e) { ShowMenu(); base.OnClick(e); }
        void ShowMenu()
        {
            ContextMenuStrip menu = CreateDropDown();
            foreach (int minutes in IdleUnloadPolicy.Choices)
            {
                int choice = minutes;
                ToolStripMenuItem item = new ToolStripMenuItem(IdleUnloadPolicy.Label(choice));
                item.Checked = choice == Minutes;
                item.Click += delegate { Minutes = choice; AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1); };
                menu.Items.Add(item);
            }
            menu.Show(this, new Point(0, Height + 4));
            foreach (ToolStripMenuItem item in menu.Items) if (item.Checked) { item.Select(); break; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.DrawSurface(g, new Rectangle(0, 0, Width - 1, Height - 1), Hovered ? Theme.Layer02 : Theme.Layer01, Theme.ControlRadius);
            Theme.DrawText(g, IdleUnloadPolicy.Label(Minutes), 9.5f, Theme.Ink, new Rectangle(12, 0, Width - 40, Height), false);
            using (Pen p = new Pen(Theme.MutedInk, 1.4f))
            {
                int x = Width - 18, y = Height / 2;
                g.DrawLines(p, new Point[] { new Point(x - 4, y - 2), new Point(x, y + 2), new Point(x + 4, y - 2) });
            }
            DrawFocus(g);
        }
    }
}
