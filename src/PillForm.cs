using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Ukti
{
    // The floating status pill, following Hex's capture capsule:
    // a dark rounded plate on a quiet field of dots while listening, and a
    // compact "Processing…" capsule while transcribing.
    //
    // It is click-through and never takes focus, so dictation can begin with
    // the caret already sitting in the target application.
    public sealed class PillForm : Form
    {
        const int WS_EX_TOPMOST = 0x00000008;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_LAYERED = 0x00080000;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int CS_DROPSHADOW = 0x00020000;

        // Hex's capsule proportions, scaled down and simplified for a small
        // always-on-top indicator at the top of the screen.
        const int PlateHeight = 34;
        const int PadX = 12;
        const int DotPlateWidth = 124;
        const int WorkWidth = 132;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
            int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        static extern bool IsWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        const int SWP_NOSIZE = 0x0001;
        const int SWP_NOMOVE = 0x0002;
        const int SWP_NOZORDER = 0x0004;
        const int SWP_NOACTIVATE = 0x0010;

        // Hex's OpenCode dark palette, from HexTheme.swift.
        static readonly Color Plate = Theme.BgDeep;
        static readonly Color TextBase = Theme.TextBase;
        static readonly Color TextMuted = Theme.TextMuted;
        static readonly Color WarmPearl = Theme.WarmPearl;
        static readonly Color AgentBlue = Theme.AgentBlue;

        readonly Timer _timer;
        readonly DotScope _scope = new DotScope();
        readonly Stopwatch _clock = new Stopwatch();

        int _mode;                 // 0 hidden, 1 listening, 2 working
        IntPtr _anchorWindow = IntPtr.Zero;
        string _caption = "";

        public Func<float> CaptureLevel;

        public PillForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Plate;
            DoubleBuffered = true;
            // WS_EX_LAYERED is applied in CreateParams, which makes the window
            // invisible until an alpha is actually set. Setting Opacity here is
            // what calls SetLayeredWindowAttributes.
            Opacity = 0.98;
            Size = new Size(DotPlateWidth + PadX * 2, PlateHeight);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _timer = new Timer();
            _timer.Interval = 16;
            _timer.Tick += OnTick;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int ex = GetWindowLong(Handle, -20);
            SetWindowLong(Handle, -20, ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        }

        public void ShowListening(IntPtr anchor)
        {
            _anchorWindow = anchor;
            _mode = 1;
            _caption = "";
            _scope.Restart();
            _clock.Restart();
            Size = new Size(DotPlateWidth + PadX * 2, PlateHeight);
            Present();
        }

        public void ShowWorking(IntPtr anchor, string caption)
        {
            _anchorWindow = anchor;
            _mode = 2;
            _caption = caption ?? "Processing";
            Size = new Size(WorkWidth, PlateHeight);
            Present();
        }

        public void HidePill()
        {
            _mode = 0;
            _timer.Stop();
            Hide();
        }

        void Present()
        {
            PositionNearAnchor();
            if (!Visible) Show();
            _timer.Start();
            Invalidate();
        }

        // Sits centred at the top of the screen, clear of the menu bar and the
        // target window, so it never covers what is being dictated into.
        void PositionNearAnchor()
        {
            Screen screen = _anchorWindow != IntPtr.Zero && IsWindow(_anchorWindow)
                ? Screen.FromHandle(_anchorWindow)
                : Screen.PrimaryScreen;

            Rectangle wa = screen.WorkingArea;

            int x = wa.Left + (wa.Width - Width) / 2;
            int y = wa.Top + TopMargin;

            Location = new Point(x, y);
            SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        const int TopMargin = 8;

        void OnTick(object sender, EventArgs e)
        {
            if (_mode == 0) return;

            if (_mode == 1)
            {
                float rms = 0f;
                if (CaptureLevel != null)
                {
                    try { rms = CaptureLevel(); }
                    catch { }
                }
                _scope.SetStage(DotScope.Stage.Recording);
                _scope.Advance(0.016f, rms, true);
            }
            else
            {
                _scope.SetStage(DotScope.Stage.Processing);
                _scope.Advance(0.016f, 0f, true);
            }

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Corners outside the capsule must match the desktop, so the window
            // is keyed to the plate colour and the capsule drawn inside it.
            g.Clear(Plate);

            Rectangle body = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.RoundedRect(body, Height / 2))
            {
                using (SolidBrush fill = new SolidBrush(Plate))
                    g.FillPath(fill, path);

                // Hex's hairline rim on a floating surface.
                using (Pen border = new Pen(Theme.BorderStrong, 1f))
                    g.DrawPath(border, path);

                GraphicsState state = g.Save();
                g.SetClip(path);

                if (_mode == 1) DrawDotField(g);
                else DrawWorking(g);

                g.Restore(state);
            }
        }

        // Draws the dot scope inside Hex's recessed well: each dot's power as
        // brightness, warm pearl while listening and cooling to agent blue.
        void DrawDotField(Graphics g)
        {
            float width = Math.Min(_scope.PlateWidth, DotPlateWidth);
            const float height = 24f;

            int columns = _scope.ColumnCount;
            int rows = _scope.RowCount;

            float pitch = width / Math.Max(columns, 1);
            float dotRadius = Math.Max(1.1f, pitch * 0.4f);

            float left = (Width - width) / 2f;
            float top = (Height - height) / 2f;

            // The well sits behind the dots, inset from the capsule edge.
            Rectangle well = new Rectangle(
                (int)left - 6, (int)top - 3, (int)width + 12, (int)height + 6);
            Theme.DrawRecessedWell(g, well);

            Color baseColor = Theme.Blend(WarmPearl, AgentBlue, _scope.Cool);
            float scale = _scope.DisplayScale;
            float midY = top + height / 2f;

            float rowTight = 1f;
            float soft = _scope.Softness;
            if (soft > 0.01f) rowTight = Math.Max(0.35f, 1f - soft * 0.12f);

            for (int i = 0; i < columns; i++)
            {
                float cx = left + (i + 0.5f) * pitch;

                for (int j = 0; j < rows; j++)
                {
                    int n = j * columns + i;
                    float power = _scope.Dots[n * 2];
                    if (power <= 0.004f) continue;

                    float fresh = _scope.Dots[n * 2 + 1];
                    float cy = midY + (j - (rows - 1) / 2f) * pitch * 1.9f * rowTight * scale;

                    // Fresh dots read near-white; the phosphor tail stays tinted.
                    Color c = Theme.Blend(baseColor, Color.White, fresh * 0.6f);
                    int alpha = (int)(Math.Min(power, 1f) * 255f);
                    if (alpha < 6) continue;

                    float r = dotRadius * (0.85f + 0.3f * fresh);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, c)))
                        g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
                }
            }
        }

        void DrawWorking(Graphics g)
        {
            float elapsed = (float)_clock.Elapsed.TotalSeconds;

            // Spinner: six fading spokes, as Hex's processing capsule leads with.
            float cx = PadX + 9f;
            float cy = Height / 2f;
            float radius = 7f;

            for (int i = 0; i < 6; i++)
            {
                float phase = elapsed * 5f + i * 0.55f;
                float pulse = 0.35f + 0.65f * (float)((Math.Sin(phase) + 1) / 2);
                double angle = i * Math.PI / 3;
                float x = cx + (float)Math.Cos(angle) * radius;
                float y = cy + (float)Math.Sin(angle) * radius;
                int alpha = (int)(60 + 175 * pulse);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 205, 215, 235)))
                    g.FillEllipse(b, x - 1.7f, y - 1.7f, 3.4f, 3.4f);
            }

            using (Font f = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(TextBase))
            {
                string text = _caption + "...";
                float textX = cx + radius + 10f;
                SizeF size = g.MeasureString(text, f);
                g.DrawString(text, f, b, textX, (Height - size.Height) / 2f - 1f);
            }
        }
    }
}
