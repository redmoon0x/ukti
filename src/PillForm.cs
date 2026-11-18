using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
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
    //
    // The capsule is composited with per-pixel alpha via UpdateLayeredWindow
    // rather than WinForms' Opacity, because constant-alpha layering applies one
    // alpha value to the entire window: the corners outside the rounded capsule
    // stay opaque and read as a dark rectangle around it.
    public sealed class PillForm : Form
    {
        const int WS_EX_TOPMOST = 0x00000008;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_LAYERED = 0x00080000;
        const int WS_EX_TRANSPARENT = 0x00000020;

        const int ULW_ALPHA = 0x00000002;
        const byte AC_SRC_ALPHA = 0x01;
        const byte AC_SRC_OVER = 0x00;

        // Hex's capsule proportions, scaled down and simplified for a small
        // always-on-top indicator at the top of the screen.
        const int PlateHeight = 34;
        const int PadX = 12;
        const int DotPlateWidth = 124;
        const int WorkWidth = 132;
        const int TopMargin = 8;

        // The window is the capsule plus room for its soft shadow.
        const int ShadowMargin = 10;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int CX, CY; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
            ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
            int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);

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
        Size _capsule;

        public Func<float> CaptureLevel;

        public PillForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Black;
            SetStyle(ControlStyles.Opaque, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);

            SetCapsuleSize(DotPlateWidth + PadX * 2, PlateHeight);

            _timer = new Timer();
            _timer.Interval = 16;
            _timer.Tick += OnTick;
        }

        void SetCapsuleSize(int width, int height)
        {
            _capsule = new Size(width, height);
            Size = new Size(width + ShadowMargin * 2, height + ShadowMargin * 2);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // No CS_DROPSHADOW: it shadows the window rectangle rather than
                // the capsule, which would bring back the box being removed.
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
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
            SetCapsuleSize(DotPlateWidth + PadX * 2, PlateHeight);
            Present();
        }

        public void ShowWorking(IntPtr anchor, string caption)
        {
            _anchorWindow = anchor;
            _mode = 2;
            _caption = caption ?? "Processing";
            SetCapsuleSize(WorkWidth, PlateHeight);
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
            Render();
        }

        void PositionNearAnchor()
        {
            Screen screen = _anchorWindow != IntPtr.Zero && IsWindow(_anchorWindow)
                ? Screen.FromHandle(_anchorWindow)
                : Screen.PrimaryScreen;

            Rectangle wa = screen.WorkingArea;
            int x = wa.Left + (wa.Width - Width) / 2;
            int y = wa.Top + TopMargin;
            Location = new Point(x, y);
        }

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

            Render();
        }

        // Paints the capsule into a 32-bit ARGB bitmap and hands it to the
        // compositor. Everything outside the capsule stays fully transparent, so
        // the capsule's edge is the window's edge.
        void Render()
        {
            if (!IsHandleCreated || _mode == 0) return;

            using (Bitmap bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    g.Clear(Color.Transparent);

                    Rectangle capsule = new Rectangle(
                        ShadowMargin, ShadowMargin, _capsule.Width, _capsule.Height);

                    DrawShadow(g, capsule);

                    using (GraphicsPath path = Theme.RoundedRect(capsule, _capsule.Height / 2))
                    {
                        using (SolidBrush fill = new SolidBrush(Theme.BgDeep))
                            g.FillPath(fill, path);

                        using (Pen border = new Pen(Theme.BorderStrong, 1f))
                            g.DrawPath(border, path);

                        GraphicsState state = g.Save();
                        g.SetClip(path, CombineMode.Intersect);
                        if (_mode == 1) DrawDotField(g, capsule);
                        else DrawWorking(g, capsule);
                        g.Restore(state);
                    }
                }

                Composite(bmp);
            }
        }

        // A soft shadow drawn as concentric translucent capsules. Every ring
        // fades out well before the window edge, so the shadow itself cannot
        // show a border.
        static void DrawShadow(Graphics g, Rectangle capsule)
        {
            for (int i = ShadowMargin; i >= 1; i--)
            {
                Rectangle r = new Rectangle(
                    capsule.Left - i, capsule.Top - i + 1,
                    capsule.Width + i * 2, capsule.Height + i * 2);
                using (GraphicsPath p = Theme.RoundedRect(r, r.Height / 2))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(9, 0, 0, 0)))
                    g.FillPath(b, p);
            }
        }

        void Composite(Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;

            try
            {
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                oldBitmap = SelectObject(memDc, hBitmap);

                SIZE size = new SIZE();
                size.CX = bmp.Width;
                size.CY = bmp.Height;

                POINT src = new POINT();
                src.X = 0;
                src.Y = 0;

                POINT dst = new POINT();
                dst.X = Left;
                dst.Y = Top;

                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AC_SRC_ALPHA;

                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc,
                    ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Content comes from UpdateLayeredWindow; painting here would lay an
            // opaque rectangle over the composited capsule.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
        }

        // Draws the dot scope inside Hex's recessed well.
        void DrawDotField(Graphics g, Rectangle capsule)
        {
            float width = Math.Min(_scope.PlateWidth, DotPlateWidth);
            const float height = 24f;

            int columns = _scope.ColumnCount;
            int rows = _scope.RowCount;

            float pitch = width / Math.Max(columns, 1);
            float dotRadius = Math.Max(1.1f, pitch * 0.4f);

            float left = capsule.Left + (capsule.Width - width) / 2f;
            float top = capsule.Top + (capsule.Height - height) / 2f;

            Rectangle well = new Rectangle(
                (int)left - 6, (int)top - 3, (int)width + 12, (int)height + 6);
            Theme.DrawRecessedWell(g, well);

            Color baseColor = Theme.Blend(Theme.WarmPearl, Theme.AgentBlue, _scope.Cool);
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

        void DrawWorking(Graphics g, Rectangle capsule)
        {
            float elapsed = (float)_clock.Elapsed.TotalSeconds;

            float cx = capsule.Left + PadX + 9f;
            float cy = capsule.Top + capsule.Height / 2f;
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
                g.DrawString(text, f, b, textX, cy - size.Height / 2f - 1f);
            }
        }
    }
}
