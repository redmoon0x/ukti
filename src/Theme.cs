using System;
using System.Drawing;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Ukti
{
    // Neutral surfaces from Hex's current Rune settings UI. Keep the Windows
    // implementation native and retain Ukti's own mark and existing controls.
    static class Theme
    {
        public static readonly Color BgDeep = Color.FromArgb(0x08, 0x08, 0x08);
        public static readonly Color BgBase = Color.FromArgb(0x15, 0x15, 0x15);
        public static readonly Color Layer01 = Color.FromArgb(0x1C, 0x1C, 0x1C);
        public static readonly Color Layer02 = Color.FromArgb(0x2C, 0x2C, 0x2C);

        // Text is white at fixed opacities rather than separate greys, so it
        // composites correctly on every layer.
        public static readonly Color TextBase = Alpha(Color.White, 0.94f);
        public static readonly Color TextMuted = Alpha(Color.White, 0.56f);
        public static readonly Color TextFaint = Alpha(Color.White, 0.36f);

        public static readonly Color BorderWeak = Alpha(Color.White, 0.06f);
        public static readonly Color BorderStrong = Alpha(Color.White, 0.11f);

        public static readonly Color BgContrast = Alpha(Color.White, 0.96f);
        public static readonly Color TextContrast = BgDeep;

        public static readonly Color Danger = Color.FromArgb(0xE5, 0x4D, 0x42);
        public static readonly Color Success = Color.FromArgb(0x34, 0xC7, 0x59);
        public static readonly Color WarmMic = Color.FromArgb(0xFF, 0x95, 0x00);

        // Listening reads warm pearl; working cools to agent blue.
        public static readonly Color WarmPearl = Color.FromArgb(245, 240, 227);
        public static readonly Color AgentBlue = Color.FromArgb(199, 219, 247);

        public const int CardRadius = 12;
        public const int ControlRadius = 9;

        // Native controls require opaque foregrounds; alpha text is only for GDI+.
        public static readonly Color Ink = Blend(BgBase, Color.White, 0.94f);
        public static readonly Color MutedInk = Blend(BgBase, Color.White, 0.64f);
        public static readonly Color FaintInk = Blend(BgBase, Color.White, 0.44f);

        public static void DrawSurface(Graphics g, Rectangle bounds, Color fill, int radius)
        {
            using (GraphicsPath path = RoundedRect(bounds, radius))
            {
                using (SolidBrush brush = new SolidBrush(fill)) g.FillPath(brush, path);
                using (Pen pen = new Pen(BorderStrong)) g.DrawPath(pen, path);
            }
        }

        public static void DrawText(Graphics g, string text, float size, Color color,
                                    Rectangle bounds, bool bold)
        {
            using (Font font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular))
                TextRenderer.DrawText(g, text, font, bounds, color,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        public static void DrawKey(Graphics g, string text, Rectangle bounds)
        {
            DrawSurface(g, bounds, Layer02, 6);
            using (Pen p = new Pen(Alpha(Color.Black, 0.6f), 2))
                g.DrawLine(p, bounds.Left + 5, bounds.Bottom - 2, bounds.Right - 5, bounds.Bottom - 2);
            using (Font font = new Font("Segoe UI", 9f))
                TextRenderer.DrawText(g, text, font, bounds, Ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        public static Color Alpha(Color c, float a)
        {
            int v = (int)Math.Round(a * 255f);
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            return Color.FromArgb(v, c.R, c.G, c.B);
        }

        public static Color Blend(Color a, Color b, float t)
        {
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Max(radius * 2, 2);
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // Hex's recessed well: a soft inset capsule for the dot scope, without an
        // opaque hole or a heavy stroke.
        public static void DrawRecessedWell(Graphics g, Rectangle bounds)
        {
            using (GraphicsPath path = RoundedRect(bounds, bounds.Height / 2))
            {
                using (SolidBrush fill = new SolidBrush(Alpha(Color.White, 0.05f)))
                    g.FillPath(fill, path);

                using (LinearGradientBrush stroke = new LinearGradientBrush(
                    bounds,
                    Alpha(Color.Black, 0.30f),
                    Alpha(Color.White, 0.065f),
                    LinearGradientMode.Vertical))
                using (Pen pen = new Pen(stroke, 0.75f))
                    g.DrawPath(pen, path);
            }
        }

        static Icon _appIcon;
        public static Icon AppIcon
        {
            get
            {
                if (_appIcon == null)
                {
                    using (System.IO.Stream stream = typeof(Theme).Assembly.GetManifestResourceStream("ukti.ico"))
                    using (Icon icon = new Icon(stream)) _appIcon = (Icon)icon.Clone();
                }
                return _appIcon;
            }
        }

        // The supplied Kannada voice-recording artwork, with its black canvas
        // removed and preserved as an antialiased alpha mark for every surface.
        public static void DrawMark(Graphics g, RectangleF bounds, Color color)
        {
            Bitmap glyph = MarkGlyph;
            if (glyph == null)
            {
                DrawFallbackMark(g, bounds, color);
                return;
            }

            float side = Math.Min(bounds.Width, bounds.Height);
            float scale = side / glyph.Height;
            float w = glyph.Width * scale;
            float x = bounds.Left + (bounds.Width - w) / 2f;

            if (side <= 0) return;
            int pixels = Math.Max(1, Math.Min(256, (int)Math.Ceiling(side)));
            long key = ((long)color.ToArgb() << 32) | (uint)pixels;
            Bitmap tinted;
            lock (_markRenders)
            {
                if (!_markRenders.TryGetValue(key, out tinted))
                {
                    // Cache at the actual UI size rather than allocating a
                    // 512px tinted bitmap on every animation frame.
                    using (Bitmap scaled = new Bitmap(pixels, pixels))
                    {
                        using (Graphics draw = Graphics.FromImage(scaled))
                        {
                            draw.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            draw.DrawImage(glyph, new Rectangle(0, 0, pixels, pixels));
                        }
                        tinted = Tint(scaled, color);
                    }
                    _markRenders.Add(key, tinted);
                }
            }
            g.DrawImage(tinted, x, bounds.Top, w, side);
        }

        static readonly Dictionary<long, Bitmap> _markRenders = new Dictionary<long, Bitmap>();
        static Bitmap _markGlyph;
        static bool _markLoaded;

        // The embedded glyph is white with a true alpha channel; tinting is a
        // colour wash combined with that alpha, so edges stay clean.
        public static Bitmap MarkGlyph
        {
            get
            {
                if (!_markLoaded)
                {
                    _markGlyph = LoadEmbedded("ukti-glyph.png");
                    _markLoaded = true;
                }
                return _markGlyph;
            }
        }

        static Bitmap LoadEmbedded(string name)
        {
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();

                // /resource embeds the file under the name given on the command
                // line, which has no namespace prefix. Match on the file name so
                // the lookup works whether or not a prefix is present.
                string found = null;
                foreach (string candidate in asm.GetManifestResourceNames())
                {
                    if (candidate == name ||
                        candidate.EndsWith("." + name, StringComparison.OrdinalIgnoreCase))
                    {
                        found = candidate;
                        break;
                    }
                }
                if (found == null) return null;

                using (System.IO.Stream s = asm.GetManifestResourceStream(found))
                {
                    if (s == null) return null;
                    using (Bitmap raw = new Bitmap(s))
                        return new Bitmap(raw);
                }
            }
            catch
            {
                return null;
            }
        }

        public static Bitmap Tint(Bitmap glyph, Color color)
        {
            Bitmap bmp = new Bitmap(glyph.Width, glyph.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                System.Drawing.Imaging.ColorMatrix matrix = new System.Drawing.Imaging.ColorMatrix();
                matrix.Matrix00 = color.R / 255f;
                matrix.Matrix11 = color.G / 255f;
                matrix.Matrix22 = color.B / 255f;
                matrix.Matrix33 = color.A / 255f;

                using (System.Drawing.Imaging.ImageAttributes attrs =
                       new System.Drawing.Imaging.ImageAttributes())
                {
                    attrs.SetColorMatrix(matrix);
                    g.DrawImage(glyph, new Rectangle(0, 0, glyph.Width, glyph.Height),
                        0, 0, glyph.Width, glyph.Height, GraphicsUnit.Pixel, attrs);
                }
            }
            return bmp;
        }

        // Fallback mark, used only if the embedded glyph cannot be loaded.
        static void DrawFallbackMark(Graphics g, RectangleF bounds, Color color)
        {
            float side = Math.Min(bounds.Width, bounds.Height);
            float s = side / 64f;
            float ox = bounds.Left + (bounds.Width - side) / 2f;
            float oy = bounds.Top + (bounds.Height - side) / 2f;
            float lineWidth = Math.Max(1.15f, 4.5f * s);

            PointF[] outer =
            {
                P(ox, oy, 32, 4, s), P(ox, oy, 57, 18.5f, s), P(ox, oy, 57, 45.5f, s),
                P(ox, oy, 32, 60, s), P(ox, oy, 7, 45.5f, s), P(ox, oy, 7, 18.5f, s),
            };
            PointF[] middle =
            {
                P(ox, oy, 32, 15, s), P(ox, oy, 48, 24.25f, s), P(ox, oy, 48, 39.75f, s),
                P(ox, oy, 32, 49, s), P(ox, oy, 16, 39.75f, s), P(ox, oy, 16, 24.25f, s),
            };
            PointF[] core =
            {
                P(ox, oy, 32, 24, s), P(ox, oy, 39.5f, 28.25f, s), P(ox, oy, 39.5f, 36.75f, s),
                P(ox, oy, 32, 41, s), P(ox, oy, 24.5f, 36.75f, s), P(ox, oy, 24.5f, 28.25f, s),
            };

            SmoothingMode previous = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(color, lineWidth))
            {
                pen.LineJoin = LineJoin.Round;
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawPolygon(pen, outer);
                g.DrawPolygon(pen, middle);
            }
            using (SolidBrush fill = new SolidBrush(color))
                g.FillPolygon(fill, core);
            g.SmoothingMode = previous;
        }

        static PointF P(float ox, float oy, float x, float y, float s)
        {
            return new PointF(ox + x * s, oy + y * s);
        }

        // Draws the Ukti mark into a bitmap, for tray state icons.
        public static Bitmap RenderMark(int size, Color color)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                DrawMark(g, new RectangleF(0, 0, size, size), color);
            }
            return bmp;
        }
    }
}
