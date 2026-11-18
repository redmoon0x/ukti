using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace Ukti
{
    // First-run model download.
    //
    // Shipping the 700 MB weights inside the installer would make the download
    // unwieldy and drag model licensing into the installer, so the app fetches
    // them once, into its own folder, and verifies the result before using it.
    sealed class ModelSetupForm : Form
    {
        // Pinned release of the English streaming model. The hash comes from the
        // runtime's own model index, so a corrupted or substituted download is
        // rejected rather than silently loaded.
        const string ModelUrl =
            "https://huggingface.co/nvidia/nemotron-speech-streaming-en-0.6b/resolve/" +
            "ebe59e5a817142986528bbbee5dba8db7b38ed50/nemotron-speech-streaming-en-0.6b.q8_0.gguf";
        const long ExpectedBytes = 699872960L;
        const string ExpectedSha256 = "d9a01898d2a611c8764e23a1c2f45e70bbd5a425dc4de93692ac951dd603812d";

        readonly Label _status = new Label();
        readonly ProgressBar _progress = new ProgressBar();
        readonly HexButton _download = new HexButton();
        readonly HexButton _quit = new HexButton();
        readonly string _targetPath;

        WebClient _client;
        bool _done;

        public ModelSetupForm(string targetPath)
        {
            _targetPath = targetPath;

            Text = "Ukti setup";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(496, 268);
            Font = new Font("Segoe UI", 9f);
            BackColor = Theme.BgBase;
            ForeColor = Theme.TextBase;
            ShowInTaskbar = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _progress.Location = new Point(24, 176);
            _progress.Size = new Size(448, 6);
            _progress.Style = ProgressBarStyle.Continuous;
            Controls.Add(_progress);

            _status.Location = new Point(24, 192);
            _status.AutoSize = true;
            _status.ForeColor = Theme.TextFaint;
            _status.BackColor = Color.Transparent;
            _status.Text = "Not downloaded yet";
            Controls.Add(_status);

            _download = new HexButton();
            _download.Text = "Download model";
            _download.Primary = true;
            _download.Bounds = new Rectangle(280, 220, 132, 32);
            _download.Click += delegate { StartDownload(); };
            Controls.Add(_download);

            _quit = new HexButton();
            _quit.Text = "Quit";
            _quit.Bounds = new Rectangle(420, 220, 56, 32);
            _quit.Click += delegate
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.Add(_quit);

            FormClosing += delegate
            {
                if (!_done && _client != null) _client.CancelAsync();
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.BgBase);

            Theme.DrawMark(g, new RectangleF(24, 22, 20, 20), Theme.TextBase);

            using (Font title = new Font("Segoe UI", 11f, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(Theme.TextBase))
                g.DrawString("Speech model", title, b, 52, 21);

            using (Font body = new Font("Segoe UI", 9f))
            using (SolidBrush b = new SolidBrush(Theme.TextMuted))
            {
                g.DrawString("Ukti transcribes on this PC, so your voice never leaves it.", body, b, 24, 66);
                g.DrawString("The model is a one-time download of about 700 MB and is stored", body, b, 24, 88);
                g.DrawString("in this app's folder.", body, b, 24, 110);
            }

            using (Pen p = new Pen(Theme.BorderWeak, 1f))
                g.DrawLine(p, 24, 146, 472, 146);

            // Progress track.
            using (GraphicsPath path = Theme.RoundedRect(new Rectangle(24, 176, 448, 6), 3))
            using (SolidBrush b = new SolidBrush(Theme.Alpha(Color.White, 0.08f)))
                g.FillPath(b, path);

            int filled = (int)(448 * (_progress.Value / 100f));
            if (filled > 0)
            {
                using (GraphicsPath path = Theme.RoundedRect(new Rectangle(24, 176, Math.Max(filled, 6), 6), 3))
                using (SolidBrush b = new SolidBrush(Theme.BgContrast))
                    g.FillPath(b, path);
            }
        }

        void StartDownload()
        {
            _download.Enabled = false;
            _status.Text = "Connecting...";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_targetPath));

                // .NET Framework still negotiates TLS 1.0 by default on some
                // machines, which Hugging Face rejects. The model host requires
                // at least 1.2.
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

                _client = new WebClient();
                _client.DownloadProgressChanged += OnProgress;
                _client.DownloadFileCompleted += OnComplete;

                // Download beside the final name so a partial file is never
                // picked up as a usable model.
                _client.DownloadFileAsync(new Uri(ModelUrl), _targetPath + ".partial");
            }
            catch (Exception ex)
            {
                Fail("Could not start the download: " + ex.Message);
            }
        }

        void OnProgress(object sender, DownloadProgressChangedEventArgs e)
        {
            // Report against the known size so the bar is honest even if the
            // server omits a content length.
            long total = e.TotalBytesToReceive > 0 ? e.TotalBytesToReceive : ExpectedBytes;
            int percent = (int)(100.0 * e.BytesReceived / Math.Max(total, 1));
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            _progress.Value = percent;
            _status.Text = string.Format("{0:0.0} MB of {1:0} MB  ({2}%)",
                e.BytesReceived / 1048576.0, total / 1048576.0, percent);
        }

        void OnComplete(object sender, AsyncCompletedEventArgs e)
        {
            if (e.Cancelled)
            {
                Fail("Download cancelled.");
                return;
            }
            if (e.Error != null)
            {
                Fail("Download failed: " + e.Error.Message);
                return;
            }

            _status.Text = "Verifying...";
            Application.DoEvents();

            // Verification runs off the UI thread: hashing 700 MB takes a moment
            // and would otherwise freeze the window.
            Thread verify = new Thread(delegate ()
            {
                string error = Verify();
                if (IsDisposed) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    if (error == null)
                    {
                        _done = true;
                        _progress.Value = 100;
                        _status.Text = "Ready";
                        DialogResult = DialogResult.OK;
                        Close();
                    }
                    else
                    {
                        Fail(error);
                    }
                });
            });
            verify.IsBackground = true;
            verify.Start();
        }

        string Verify()
        {
            string partial = _targetPath + ".partial";
            try
            {
                FileInfo info = new FileInfo(partial);
                if (!info.Exists) return "The downloaded file is missing.";
                if (info.Length != ExpectedBytes)
                {
                    return string.Format("The download is incomplete or wrong: {0:0.0} MB, expected {1:0.0} MB.",
                        info.Length / 1048576.0, ExpectedBytes / 1048576.0);
                }

                string actual = Sha256(partial);
                if (!string.Equals(actual, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return "The downloaded file failed its integrity check.";
                }

                if (File.Exists(_targetPath)) File.Delete(_targetPath);
                File.Move(partial, _targetPath);
                return null;
            }
            catch (Exception ex)
            {
                return "Could not verify the download: " + ex.Message;
            }
        }

        static string Sha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                System.Text.StringBuilder sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        void Fail(string message)
        {
            try { if (File.Exists(_targetPath + ".partial")) File.Delete(_targetPath + ".partial"); }
            catch { }

            // The form may already be closing (for example when the user quit
            // mid-download), in which case touching a control would throw.
            if (IsDisposed || Disposing || !IsHandleCreated) return;

            _status.Text = message;
            _status.ForeColor = Color.FromArgb(229, 77, 66);
            _download.Text = "Try again";
            _download.Enabled = true;
        }
    }
}
