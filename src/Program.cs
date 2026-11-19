using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Ukti
{


    sealed class TrayApp : ApplicationContext
    {
        sealed class DispatcherWindow : Control
        {
            public event MethodInvoker ShowRequested;
            public event MethodInvoker ThemeChange;
            public Func<IntPtr, bool> RawInput;

            public DispatcherWindow() { Text = Ipc.WindowTitle; IntPtr handle = Handle; GC.KeepAlive(handle); }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == (int)Ipc.ShowMessage)
                { if (ShowRequested != null) ShowRequested(); return; }
                if (message.Msg == 0x00FF /* WM_INPUT */ && RawInput != null && RawInput(message.LParam)) return;
                // System theme/DWM changes: repaint the custom-drawn dark UI.
                if (message.Msg == 0x001A /* WM_SETTINGCHANGE */ || message.Msg == 0x0320 /* WM_DWMCOLORIZATIONCOLORCHANGED */)
                {
                    if (ThemeChange != null) ThemeChange();
                    return;
                }
                base.WndProc(ref message);
            }
        }
        readonly DispatcherWindow _dispatcher;
        readonly NotifyIcon _tray;
        readonly PillForm _pill;
        readonly HoldHotkey _hotkey;
        readonly Settings _settings;
        readonly WorkCoordinator _work;
        readonly object _inferGate = new object();
        readonly bool _startInBackground;
        readonly Stopwatch _idleClock = Stopwatch.StartNew();
        readonly System.Windows.Forms.Timer _idleTimer;
        string _loadedModelPath;
        SpeechModel _pendingModel;
        string _pendingModelPath;
        System.Threading.CancellationTokenSource _wakeToken;
        System.Threading.CancellationTokenSource _transcribeToken;
        bool _wakeHeld, _wakeCancelled;
        float[] _wakeSamples;
        IAsrEngine _asr;
        AudioCapture _mic;
        SettingsForm _settingsForm;
        AppState _state = AppState.Loading;
        IntPtr _targetWindow;
        TargetIdentity _targetIdentity;
        string _lastText = "";
        volatile bool _exiting;
        int _captureVersion;
        System.Windows.Forms.Timer _pasteTimer;
        string HotkeyText { get { return _settings.GetShortcut().ToString(); } }

        public TrayApp(bool startInBackground) : this(startInBackground, true) { }
        internal TrayApp(bool startInBackground, bool startEngine)
        {
            _settings = Settings.Load();
            _startInBackground = startInBackground;
            _work = new WorkCoordinator(delegate (Action action) { Post(new MethodInvoker(action)); });
            _dispatcher = new DispatcherWindow();

            _pill = new PillForm();
            _pill.CaptureLevel = delegate { AudioCapture mic = _mic; return mic != null ? mic.Level : 0f; };
            _dispatcher.ThemeChange += delegate
            {
                _pill.Invalidate(true);
                if (_settingsForm != null) _settingsForm.Invalidate(true);
            };
            _hotkey = new HoldHotkey();
            _hotkey.Configure(_settings.GetShortcut());
            _dispatcher.RawInput = _hotkey.HandleRawInput;
            _hotkey.Pressed += StartRecording;
            _hotkey.Released += StopAndTranscribe;
            _hotkey.Cancelled += CancelRecording;
            _tray = new NotifyIcon();
            _tray.ContextMenuStrip = BuildMenu();
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowSettings(false); };

            _idleTimer = new System.Windows.Forms.Timer();
            _idleTimer.Interval = 1000;
            _idleTimer.Tick += delegate { TryUnloadIdleModel(_idleClock.Elapsed); };
            SetState(AppState.Loading, "Loading speech model\u2026");
            if (startEngine)
            {
                _idleTimer.Start(); // Also ticks inside the modal Settings loop.
                _dispatcher.BeginInvoke((MethodInvoker)BeginStartup);
            }
        }
        void BeginStartup()
        {
            if (_exiting) return;
            if (_settings.RunAtStartup) Startup.Apply(true);
            SpeechModel model = ModelCatalog.Find(_settings.ModelId);
            string path = ModelCatalog.PathFor(model, ModelCatalog.DirectoryPath);
            string custom = Environment.GetEnvironmentVariable("VT_MODEL");
            if (!string.IsNullOrEmpty(custom) && File.Exists(custom)) path = custom;
            else if (!ModelCatalog.IsInstalled(model, ModelCatalog.DirectoryPath))
            {
                using (ModelSetupForm setup = new ModelSetupForm(model))
                {
                    if (setup.ShowDialog() != DialogResult.OK)
                    { SetState(AppState.Faulted, "Choose a speech model in Settings"); return; }
                    model = setup.SelectedModel;
                    path = ModelCatalog.PathFor(model, ModelCatalog.DirectoryPath);
                }
            }
            StartModelLoad(model, path, !_startInBackground || !_settings.FirstRunDone);
        }
        ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            HexMenuRenderer.Style(menu);
            ToolStripMenuItem status = new ToolStripMenuItem("Starting\u2026");
            status.Name = "status"; status.Enabled = false; menu.Items.Add(status);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem hint = new ToolStripMenuItem("Hold " + HotkeyText + " to dictate");
            hint.Name = "shortcutHint"; hint.Enabled = false; menu.Items.Add(hint);
            ToolStripMenuItem enable = new ToolStripMenuItem("Enable dictation");
            enable.Name = "enable"; enable.CheckOnClick = true; enable.Checked = _settings.Enabled;
            enable.Click += delegate { SetEnabled(enable.Checked); }; menu.Items.Add(enable);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem preferences = new ToolStripMenuItem("Settings\u2026");
            preferences.Click += delegate { ShowSettings(false); }; menu.Items.Add(preferences);
            ToolStripMenuItem models = new ToolStripMenuItem("Choose speech model\u2026");
            models.Click += delegate { ShowSettings(true); }; menu.Items.Add(models);
            ToolStripMenuItem copy = new ToolStripMenuItem("Copy last transcript");
            copy.Click += delegate { CopyLastText(); }; menu.Items.Add(copy);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem quit = new ToolStripMenuItem("Quit Ukti");
            quit.Click += delegate { Quit(); }; menu.Items.Add(quit);
            return menu;
        }
        void RefreshHotkey()
        {
            bool ready = _asr != null && _mic != null && (_state == AppState.Idle || _state == AppState.Recording);
            bool sleeping = _state == AppState.Sleeping && !string.IsNullOrEmpty(_loadedModelPath);
            bool waking = _state == AppState.Waking && _wakeHeld && !_wakeCancelled;
            _hotkey.Enabled = !_exiting && _settings.Enabled && _settingsForm == null && (ready || sleeping || waking);
        }
        void SetState(AppState state, string tooltip)
        {
            if (_exiting) return;
            _state = state;
            _tray.Text = tooltip.Length > 63 ? tooltip.Substring(0, 60) + "\u2026" : tooltip;
            _tray.ContextMenuStrip.Items["status"].Text = tooltip;
            _tray.Icon = BuildTrayIcon(state, _settings.Enabled);
            RefreshHotkey();
        }
        static Icon _idleIcon, _pausedIcon, _recordIcon, _workIcon, _errorIcon;
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
        static Icon BuildTrayIcon(AppState state, bool enabled)
        {
            bool paused = !enabled && (state == AppState.Idle || state == AppState.Sleeping);
            Icon cached = paused ? _pausedIcon : state == AppState.Recording ? _recordIcon :
                state == AppState.Working ? _workIcon : state == AppState.Faulted ? _errorIcon : _idleIcon;
            if (cached != null) return cached;
            Color color = paused ? Theme.FaintInk : state == AppState.Recording ? Theme.Danger :
                state == AppState.Working ? Theme.AgentBlue : state == AppState.Faulted ? Theme.WarmMic : Theme.Ink;
            Icon result;
            using (Bitmap bitmap = Theme.RenderMark(32, color))
            {
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon icon = Icon.FromHandle(handle)) result = (Icon)icon.Clone(); }
                finally { DestroyIcon(handle); }
            }
            if (paused) _pausedIcon = result;
            else if (state == AppState.Recording) _recordIcon = result;
            else if (state == AppState.Working) _workIcon = result;
            else if (state == AppState.Faulted) _errorIcon = result;
            else _idleIcon = result;
            return result;
        }
        void CloseMicrophone()
        {
            if (_mic == null) return;
            _mic.Dispose();
            _mic = null;
        }
        void EnsureMicrophone()
        {
            if (!_settings.Enabled || _mic != null || _asr == null) return;
            AudioCapture mic = new AudioCapture();
            try { mic.DeviceIndex = _settings.MicrophoneIndex; mic.Open(); _mic = mic; }
            catch { mic.Dispose(); throw; }
        }
        void SetReady()
        {
            if (_exiting) return;
            if (_state == AppState.Unloading) { RefreshHotkey(); return; }
            _idleClock.Restart();
            if (_asr == null)
            {
                if (_state == AppState.Sleeping) SetSleepingReady();
                else SetState(AppState.Faulted, "Speech engine unavailable - open Settings");
                return;
            }
            if (!_settings.Enabled)
            {
                _hotkey.Dispose(); CloseMicrophone();
                SetState(AppState.Idle, "Paused - dictation is off");
                return;
            }
            try
            {
                // Mic stays open while idle; opening it can block for a while,
                // which is safe now that Raw Input has no hook-thread timeout.
                EnsureMicrophone();
                if (!_hotkey.Install(_dispatcher.Handle)) throw new InvalidOperationException("Could not install the dictation shortcut.");
                SetState(AppState.Idle, _settings.Enabled ? "Ready - hold " + HotkeyText + " to dictate" : "Paused - dictation is off");
            }
            catch (Exception ex)
            {
                CloseMicrophone();
                SetState(AppState.Faulted, "Input unavailable - open Settings");
                Notify("Could not enable dictation. " + ex.Message, ToolTipIcon.Warning);
            }
        }
        void SetSleepingReady()
        {
            if (_exiting) return;
            CloseMicrophone();
            if (!_settings.Enabled) _hotkey.Dispose();
            else if (!_hotkey.Install(_dispatcher.Handle))
            { SetState(AppState.Faulted, "Could not install the wake shortcut"); return; }
            SetState(AppState.Sleeping, _settings.Enabled ? "Model unloaded - hold " + HotkeyText + " to wake" : "Paused - model unloaded");
        }
        internal bool TryUnloadIdleModel(TimeSpan idle)
        {
            if (_exiting || (_state != AppState.Idle && _state != AppState.Faulted) || _asr == null || _pasteTimer != null ||
                string.IsNullOrEmpty(_loadedModelPath) || !IdleUnloadPolicy.IsDue(_settings.IdleUnloadMinutes, idle)) return false;
            IAsrEngine resident = _asr;
            _asr = null;
            SetState(AppState.Unloading, "Releasing model memory\u2026");
            CloseMicrophone();
            _work.Submit(delegate (CancellationToken token)
            {
                string error = null;
                try { lock (_inferGate) resident.Dispose(); Memory.Trim(); }
                catch (Exception ex) { error = ex.Message; }
                return error;
            }, delegate (object result, bool cancelled)
            {
                string error = result as string;
                if (error == null) { SetSleepingReady(); Log("model unloaded after inactivity"); }
                else { SetState(AppState.Faulted, "Could not release model memory"); Notify(error, ToolTipIcon.Warning); }
                SpeechModel pending = _pendingModel;
                string path = _pendingModelPath;
                _pendingModel = null; _pendingModelPath = null;
                if (pending != null) StartModelLoad(pending, path, false);
            }, delegate { resident.Dispose(); });
            return true;
        }
        void WakeForRecording()
        {
            _targetIdentity = new TargetIdentity(TextInjector.CurrentForegroundWindow());
            _targetWindow = _targetIdentity.Window;
            _wakeHeld = true; _wakeCancelled = false; _wakeSamples = null;
            _captureVersion++;
            SpeechModel model = ModelCatalog.Find(_settings.ModelId);
            string path = _loadedModelPath;
            int microphoneIndex = _settings.MicrophoneIndex;
            SetState(AppState.Waking, "Waking model - wait for Listening before speaking");
            _pill.ShowWorking(_targetWindow, "Waking");
            _wakeToken = _work.Submit(delegate (CancellationToken token)
            {
                AudioCapture microphone = new AudioCapture();
                try { microphone.DeviceIndex = microphoneIndex; microphone.Open(); }
                catch (Exception ex)
                {
                    microphone.Dispose();
                    return new object[] { null, null, ex.Message };
                }
                if (token.IsCancellationRequested) { microphone.Dispose(); return new object[] { null, null, null }; }
                IAsrEngine loaded = null;
                string error = null;
                try { lock (_inferGate) loaded = CreateRecognizer(model, path, token); }
                catch (Exception ex) { error = ex.Message; }
                return new object[] { microphone, loaded, error };
            }, delegate (object state, bool cancelled)
            {
                _wakeToken = null;
                if (cancelled || _exiting)
                {
                    DisposeWakeResult(state);
                    _wakeHeld = false; _wakeCancelled = true; _wakeSamples = null;
                    if (!_exiting) { _pill.HidePill(); SetSleepingReady(); }
                    return;
                }
                object[] result = state as object[];
                if (result == null) { FinishWake(null, WorkError(state)); return; }
                AudioCapture microphone = (AudioCapture)result[0];
                IAsrEngine recognizer = (IAsrEngine)result[1];
                string error = cancelled ? null : (string)result[2];
                if (microphone != null)
                {
                    if (cancelled || _state != AppState.Waking || !_settings.Enabled) microphone.Dispose();
                    else
                    {
                        _mic = microphone;
                        if (_wakeHeld && !_wakeCancelled)
                        {
                            try { _mic.BeginCapture(); _pill.ShowListening(_targetWindow); }
                            catch (Exception ex)
                            {
                                _wakeHeld = false; _wakeCancelled = true;
                                CloseMicrophone(); _pill.HidePill(); RefreshHotkey(); Notify(ex.Message, ToolTipIcon.Warning);
                            }
                        }
                    }
                }
                else if (error != null)
                {
                    _wakeHeld = false; _wakeCancelled = true;
                    _pill.HidePill(); SetSleepingReady(); Notify("Could not open the microphone. " + error, ToolTipIcon.Warning);
                }
                if (recognizer != null)
                {
                    if (cancelled) recognizer.Dispose();
                    else FinishWake(recognizer, error);
                }
                else if (error != null) FinishWake(null, error);
            }, DisposeWakeResult);
        }
        void FinishWake(IAsrEngine recognizer, string error)
        {
            if (_state != AppState.Waking) { if (recognizer != null) recognizer.Dispose(); return; }
            if (error != null)
            {
                try { if (_mic != null) _mic.EndCapture(); } catch { }
                _wakeHeld = false; _wakeCancelled = true; _wakeSamples = null;
                CloseMicrophone(); _pill.HidePill();
                SetState(AppState.Faulted, "Could not wake model - open Settings");
                Notify("Could not reload the model. " + error, ToolTipIcon.Error);
                return;
            }
            _asr = recognizer;
            _idleClock.Restart();
            // Capture starts as soon as the mic is ready, while weights warm on
            // the worker. Audio spoken after Listening is never lost to reload.
            if (_wakeHeld && !_wakeCancelled && _settings.Enabled && _mic != null)
            { SetState(AppState.Recording, "Listening - release " + HotkeyText + " to insert"); return; }
            float[] samples = _wakeSamples;
            _wakeSamples = null;
            if (!_wakeCancelled && _settings.Enabled && samples != null) TranscribeSamples(samples);
            else { _pill.HidePill(); SetReady(); }
        }
        static string WorkError(object state)
        { return state is Exception ? ((Exception)state).Message : "The speech operation did not return a result."; }
        static void DisposeWakeResult(object state)
        {
            object[] result = state as object[];
            if (result == null) return;
            if (result[0] is IDisposable) ((IDisposable)result[0]).Dispose();
            if (result[1] is IDisposable) ((IDisposable)result[1]).Dispose();
        }
        static void DisposeLoadResult(object state)
        {
            object[] result = state as object[];
            if (result != null && result[0] is IDisposable) ((IDisposable)result[0]).Dispose();
        }
        static IAsrEngine CreateRecognizer(SpeechModel model, string path, CancellationToken cancel)
        {
            IAsrEngine engine = AsrBackend.Create(model, path, cancel);
            try
            {
                using (cancel.Register(engine.Dispose))
                {
                    cancel.ThrowIfCancellationRequested();
                    engine.Transcribe(new float[AudioCapture.SampleRate / 2], AudioCapture.SampleRate);
                    cancel.ThrowIfCancellationRequested();
                }
                return engine;
            }
            catch { engine.Dispose(); throw; }
        }
        void StartModelLoad(SpeechModel model, string path, bool showPreferences)
        {
            if (_exiting || _state == AppState.Recording || _state == AppState.Working || _state == AppState.Waking) return;
            if (_state == AppState.Unloading) { _pendingModel = model; _pendingModelPath = path; return; }
            SetState(AppState.Loading, "Loading " + model.Name + "\u2026");
            // Never overlap two multi-GB native workers during a model switch.
            // Keep the old model's path so a failed replacement can wake it.
            IAsrEngine previous = _asr;
            _asr = null;
            _hotkey.Dispose();
            CloseMicrophone();
            _work.Submit(delegate (CancellationToken token)
            {
                IAsrEngine replacement = null;
                string error = null;
                Stopwatch clock = Stopwatch.StartNew();
                try
                {
                    lock (_inferGate)
                    {
                        if (previous != null) previous.Dispose();
                        replacement = CreateRecognizer(model, path, token);
                    }
                }
                catch (Exception ex) { error = ex.Message; if (replacement != null) replacement.Dispose(); replacement = null; }
                return new object[] { replacement, error, clock.ElapsedMilliseconds };
            }, delegate (object state, bool cancelled)
            {
                if (_exiting || cancelled)
                {
                    DisposeLoadResult(state);
                    if (!_exiting)
                    {
                        if (!string.IsNullOrEmpty(_loadedModelPath)) SetSleepingReady();
                        else SetReady();
                    }
                    return;
                }
                object[] result = state as object[];
                if (result == null) { SetReady(); Notify(WorkError(state), ToolTipIcon.Error); return; }
                IAsrEngine loaded = (IAsrEngine)result[0];
                string error = (string)result[1];
                if (result[0] != null && !cancelled)
                {
                    lock (_inferGate) { if (_asr != null) _asr.Dispose(); _asr = loaded; }
                    _loadedModelPath = path;
                    _settings.ModelId = model.Id;
                    _settings.FirstRunDone = true;
                    SaveSettings();
                    Log("loaded " + model.Id + " in " + result[2] + " ms");
                }
                else if (loaded != null) loaded.Dispose();
                if (_asr == null && !string.IsNullOrEmpty(_loadedModelPath)) SetSleepingReady();
                else SetReady();
                if (error != null)
                    Notify("Could not load " + model.Name + ". " + error +
                        (!string.IsNullOrEmpty(_loadedModelPath) ? " Your previous model remains selected; hold the shortcut to reload it." : " Choose a model in Settings."), ToolTipIcon.Error);
                if (showPreferences) ShowSettings(false);
            }, delegate(object state)
            {
                if (previous != null) previous.Dispose();
                DisposeLoadResult(state);
            });
        }
        void SetEnabled(bool enabled)
        {
            _settings.Enabled = enabled;
            SaveSettings();
            ((ToolStripMenuItem)_tray.ContextMenuStrip.Items["enable"]).Checked = enabled;
            if (!enabled)
            {
                CancelWork();
                _captureVersion++;
                // A released wake hold can still have buffered audio waiting for
                // weights. Invalidate it explicitly, not only via an armed hook.
                if (_state == AppState.Recording || _state == AppState.Waking) CancelRecording();
                _hotkey.Enabled = false;
                CloseMicrophone();
            }
            if (_state == AppState.Loading || _state == AppState.Working || _state == AppState.Unloading || _state == AppState.Waking)
            { RefreshHotkey(); return; }
            SetReady();
        }
        void ShowSettings(bool openModels)
        {
            if (_exiting) return;
            if (_settingsForm != null) { _settingsForm.Activate(); return; }
            if (_state == AppState.Loading || _state == AppState.Working || _state == AppState.Unloading || _state == AppState.Waking)
            { Notify("Finishing the current operation. Please open Settings in a moment.", ToolTipIcon.Info); return; }
            if (_state == AppState.Recording) CancelRecording();
            string proposedModel = _settings.ModelId;
            bool accepted;
            bool closeRequested;
            using (SettingsForm form = new SettingsForm(_settings))
            {
                _settingsForm = form;
                Shortcut shortcut = _settings.GetShortcut();
                form.SuppressKey = delegate (Keys keyData)
                {
                    Keys key = keyData & Keys.KeyCode;
                    Keys mods = keyData & (Keys.Control | Keys.Alt | Keys.Shift);
                    return key == shortcut.Key && mods == shortcut.Modifiers;
                };
                form.EnableChanged += SetEnabled;
                if (openModels) form.Shown += delegate { form.OpenModelLibrary(); };
                accepted = form.ShowDialog() == DialogResult.OK;
                closeRequested = form.WindowCloseRequested;
                if (accepted)
                {
                    _settings.MicrophoneIndex = form.SelectedIndex;
                    _settings.RunAtStartup = form.StartWithWindows;
                    _settings.RunInBackground = form.KeepRunning;
                    _settings.ShortcutText = form.ShortcutText;
                    _settings.IdleUnloadMinutes = form.IdleUnloadMinutes;
                    _settings.FirstRunDone = true;
                    proposedModel = form.ModelId;
                    _hotkey.Configure(_settings.GetShortcut());
                    _tray.ContextMenuStrip.Items["shortcutHint"].Text = "Hold " + HotkeyText + " to dictate";
                    SaveSettings();
                    if (!Startup.Apply(_settings.RunAtStartup)) Notify("Could not update Windows startup registration.", ToolTipIcon.Warning);
                    // Reopen even the same selection to recover an unplugged
                    // endpoint or stopped capture stream when preferences save.
                    CloseMicrophone();
                }
                _settingsForm = null;
            }
            if (_exiting) return;
            if (closeRequested && !_settings.RunInBackground) { Quit(); return; }
            if (accepted && (proposedModel != _settings.ModelId || (_asr == null && _state != AppState.Sleeping && _state != AppState.Unloading)))
            {
                SpeechModel model = ModelCatalog.Find(proposedModel);
                string path = ModelCatalog.PathFor(model, ModelCatalog.DirectoryPath);
                if (!ModelCatalog.IsInstalled(model, ModelCatalog.DirectoryPath))
                { Notify("Download this model with Manage models before applying it.", ToolTipIcon.Warning); SetReady(); return; }
                StartModelLoad(model, path, false);
            }
            else SetReady();
        }
        void StartRecording()
        {
            if (!_exiting && _settings.Enabled && _settingsForm == null && _state == AppState.Sleeping)
            { WakeForRecording(); return; }
            if (_exiting || _asr == null || _mic == null || !_settings.Enabled || _state != AppState.Idle || _settingsForm != null) return;
            try
            {
                _targetIdentity = new TargetIdentity(TextInjector.CurrentForegroundWindow());
                _targetWindow = _targetIdentity.Window;
                _mic.BeginCapture();
                _pill.ShowListening(_targetWindow);
                SetState(AppState.Recording, "Listening - release " + HotkeyText + " to insert");
            }
            catch (Exception ex) { _pill.HidePill(); SetReady(); Notify("Could not record. " + ex.Message, ToolTipIcon.Warning); }
        }
        void CancelRecording()
        {
            if (_state == AppState.Waking)
            {
                if (_wakeCancelled) return;
                _wakeHeld = false; _wakeCancelled = true; _wakeSamples = null;
                CancelWork();
                _captureVersion++;
                try { if (_mic != null) _mic.EndCapture(); } catch (Exception ex) { Log("cancel wake: " + ex.Message); }
                _pill.HidePill(); RefreshHotkey();
                return;
            }
            if (_state != AppState.Recording) return;
            CancelWork();
            _captureVersion++;
            try { if (_mic != null) _mic.EndCapture(); } catch (Exception ex) { Log("cancel: " + ex.Message); }
            _pill.HidePill();
            SetReady();
        }
        void StopAndTranscribe()
        {
            if (_state == AppState.Waking)
            {
                _wakeHeld = false;
                try { if (_mic != null && !_wakeCancelled) _wakeSamples = _mic.EndCapture(); }
                catch (Exception ex) { _wakeCancelled = true; Notify(ex.Message, ToolTipIcon.Warning); }
                if (_wakeSamples != null && !_wakeCancelled) _pill.ShowWorking(_targetWindow, "Waking");
                else _pill.HidePill();
                RefreshHotkey();
                return;
            }
            if (_state != AppState.Recording || _mic == null || _exiting) return;
            float[] samples;
            try { samples = _mic.EndCapture(); }
            catch (Exception ex) { _pill.HidePill(); SetReady(); Notify(ex.Message, ToolTipIcon.Warning); return; }
            TranscribeSamples(samples);
        }
        void TranscribeSamples(float[] samples)
        {
            if (samples.Length < AudioCapture.SampleRate / 4) { _pill.HidePill(); SetReady(); return; }
            double peak = 0;
            foreach (float sample in samples) peak = Math.Max(peak, Math.Abs(sample));
            if (peak < 0.002)
            {
                _pill.HidePill(); SetReady();
                Notify("No sound reached the microphone. Check your input device and level.", ToolTipIcon.Info); return;
            }
            int version = ++_captureVersion;
            IntPtr target = _targetWindow;
            _pill.ShowWorking(target, "Processing");
            SetState(AppState.Working, "Transcribing\u2026");
            _transcribeToken = _work.Submit(delegate (CancellationToken cancel)
            {
                string text = null, error = null;
                Stopwatch clock = Stopwatch.StartNew();
                IAsrEngine engine = _asr;
                try
                {
                    lock (_inferGate)
                    {
                        if (engine == null || cancel.IsCancellationRequested) return new object[] { null, null };
                        float[] prepared = AudioPipeline.Prepare(samples, AudioCapture.SampleRate);
                        text = StreamingPrototype.Transcribe(engine, prepared, AudioCapture.SampleRate, 2);
                        if (text == null) text = engine.Transcribe(prepared, AudioCapture.SampleRate);
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                finally { samples = null; }
                Log("transcription " + clock.ElapsedMilliseconds + " ms" + (error != null ? ", failed" : ""));
                return new object[] { text, error };
            }, delegate (object state, bool cancelled)
            {
                _transcribeToken = null;
                if (_exiting) return;
                _pill.HidePill();
                if (cancelled) { SetReady(); return; }
                object[] result = state as object[];
                if (result == null) { SetReady(); Notify(WorkError(state), ToolTipIcon.Error); return; }
                string text = (string)result[0], error = (string)result[1];
                if (error != null) { SetReady(); Notify("Transcription failed: " + error, ToolTipIcon.Error); return; }
                text = (text ?? "").Trim();
                if (text.Length == 0) { SetReady(); return; }
                InsertAfterKeyRelease(text, target, version);
            });
        }
        void CancelWork()
        {
            CancelToken(ref _wakeToken);
            CancelToken(ref _transcribeToken);
        }
        static void CancelToken(ref CancellationTokenSource field)
        {
            CancellationTokenSource token = field;
            field = null;
            if (token != null) try { token.Cancel(); } catch (ObjectDisposedException) { }
        }
        internal bool CanPasteResult(int version)
        { return !_exiting && _settings.Enabled && version == _captureVersion; }
        void InsertAfterKeyRelease(string text, IntPtr target, int version)
        {
            if (TextInjector.ModifiersReleased()) { CommitText(text, target, version, false); return; }
            SetState(AppState.Working, "Release the shortcut keys to insert");
            Stopwatch wait = Stopwatch.StartNew();
            _pasteTimer = new System.Windows.Forms.Timer();
            _pasteTimer.Interval = 15;
            _pasteTimer.Tick += delegate
            {
                if (!CanPasteResult(version))
                {
                    _pasteTimer.Dispose(); _pasteTimer = null; SetReady(); return;
                }
                bool released = TextInjector.ModifiersReleased();
                if (!released && wait.ElapsedMilliseconds < 1500) return;
                _pasteTimer.Dispose(); _pasteTimer = null;
                CommitText(text, target, version, !released);
            };
            _pasteTimer.Start();
        }
        void CommitText(string text, IntPtr target, int version, bool clipboardOnly)
        {
            if (!CanPasteResult(version)) { SetReady(); return; }
            // Retain every valid result, including clipboard-only fallbacks.
            _lastText = text;
            if (!clipboardOnly && _targetIdentity != null && !_targetIdentity.Matches(TextInjector.CurrentForegroundWindow()))
            {
                string copied = TextInjector.CopyToClipboard(text) ? "The target application changed. Your text was copied; paste it with Ctrl+V." :
                    "The target application changed and the clipboard is unavailable. Use Copy last transcript from the tray to try again.";
                SetReady(); Notify(copied, ToolTipIcon.Info); return;
            }
            string error;
            if (clipboardOnly)
                error = TextInjector.CopyToClipboard(text) ? "Shortcut keys are still held. Your text was copied; release them and paste with Ctrl+V." : "Could not access the clipboard. Use Copy last transcript from the tray to try again.";
            else error = TextInjector.Paste(text, target);
            SetReady();
            if (error != null) Notify(error, ToolTipIcon.Info);
        }
        void CopyLastText()
        {
            if (_lastText.Length == 0) { Notify("Nothing has been dictated yet.", ToolTipIcon.Info); return; }
            if (TextInjector.CopyToClipboard(_lastText)) Notify("Last transcript copied.", ToolTipIcon.Info);
        }
        void SaveSettings()
        {
            if (!_settings.Save()) Notify("Could not save preferences. Check that your Ukti app-data folder is writable. Changes may be lost on restart.", ToolTipIcon.Warning);
        }
        void Notify(string message, ToolTipIcon icon) { if (!_exiting) _tray.ShowBalloonTip(7000, "Ukti", message, icon); }
        // Native resources own their lifetime via SafeHandle; callbacks are
        // marshalled by WorkCoordinator.Post.
        void Post(MethodInvoker action)
        {
            if (_exiting) return;
            try { _dispatcher.BeginInvoke((MethodInvoker)delegate { if (!_exiting) action(); }); }
            catch (InvalidOperationException) { }
        }
        internal static void Log(string message) { AppLogger.Info(message); }
        internal static void LogError(string message) { AppLogger.Error(message); }

        void Quit()
        {
            if (_exiting) return;
            _exiting = true;
            _captureVersion++;
            _hotkey.Dispose();

            _idleTimer.Dispose();
            _wakeSamples = null;
            if (_pasteTimer != null) { _pasteTimer.Dispose(); _pasteTimer = null; }
            if (_settingsForm != null) _settingsForm.Close();
            CloseMicrophone();
            CancelWork();
            _work.CancelAll();
            // Killing the isolated engine unblocks any in-flight pipe read;
            // only then join workers. Do not wait on inference's lock here.
            IAsrEngine resident = _asr;
            _asr = null;
            if (resident != null) resident.Dispose();
            _work.Dispose();
            _pill.HidePill();
            _pill.Dispose();
            // Quit is clicked from the tray menu, so that menu is still open.
            // Disposing the strip inside its own modal loop makes WinForms'
            // ModalMenuFilter re-parent a disposed drop-down and throw.
            // ExitThread ends the process; Windows reclaims the handles.
            _tray.ContextMenuStrip.Close();
            _tray.Visible = false;
            ExitThread();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (AsrWorker.IsWorker(args)) { Environment.ExitCode = AsrWorker.Run(args); return; }
            bool created;
            using (Mutex mutex = new Mutex(true, "Ukti.SingleInstance", out created))
            {
                if (!created)
                {
                    if (!Ipc.RequestShowWindow()) MessageBox.Show("Ukti is already running. Open it from the system tray.",
                        "Ukti", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                CrashDumps.Enable();
                Application.ThreadException += delegate (object sender, System.Threading.ThreadExceptionEventArgs e)
                { CrashLog.Write(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e)
                { CrashLog.Write(e.ExceptionObject as Exception); };
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp(args.Length > 0 && args[0] == "--background"));
            }
        }
    }
}
