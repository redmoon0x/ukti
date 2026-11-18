using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Threading;
using System.Windows.Forms;

namespace Ukti
{
    enum AppState { Loading, Idle, Recording, Working, Faulted }

    sealed class TrayApp : ApplicationContext
    {
        const string HotkeyText = "Ctrl+Space";

        readonly NotifyIcon _tray;
        readonly string _modelPath;
        readonly PillForm _pill;
        readonly HoldHotkey _hotkey;

        // Lives on the UI thread and anchors all cross-thread marshalling.
        // A low-level keyboard hook only fires on a thread that pumps messages,
        // so the hook must be installed through this control, never from the
        // background loader thread.
        readonly Control _dispatcher;

        sealed class DispatcherWindow : Control
        {
            // A recognisable title lets a second process find this window, so a
            // relaunch can ask the running instance to show its settings. The
            // class name is left alone: overriding CreateParams.ClassName makes
            // WinForms try to register a new window class, which fails.
            public DispatcherWindow()
            {
                Text = Ipc.WindowTitle;
                // Force a real window handle: InvokeRequired and BeginInvoke are
                // meaningless without one, and without a live handle on this
                // thread the keyboard hook would never receive a callback.
                IntPtr force = Handle;
                GC.KeepAlive(force);
            }

            public event MethodInvoker ShowRequested;

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == (int)Ipc.ShowMessage)
                {
                    MethodInvoker handler = ShowRequested;
                    if (handler != null)
                    {
                        try { handler(); }
                        catch { }
                    }
                    return;
                }
                base.WndProc(ref m);
            }
        }

        AppState _state = AppState.Loading;
        NemoAsr _asr;
        AudioCapture _mic;
        IntPtr _targetWindow = IntPtr.Zero;
        string _lastText = string.Empty;
        Settings _settings;
        readonly bool _startInBackground;
        System.Windows.Forms.Timer _trimTimer;
        readonly Stopwatch _since = Stopwatch.StartNew();

        // The runtime is not thread-safe; all inference goes through this gate.
        readonly object _inferGate = new object();

        public TrayApp(bool startInBackground)
        {
            _modelPath = ResolveModelPath();
            _settings = Settings.Load();
            _startInBackground = startInBackground;

            _dispatcher = new DispatcherWindow();
            ((DispatcherWindow)_dispatcher).ShowRequested += delegate
            {
                ShowSettings();
            };

            _pill = new PillForm();
            _pill.CaptureLevel = delegate { return _mic != null ? _mic.Level : 0f; };

            _tray = new NotifyIcon();
            _tray.ContextMenuStrip = BuildMenu();
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowLastText(); };

            _hotkey = new HoldHotkey();
            _hotkey.Pressed += OnHotkeyPressed;
            _hotkey.Released += OnHotkeyReleased;
            _hotkey.Cancelled += OnHotkeyCancelled;

            SetState(AppState.Loading, "Loading speech model...");

            Thread loader = new Thread(LoadModel);
            loader.IsBackground = true;
            loader.SetApartmentState(ApartmentState.STA);
            loader.Start();
        }

        static string ResolveModelPath()
        {
            string explicitPath = Environment.GetEnvironmentVariable("VT_MODEL");
            if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath)) return explicitPath;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                // English-only model first: it is smaller and slightly faster
                // than the multilingual one, at the same accuracy for English.
                Path.Combine(baseDir, "models", "nemotron-en.q8_0.gguf"),
                Path.Combine(baseDir, "nemotron-en.q8_0.gguf"),
                Path.Combine(baseDir, "models", "nemotron-3.5-asr-streaming-0.6b.q8_0.gguf"),
                Path.Combine(baseDir, "nemotron-3.5-asr-streaming-0.6b.q8_0.gguf"),
            };
            foreach (string c in candidates)
                if (File.Exists(c)) return c;
            return candidates[0];
        }

        ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem status = new ToolStripMenuItem("Starting...");
            status.Enabled = false;
            status.Name = "status";
            menu.Items.Add(status);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem hint = new ToolStripMenuItem("Hold " + HotkeyText + " to dictate");
            hint.Enabled = false;
            menu.Items.Add(hint);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem enable = new ToolStripMenuItem("Enabled");
            enable.Name = "enable";
            enable.CheckOnClick = true;
            enable.Checked = _settings != null && _settings.Enabled;
            enable.Click += delegate { SetEnabled(enable.Checked); };
            menu.Items.Add(enable);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem settingsItem = new ToolStripMenuItem("Settings...");
            settingsItem.Name = "settings";
            settingsItem.Click += delegate { ShowSettings(); };
            menu.Items.Add(settingsItem);

            ToolStripMenuItem downloadItem = new ToolStripMenuItem("Download speech model...");
            downloadItem.Name = "download";
            downloadItem.Click += delegate { DownloadModel(); };
            menu.Items.Add(downloadItem);

            ToolStripMenuItem copy = new ToolStripMenuItem("Copy last transcript");
            copy.Click += delegate { ShowLastText(); };
            menu.Items.Add(copy);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem quit = new ToolStripMenuItem("Quit");
            quit.Click += delegate { Quit(); };
            menu.Items.Add(quit);

            return menu;
        }

        void SetStatusItem(string text)
        {
            if (_tray.ContextMenuStrip == null) return;
            ToolStripItem item = _tray.ContextMenuStrip.Items["status"];
            if (item != null) item.Text = text;
        }

        void SetState(AppState state, string tooltip)
        {
            _state = state;
            if (_tray == null) return;

            _tray.Text = Truncate(tooltip);
            _tray.Icon = BuildTrayIcon(state);
            SetStatusItem(tooltip);
        }

        static Icon _idleIcon, _recordIcon, _workIcon, _errorIcon;

        static Icon BuildTrayIcon(AppState state)
        {
            Icon cached = state == AppState.Recording ? _recordIcon
                        : state == AppState.Working ? _workIcon
                        : state == AppState.Faulted ? _errorIcon
                        : _idleIcon;
            if (cached != null) return cached;

            // The mark takes the state colour, so one glyph reads as four
            // states without four separate assets.
            Color color = state == AppState.Recording ? Theme.Danger
                        : state == AppState.Working ? Color.FromArgb(120, 170, 245)
                        : state == AppState.Faulted ? Theme.WarmMic
                        : Theme.TextBase;

            Bitmap bmp = Theme.RenderMark(32, color);
            Icon icon = Icon.FromHandle(bmp.GetHicon());
            bmp.Dispose();

            if (state == AppState.Recording) _recordIcon = icon;
            else if (state == AppState.Working) _workIcon = icon;
            else if (state == AppState.Faulted) _errorIcon = icon;
            else _idleIcon = icon;
            return icon;
        }

        void LoadModel()
        {
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                if (!File.Exists(_modelPath))
                {
                    // A missing model is the normal first-run state, not an
                    // error: fetch it here so the user never has to hunt for a
                    // file or a download link.
                    BeginOnUi(delegate
                    {
                        SetState(AppState.Loading, "Waiting for the speech model...");
                        using (ModelSetupForm setup = new ModelSetupForm(_modelPath))
                        {
                            if (setup.ShowDialog() == DialogResult.OK)
                            {
                                // Model is in place; retry the load.
                                Thread again = new Thread(LoadModel);
                                again.IsBackground = true;
                                again.SetApartmentState(ApartmentState.STA);
                                again.Start();
                            }
                            else
                            {
                                SetState(AppState.Faulted, "No speech model - open Settings to try again");
                            }
                        }
                    });
                    return;
                }

                NemoAsr asr = NemoAsr.Create(_modelPath, 0, 0.16f, 1);

                // First inference compiles GPU shaders (several seconds). Do it now
                // so the user's first dictation is not the one that pays for it.
                float[] warm = new float[AudioCapture.SampleRate / 2];
                asr.Transcribe(warm, AudioCapture.SampleRate);

                _asr = asr;
                sw.Stop();
                Log("model loaded in " + sw.ElapsedMilliseconds + " ms from " + _modelPath);

                // Keep the microphone warm: opening it costs ~0.5 s, which would
                // otherwise clip the start of every dictation.
                AudioCapture mic = new AudioCapture();
                mic.DeviceIndex = _settings.MicrophoneIndex;
                mic.Open();
                _mic = mic;
                Log("microphone open: " + MicName());

                BeginOnUi(delegate
                {
                    if (!_hotkey.Install())
                    {
                        Log("keyboard hook FAILED");
                        SetState(AppState.Faulted, "Could not install the " + HotkeyText + " shortcut");
                        _tray.ShowBalloonTip(8000, "Ukti",
                            "Could not install the keyboard hook needed for hold-to-talk.",
                            ToolTipIcon.Error);
                        return;
                    }
                    Log("keyboard hook installed");

                    // Keep Windows registration in sync with the saved setting;
                    // the executable may have moved since it was set.
                    if (_settings.RunAtStartup) Startup.Apply(true);

                    SetState(AppState.Idle, _settings.Enabled
                        ? "Ready - hold " + HotkeyText + " to dictate (" + sw.ElapsedMilliseconds + " ms load)"
                        : "Turned off");

                    // On first launch, or whenever the app is opened from the
                    // Start menu rather than Windows sign-in, take the user to the
                    // choices that matter instead of leaving them to find a tray
                    // menu. A sign-in launch stays out of the way.
                    if (!_settings.FirstRunDone)
                    {
                        _settings.FirstRunDone = true;
                        _settings.Save();
                        ShowSettings();
                    }
                    else if (!_startInBackground)
                    {
                        ShowSettings();
                    }

                    // Loading and warming the model touches far more memory than
                    // sits in the tray afterwards, so give those pages back once
                    // the first-run window is out of the way.
                    StartIdleTrim();
                });
            }
            catch (Exception ex)
            {
                Fail("Could not start the speech engine.\n\n" + ex.Message);
            }
        }

        void Fail(string message)
        {
            Log("FAILED: " + message.Replace('\n', ' '));
            _asr = null;
            BeginOnUi(delegate
            {
                SetState(AppState.Faulted, "Speech engine unavailable - click for details");
                _tray.ShowBalloonTip(8000, "Ukti", message, ToolTipIcon.Error);
            });
        }

        // Turning the app off must actually stop it listening, not just grey out
        // an icon, so the master switch gates the shortcut itself.
        void SetEnabled(bool enabled)
        {
            _settings.Enabled = enabled;
            _settings.Save();

            ToolStripItem item = _tray.ContextMenuStrip.Items["enable"];
            if (item is ToolStripMenuItem) ((ToolStripMenuItem)item).Checked = enabled;

            if (!enabled && _state == AppState.Recording)
            {
                if (_mic != null) _mic.EndCapture();
                _pill.HidePill();
            }

            SetState(AppState.Idle,
                     enabled ? "Ready - hold " + HotkeyText + " to dictate" : "Turned off");
        }

        string MicName()
        {
            return _settings.MicrophoneIndex < 0
                ? AudioCapture.DefaultDeviceName() + " (default)"
                : AudioCapture.DeviceName(_settings.MicrophoneIndex);
        }

        // Changing the device reopens the capture stream; the app keeps the mic
        // open for latency, so it has to be swapped rather than reconfigured.
        void ShowSettings()
        {
            using (SettingsForm form = new SettingsForm(_settings))
            {
                if (form.ShowDialog() != DialogResult.OK) return;

                int previousMic = _settings.MicrophoneIndex;

                _settings.MicrophoneIndex = form.SelectedIndex;
                _settings.Enabled = form.EnabledNow;
                _settings.RunAtStartup = form.StartWithWindows;
                _settings.RunInBackground = form.KeepRunning;
                _settings.FirstRunDone = true;
                _settings.Save();

                if (!Startup.Apply(_settings.RunAtStartup))
                {
                    _tray.ShowBalloonTip(7000, "Ukti",
                        "Could not change the Windows startup entry.", ToolTipIcon.Warning);
                }

                SetEnabled(_settings.Enabled);

                if (_settings.MicrophoneIndex != previousMic)
                {
                    try
                    {
                        if (_mic != null) { _mic.Close(); _mic.Dispose(); _mic = null; }
                        AudioCapture mic = new AudioCapture();
                        mic.DeviceIndex = _settings.MicrophoneIndex;
                        mic.Open();
                        _mic = mic;
                        Log("microphone switched to " + MicName());
                    }
                    catch (Exception ex)
                    {
                        Log("microphone switch failed: " + ex.Message);
                        _tray.ShowBalloonTip(7000, "Ukti",
                            "Could not open that microphone: " + ex.Message, ToolTipIcon.Warning);
                    }
                }
            }
        }

        // Gives memory back after a quiet period, and again after each dictation
        // settles. The delay matters: trimming right after a session would page
        // the model back out just as the user is likely to speak again.
        void StartIdleTrim()
        {
            if (_trimTimer != null) return;

            _trimTimer = new System.Windows.Forms.Timer();
            _trimTimer.Interval = 20000;
            _trimTimer.Tick += delegate
            {
                if (_state != AppState.Idle) return;
                long before = Memory.WorkingSetMb();
                Memory.Trim();
                long after = Memory.WorkingSetMb();
                if (before > 0 && after > 0 && before - after > 24)
                    Log("trimmed working set " + before + " MB -> " + after + " MB");
            };
            _trimTimer.Start();
        }

        void DownloadModel()
        {
            if (File.Exists(_modelPath))
            {
                _tray.ShowBalloonTip(4000, "Ukti",
                    "The speech model is already installed.", ToolTipIcon.Info);
                return;
            }

            using (ModelSetupForm setup = new ModelSetupForm(_modelPath))
            {
                if (setup.ShowDialog() != DialogResult.OK) return;
            }

            Thread again = new Thread(LoadModel);
            again.IsBackground = true;
            again.SetApartmentState(ApartmentState.STA);
            again.Start();
        }

        void BeginOnUi(MethodInvoker action)
        {
            if (_dispatcher.InvokeRequired)
                _dispatcher.BeginInvoke(action);
            else
                action();
        }

        static readonly string LogPath = Path.Combine(Path.GetTempPath(), "Ukti.log");

        internal static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch { }
        }

        void OnHotkeyPressed()
        {
            if (_asr == null || !_settings.Enabled || _state != AppState.Idle) return;
            BeginOnUi(delegate { StartRecording(); });
        }

        void OnHotkeyReleased()
        {
            if (_state != AppState.Recording) return;
            BeginOnUi(delegate { StopAndTranscribe(); });
        }

        void OnHotkeyCancelled()
        {
            if (_state != AppState.Recording) return;
            BeginOnUi(delegate
            {
                if (_mic != null) _mic.EndCapture();
                _pill.HidePill();
                SetState(AppState.Idle, "Ready - hold " + HotkeyText + " to dictate");
                PlayCue(true);
            });
        }

        void StartRecording()
        {
            try
            {
                _targetWindow = TextInjector.CurrentForegroundWindow();
                _since.Restart();

                _mic.BeginCapture();

                _pill.ShowListening(_targetWindow);
                SetState(AppState.Recording, "Listening... release " + HotkeyText + " to insert");
            }
            catch (Exception ex)
            {
                Log("start FAILED: " + ex.Message);
                _pill.HidePill();
                SetState(AppState.Idle, "Ready - hold " + HotkeyText + " to dictate");
                _tray.ShowBalloonTip(7000, "Ukti", ex.Message, ToolTipIcon.Warning);
            }
        }

        void StopAndTranscribe()
        {
            _since.Stop();
            long heldMs = _since.ElapsedMilliseconds;

            float[] samples;
            try
            {
                samples = _mic.EndCapture();
            }
            catch (Exception ex)
            {
                _pill.HidePill();
                SetState(AppState.Idle, "Ready - hold " + HotkeyText + " to dictate");
                _tray.ShowBalloonTip(7000, "Ukti", "Recording stopped: " + ex.Message, ToolTipIcon.Warning);
                return;
            }

            double peak = 0, sumSquares = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                double a = Math.Abs(samples[i]);
                if (a > peak) peak = a;
                sumSquares += samples[i] * samples[i];
            }
            double rms = samples.Length > 0 ? Math.Sqrt(sumSquares / samples.Length) : 0;

            Log("captured " + samples.Length + " samples (" + (samples.Length / 16.0).ToString("0") +
                " ms, held " + heldMs + " ms) peak=" + peak.ToString("0.0000") +
                " rms=" + rms.ToString("0.00000"));

            if (samples.Length < AudioCapture.SampleRate / 4)
            {
                Log("too short, ignored");
                _pill.HidePill();
                SetState(AppState.Idle, "Ready - hold " + HotkeyText + " to dictate");
                return;
            }

            if (peak < 0.002)
            {
                Log("input was silent, skipping inference");
                _pill.HidePill();
                SetState(AppState.Idle, "Ready - hold " + HotkeyText + " to dictate");
                _tray.ShowBalloonTip(6000, "Ukti",
                    "No sound reached the microphone. Check the input device and its level.",
                    ToolTipIcon.Info);
                return;
            }

            _pill.ShowWorking(_targetWindow, "Processing");
            SetState(AppState.Working, "Transcribing...");

            Thread worker = new Thread(delegate ()
            {
                string text = null;
                string error = null;
                long inferMs = 0;
                Stopwatch sw = Stopwatch.StartNew();
                try
                {
                    lock (_inferGate)
                    {
                        // Prepare builds a conditioned copy, so the raw capture is
                        // dropped as soon as it is no longer needed: on a long
                        // dictation this is tens of megabytes of float audio that
                        // would otherwise sit alive until the next session.
                        float[] prepared = AudioPipeline.Prepare(samples, AudioCapture.SampleRate);
                        samples = null;

                        text = _asr.Transcribe(prepared, AudioCapture.SampleRate);
                        prepared = null;
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                finally
                {
                    samples = null;
                }
                sw.Stop();
                inferMs = sw.ElapsedMilliseconds;
                Log("infer " + inferMs + " ms error=" + error + " text=[" + (text ?? "") + "]");

                BeginOnUi(delegate
                {
                    _pill.HidePill();
                    SetState(AppState.Idle, "Ready - hold " + HotkeyText + " to dictate");

                    if (error != null)
                    {
                        _tray.ShowBalloonTip(8000, "Ukti", "Transcription failed: " + error, ToolTipIcon.Error);
                        return;
                    }

                    text = (text ?? string.Empty).Trim();
                    if (text.Length == 0) return;

                    _lastText = text;

                    string pasteError = TextInjector.Paste(text, _targetWindow);
                    Log("paste=" + (pasteError ?? "ok") + " total=" + (heldMs + inferMs) + " ms");

                    if (pasteError != null)
                    {
                        _tray.ShowBalloonTip(8000, "Ukti",
                            pasteError + "\n\nThe text was copied, so you can paste it manually with Ctrl+V.",
                            ToolTipIcon.Info);
                    }
                });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        static string Truncate(string s)
        {
            if (s == null) return "";
            return s.Length > 63 ? s.Substring(0, 60) + "..." : s;
        }

        static void PlayCue(bool start)
        {
            try
            {
                if (start) SystemSounds.Asterisk.Play();
                else SystemSounds.Beep.Play();
            }
            catch { }
        }

        void ShowLastText()
        {
            if (string.IsNullOrEmpty(_lastText))
            {
                _tray.ShowBalloonTip(4000, "Ukti", "Nothing has been dictated yet.", ToolTipIcon.Info);
                return;
            }

            if (TextInjector.CopyToClipboard(_lastText))
                _tray.ShowBalloonTip(4000, "Ukti", "Last transcript copied to the clipboard.", ToolTipIcon.Info);
        }

        void Quit()
        {
            if (_mic != null) { _mic.Close(); _mic.Dispose(); _mic = null; }
            if (_asr != null) { _asr.Dispose(); _asr = null; }
            _hotkey.Dispose();
            _pill.Dispose();

            _tray.Visible = false;
            _tray.Dispose();

            ExitThread();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Launching the executable again while it is already running is how a
            // user reopens the window from the Start menu, so the second process
            // asks the running one to show its settings instead of erroring.
            bool created;
            using (Mutex mutex = new Mutex(true, "Ukti.SingleInstance", out created))
            {
                if (!created)
                {
                    if (Ipc.RequestShowWindow())
                    {
                        return;
                    }

                    MessageBox.Show("Ukti is already running. Look for the microphone icon in the tray.",
                        "Ukti", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp(args.Length > 0 && args[0] == "--background"));
            }
        }
    }
}
