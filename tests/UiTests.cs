using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace Ukti
{
    static class UiTests
    {
        static int _assertions;
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowLong(IntPtr handle, int index);
        static void Assert(bool value, string message) { _assertions++; if (!value) throw new Exception(message); }
        static T Field<T>(object target, string name)
        { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
        static void SetField(object target, string name, object value)
        { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }
        static void Invoke(object target, string name, params object[] args)
        { target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args); }
        static void Pump(int milliseconds)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
            do { Application.DoEvents(); Thread.Sleep(10); } while (DateTime.UtcNow < end);
        }
        static void WaitFor(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < deadline) Pump(20);
            Assert(condition(), "Asynchronous UI operation timed out.");
        }
        static void CheckBounds(Form form)
        {
            foreach (Control control in form.Controls)
                Assert(form.ClientRectangle.Contains(control.Bounds), control.GetType().Name + " is outside " + form.Text);
        }
        static void Snapshot(Form form, string directory, string name)
        {
            if (directory == null) return;
            form.Show(); Pump(150);
            using (Bitmap bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            { form.DrawToBitmap(bitmap, form.ClientRectangle); bitmap.Save(Path.Combine(directory, name)); }
        }
        static void ShortcutTests()
        {
            Shortcut chord; string error;
            foreach (string text in new string[] { "Ctrl+Space", "Alt+K", "Ctrl+Shift+D", "Ctrl+Alt+F8", "Ctrl+0", "Alt+9" })
            {
                Assert(Shortcut.TryParse(text, out chord), "Cannot parse " + text);
                Assert(chord.ToString() == text, "Shortcut did not round-trip: " + text);
            }
            foreach (string text in new string[] { "Space", "Shift+A", "Alt+F4", "Alt+Space", "Ctrl+Escape", "Ctrl+F12", "Ctrl+A+B", "Ctrl+65535", "Win+A" })
                Assert(!Shortcut.TryParse(text, out chord), "Invalid/reserved shortcut accepted: " + text);
            Assert(!Shortcut.TryCreate(Keys.A, Keys.Control | Keys.LWin, out chord, out error), "Windows modifier must not be accepted.");
            using (HoldHotkey hook = new HoldHotkey())
            {
                int pressed = 0, released = 0, cancelled = 0;
                hook.Pressed += delegate { pressed++; }; hook.Released += delegate { released++; }; hook.Cancelled += delegate { cancelled++; };
                hook.HandleKey((int)Keys.LControlKey, true);
                Assert(!hook.HandleKey((int)Keys.Space, true), "Disabled shortcut must not swallow normal input.");
                hook.HandleKey((int)Keys.Space, false);
                hook.Enabled = true;
                Assert(hook.HandleKey((int)Keys.Space, true), "Configured key must be consumed.");
                Assert(hook.HandleKey((int)Keys.Space, true), "Repeat must be consumed.");
                Assert(pressed == 1, "Repeat must not start a second recording.");
                hook.HandleKey((int)Keys.LControlKey, false);
                Assert(released == 1, "Releasing a required modifier must finish dictation.");
                Assert(hook.HandleKey((int)Keys.Space, false), "Consumed trigger must have its release consumed.");
                Assert(released == 1, "Release must fire only once.");
                hook.HandleKey((int)Keys.LControlKey, true);
                hook.HandleKey((int)Keys.Space, true);
                Assert(hook.HandleKey((int)Keys.Escape, true), "Esc must cancel recording.");
                Assert(cancelled == 1, "Cancel event missing.");
                hook.HandleKey((int)Keys.Escape, false);
                Assert(hook.HandleKey((int)Keys.Space, false), "Cancel must retain trigger release suppression.");
                hook.HandleKey((int)Keys.Space, true);
                hook.Enabled = false;
                Assert(cancelled == 2, "Turning off while held must cancel.");
                hook.HandleKey((int)Keys.Space, false);
                Assert(!hook.HandleKey((int)Keys.Space, true), "Turning off must restore ordinary typing.");
                hook.HandleKey((int)Keys.Space, false); hook.HandleKey((int)Keys.LControlKey, false);
                Shortcut.TryParse("Alt+Shift+K", out chord); hook.Configure(chord); hook.Enabled = true;
                hook.HandleKey((int)Keys.LMenu, true); hook.HandleKey((int)Keys.LShiftKey, true);
                Assert(hook.HandleKey((int)Keys.K, true), "Custom shortcut must start.");
                hook.HandleKey((int)Keys.K, false);
                Assert(released == 2, "Custom shortcut must release.");
                hook.HandleKey((int)Keys.LControlKey, true);
                Assert(!hook.HandleKey((int)Keys.K, true), "Extra modifiers must not trigger a different chord.");
                hook.HandleKey((int)Keys.K, false); hook.HandleKey((int)Keys.LControlKey, false);
                hook.HandleKey((int)Keys.LWin, true);
                Assert(!hook.HandleKey((int)Keys.K, true), "Windows shortcuts must not start dictation.");
            }
        }
        static void ModifierSnapshotTests()
        {
            using (HoldHotkey hook = new HoldHotkey())
            {
                hook.Enabled = true;
                bool[] snapshot = Field<bool[]>(hook, "_down");
                snapshot[(int)Keys.ControlKey] = snapshot[(int)Keys.LControlKey] = true;
                hook.HandleKey(RawInputKeyboard.NormalizeVirtualKey(0x11, 0x1D, 1), false);
                Assert(!hook.HandleKey((int)Keys.Space, true), "Releasing a modifier held during installation must not leave generic Ctrl stuck.");
                hook.HandleKey((int)Keys.Space, false);
                int releases = 0;
                hook.Released += delegate { releases++; };
                hook.HandleKey((int)Keys.LControlKey, true); hook.HandleKey((int)Keys.RControlKey, true);
                hook.HandleKey((int)Keys.Space, true);
                hook.HandleKey((int)Keys.LControlKey, false);
                Assert(releases == 0, "The second physical Ctrl must keep the chord held.");
                hook.HandleKey((int)Keys.RControlKey, false);
                Assert(releases == 1, "Releasing both physical modifiers must finish once.");
            }
        }
        static void SettingsPersistence()
        {
            Settings legacy = Settings.Parse(new string[] { "enabled=false", "microphone=1", "run_at_startup=true", "run_in_background=false" });
            Assert(!legacy.Enabled && legacy.MicrophoneIndex == 1 && legacy.RunAtStartup && !legacy.RunInBackground, "Old settings must survive upgrade.");
            Assert(legacy.ShortcutText == "Ctrl+Space" && legacy.ModelId == "nemotron-en", "New preferences must have safe defaults.");
            Assert(legacy.IdleUnloadMinutes == 5, "Existing installs must acquire a five-minute default.");
            foreach (int minutes in IdleUnloadPolicy.Choices)
            {
                Settings configured = Settings.Parse(new string[] { "idle_unload_minutes=" + minutes });
                Assert(configured.IdleUnloadMinutes == minutes, "Idle timeout choice must parse.");
                Assert(Settings.Parse(configured.Serialize().Split('\n')).IdleUnloadMinutes == minutes, "Idle timeout must persist.");
            }
            foreach (string invalidTimeout in new string[] { "-1", "1441", "invalid", "2147483648" })
                Assert(Settings.Parse(new string[] { "idle_unload_minutes=" + invalidTimeout }).IdleUnloadMinutes == 5, "Invalid timeouts must use the default.");
            legacy.ShortcutText = "Alt+Shift+K"; legacy.ModelId = "parakeet-tdt";
            Settings copy = Settings.Parse(legacy.Serialize().Split(new char[] { '\n' }));
            Assert(copy.ShortcutText == legacy.ShortcutText && copy.ModelId == legacy.ModelId, "New preferences must persist.");
            Settings invalid = Settings.Parse(new string[] { "shortcut=Alt+F4", "model=untrusted-model", "microphone=-10" });
            Assert(invalid.ShortcutText == "Ctrl+Space" && invalid.ModelId == "nemotron-en" && invalid.MicrophoneIndex == -1, "Invalid settings must fall back safely.");
            string root = AppData.TestRoot;
            try
            {
                AppData.TestRoot = Path.Combine(root, "settings-persistence");
                Settings saved = new Settings { MicrophoneIndex = 2 };
                Assert(saved.Save(), "First save must create its app-data folder and settings file.");
                Assert(Settings.Load().MicrophoneIndex == 2, "A disk reload must remember the chosen microphone.");
                saved.MicrophoneIndex = 1;
                Assert(saved.Save() && Settings.Load().MicrophoneIndex == 1, "Replacing existing settings must retain the new microphone choice.");
                Assert(Directory.GetFiles(AppData.TestRoot, "*.tmp").Length == 0, "Atomic settings save must not leave temporary files behind.");
                string blocked = Path.Combine(root, "blocked-settings-folder");
                File.WriteAllText(blocked, "not a directory");
                AppData.TestRoot = blocked;
                Assert(!saved.Save(), "A failed settings write must report failure, not pretend the preference was saved.");
            }
            finally { AppData.TestRoot = root; }
        }
        static void SettingsUI(string output, string cache)
        {
            for (int flags = 0; flags < 8; flags++)
            {
                Settings source = new Settings();
                source.Enabled = (flags & 1) != 0; source.RunAtStartup = (flags & 2) != 0; source.RunInBackground = (flags & 4) != 0;
                using (SettingsForm form = new SettingsForm(source, cache))
                {
                    CheckBounds(form);
                    ((HexButton)form.AcceptButton).PerformClick();
                    Assert(form.DialogResult == DialogResult.OK, "Save must accept.");
                    Assert(form.EnabledNow == source.Enabled && form.StartWithWindows == source.RunAtStartup && form.KeepRunning == source.RunInBackground, "Boolean preferences changed during layout.");
                    Assert(form.SelectedIndex == -1 && form.ShortcutText == "Ctrl+Space" && form.ModelId == "nemotron-en", "Save lost initial preferences.");
                    Assert(form.IdleUnloadMinutes == 5, "Save must retain the default idle timeout.");
                }
            }
            Settings original = new Settings();
            using (SettingsForm form = new SettingsForm(original, cache))
            {
                form.Show(); Pump(40);
                Snapshot(form, output, "settings.png");
                ToggleSwitch enabled = Field<ToggleSwitch>(form, "_enabled");
                enabled.Focus();
                Assert(enabled.Focused && enabled.TabStop, "Switch must accept keyboard focus.");
                bool? immediate = null;
                form.EnableChanged += delegate(bool value) { immediate = value; };
                KeyEventArgs key = new KeyEventArgs(Keys.Space);
                Invoke(enabled, "OnKeyDown", key);
                Assert(!key.SuppressKeyPress, "KeyDown must preserve KeyUp for activation.");
                Invoke(enabled, "OnKeyUp", new KeyEventArgs(Keys.Space));
                Assert(immediate == false && !enabled.Checked, "On/off must notify the app immediately, before Save.");
                Assert((enabled.AccessibilityObject.State & AccessibleStates.Checked) == 0, "Unchecked accessible state is wrong.");
                Snapshot(form, output, "settings-paused.png");
                enabled.AccessibilityObject.DoDefaultAction();
                Assert(immediate == true && enabled.Checked, "Accessible toggle must notify immediately.");
                Assert((enabled.AccessibilityObject.State & AccessibleStates.Checked) != 0, "Checked accessible state missing.");
                ShortcutRecorder recorder = Field<ShortcutRecorder>(form, "_shortcut");
                recorder.ActivateControl();
                Assert(recorder.IsRecording, "Shortcut control must enter recording mode.");
                Snapshot(form, output, "settings-shortcut.png");
                recorder.RecordKey(Keys.Control | Keys.Shift | Keys.K);
                Assert(recorder.Value.ToString() == "Ctrl+Shift+K" && !recorder.IsRecording, "Shortcut recorder did not accept the chord.");
                recorder.ActivateControl(); recorder.RecordKey(Keys.Alt | Keys.F4);
                Assert(recorder.IsRecording, "Reserved shortcut must remain unaccepted.");
                recorder.RecordKey(Keys.Escape);
                Assert(!recorder.IsRecording && form.Visible && recorder.Value.ToString() == "Ctrl+Shift+K", "Esc must cancel recording, not close Settings.");
                Field<ToggleSwitch>(form, "_startAuto").ActivateControl();
                Field<ToggleSwitch>(form, "_keepRunning").ActivateControl();
                DeviceRow device = Field<DeviceRow>(form, "_device");
                int selected = AudioCapture.DeviceCount() > 0 ? 0 : -1;
                device.SelectedIndex = selected;
                Assert(device.AccessibleRole == AccessibleRole.ButtonDropDown, "Microphone picker must expose its role.");
                Field<IdleTimeoutRow>(form, "_idleTimeout").Minutes = 30;
                SetField(form, "_selectedModel", ModelCatalog.Find("parakeet-tdt"));
                ((HexButton)form.AcceptButton).PerformClick();
                Assert(form.EnabledNow && form.StartWithWindows && !form.KeepRunning, "Save must transfer changed booleans.");
                Assert(form.ShortcutText == "Ctrl+Shift+K" && form.SelectedIndex == selected && form.ModelId == "parakeet-tdt", "Save must transfer the custom shortcut, microphone and model.");
                Assert(form.IdleUnloadMinutes == 30 && original.IdleUnloadMinutes == 5, "Timeout edits must be staged until Save.");
            }
            using (SettingsForm form = new SettingsForm(original, cache))
            {
                form.Show();
                Field<ShortcutRecorder>(form, "_shortcut").Value = Shortcut.Default;
                Field<IdleTimeoutRow>(form, "_idleTimeout").Minutes = 0;
                Invoke(form, "ProcessDialogKey", Keys.Escape);
                Assert(form.DialogResult == DialogResult.Cancel && !form.WindowCloseRequested, "Escape must cancel unsaved preferences.");
                Assert(original.Enabled && original.ModelId == "nemotron-en" && original.IdleUnloadMinutes == 5, "Cancel must not mutate its source settings or timeout.");
            }
            using (SettingsForm form = new SettingsForm(original, cache))
            {
                form.Show();
                foreach (Control control in form.Controls) if (control.AccessibleName == "Close") ((HexButton)control).PerformClick();
                Assert(form.DialogResult == DialogResult.Cancel && form.WindowCloseRequested, "Close must work and report window-close intent.");
            }
            using (SettingsForm form = new SettingsForm(original, cache))
            { form.Show(); ((HexButton)form.CancelButton).PerformClick(); Assert(form.DialogResult == DialogResult.Cancel, "Cancel must work."); }
            using (SettingsForm form = new SettingsForm(original, cache))
            { form.Show(); Invoke(form, "ProcessDialogKey", Keys.Enter); Assert(form.DialogResult == DialogResult.OK, "Enter must save outside recording mode."); }
        }
        static void ControllerTests()
        {
            TrayApp startup = new TrayApp(true, true);
            try
            {
                Assert(Field<System.Windows.Forms.Timer>(startup, "_idleTimer").Enabled,
                    "Idle timer must start before startup/preferences enter a modal loop.");
            }
            finally { Invoke(startup, "Quit"); }
            Pump(20); // The deferred startup must also respect exit before loading anything.
            // Deferred startup keeps this test independent of CUDA, microphones,
            // first-run dialogs and global hooks. Settings live beside the test
            // executable in the runner's isolated temporary directory.
            TrayApp app = new TrayApp(true, false);
            try
            {
                AudioCapture mic = new AudioCapture();
                SetField(mic, "_level", 0.125f);
                SetField(app, "_mic", mic);
                Assert(Field<PillForm>(app, "_pill").CaptureLevel() == 0.125f, "The listening pill must read the active microphone level.");
                Invoke(app, "CloseMicrophone");
                Assert(Field<PillForm>(app, "_pill").CaptureLevel() == 0f, "A closed microphone must show zero level.");
                Invoke(app, "SetState", AppState.Faulted, "No test engine");
                Invoke(app, "SetEnabled", false);
                Assert(!Field<HoldHotkey>(app, "_hotkey").Enabled, "Paused app must not consume its shortcut.");
                Assert(Field<AppState>(app, "_state") == AppState.Faulted, "Pause must not turn a missing engine into a fake Ready state.");
                Invoke(app, "SetEnabled", true);
                Assert(Field<AppState>(app, "_state") == AppState.Faulted, "Enable must not claim Ready without a model.");
                SetField(app, "_captureVersion", 42);
                Invoke(app, "SetState", AppState.Working, "Test inference");
                Assert(app.CanPasteResult(42), "Current inference result should be eligible while enabled.");
                Invoke(app, "SetEnabled", false);
                Assert(Field<AppState>(app, "_state") == AppState.Working, "Pause must retain the in-flight inference state until completion.");
                Assert(!app.CanPasteResult(42), "Pause must discard an in-flight transcript.");
                Invoke(app, "SetEnabled", true);
                Assert(!app.CanPasteResult(42), "Re-enable must not revive a discarded result.");
                Assert(!Field<HoldHotkey>(app, "_hotkey").Enabled, "Shortcut must remain inactive while inference is in flight.");
                Assert(!Field<HoldHotkey>(app, "_hotkey").IsHooked, "Unit tests must not install a global hook.");
            }
            finally { Invoke(app, "Quit"); }
        }

        static void IdleLifecycleTests(string cache)
        {
            Assert(!IdleUnloadPolicy.IsDue(0, TimeSpan.FromDays(100)), "Never must disable unloading.");
            Assert(!IdleUnloadPolicy.IsDue(5, TimeSpan.FromSeconds(299)), "Must not unload before the timeout.");
            Assert(IdleUnloadPolicy.IsDue(5, TimeSpan.FromSeconds(300)), "Must unload at the timeout.");
            Assert(!IdleUnloadPolicy.IsDue(-1, TimeSpan.FromDays(10)), "Invalid timeouts must not execute.");
            string weights = Path.Combine(cache, "idle-test.gguf");
            File.WriteAllBytes(weights, new byte[] { 1, 2, 3 });
            TrayApp app = new TrayApp(true, false);
            try
            {
                Settings settings = Field<Settings>(app, "_settings");
                settings.Enabled = false; settings.IdleUnloadMinutes = 5;
                // Empty native handles let us check actual disposal orchestration
                // without loading CUDA or opening any microphone in unit tests.
                NemoAsr resident = new NemoAsr();
                SetField(app, "_asr", resident); SetField(app, "_loadedModelPath", weights);
                Invoke(app, "SetState", AppState.Idle, "Test idle");
                Assert(!app.TryUnloadIdleModel(TimeSpan.FromSeconds(299)), "Controller must wait for the timeout.");
                foreach (AppState busy in new AppState[] { AppState.Loading, AppState.Recording, AppState.Working, AppState.Waking, AppState.Unloading })
                {
                    Invoke(app, "SetState", busy, "Busy test");
                    Assert(!app.TryUnloadIdleModel(TimeSpan.FromHours(1)), "Must not unload during " + busy);
                }
                Invoke(app, "SetState", AppState.Idle, "Test idle");
                using (SettingsForm preferences = new SettingsForm(settings, cache))
                {
                    SetField(app, "_settingsForm", preferences);
                    Assert(!app.TryUnloadIdleModel(TimeSpan.FromSeconds(299)), "Open settings must still respect the timeout.");
                    SetField(app, "_settingsForm", null);
                }
                settings.IdleUnloadMinutes = 0;
                Assert(!app.TryUnloadIdleModel(TimeSpan.FromHours(1)), "Never must be honored by the controller.");
                settings.IdleUnloadMinutes = 5;
                using (SettingsForm preferences = new SettingsForm(settings, cache))
                {
                    SetField(app, "_settingsForm", preferences);
                    Invoke(app, "SetState", AppState.Faulted, "Simulated microphone error");
                    Assert(app.TryUnloadIdleModel(TimeSpan.FromMinutes(5)), "Faulted resident model must unload even with Settings open.");
                    Assert(Field<NemoAsr>(app, "_asr") == null, "Detach the recognizer before background disposal.");
                    Invoke(app, "SetReady");
                    Assert(Field<AppState>(app, "_state") == AppState.Unloading, "Closing/saving preferences during unload must not fault or reload the model.");
                    WaitFor(delegate { return Field<AppState>(app, "_state") == AppState.Sleeping; });
                    SetField(app, "_settingsForm", null);
                }
                Assert(Field<bool>(resident, "_disposed"), "Idle unloading must actually dispose the recognizer, not just trim the working set.");
                Assert(File.Exists(weights), "Idle unloading must retain downloaded weights.");
                Assert(Field<AudioCapture>(app, "_mic") == null && !Field<HoldHotkey>(app, "_hotkey").IsHooked, "Paused sleeping app must hold no mic or global hook.");
                Invoke(app, "SetReady");
                Assert(Field<AppState>(app, "_state") == AppState.Sleeping, "Closing settings must not reload or fault an intentionally sleeping model.");
                settings.Enabled = true;
                Invoke(app, "RefreshHotkey");
                Assert(Field<HoldHotkey>(app, "_hotkey").Enabled, "Sleeping model must keep a wake-capable shortcut.");
                settings.Enabled = false; Invoke(app, "RefreshHotkey");

                // A hold released before warm-up completes must retain its audio.
                AudioCapture microphone = new AudioCapture();
                SetField(microphone, "_open", true); // No native handle; never call Open.
                Field<List<float>>(microphone, "_samples").AddRange(new float[] { 0.1f, 0.2f, 0.1f });
                SetField(app, "_mic", microphone);
                SetField(app, "_wakeHeld", true); SetField(app, "_wakeCancelled", false);
                Invoke(app, "SetState", AppState.Waking, "Wake test");
                Invoke(app, "StopAndTranscribe");
                Assert(!Field<bool>(app, "_wakeHeld") && Field<float[]>(app, "_wakeSamples").Length == 3, "Release during warm-up must buffer the captured utterance.");
                Invoke(app, "SetEnabled", false);
                Assert(Field<bool>(app, "_wakeCancelled") && Field<float[]>(app, "_wakeSamples") == null, "Pausing after release must discard buffered wake audio, even though the hook is no longer armed.");
                Invoke(app, "SetEnabled", true);
                Assert(Field<bool>(app, "_wakeCancelled") && !Field<HoldHotkey>(app, "_hotkey").Enabled, "Re-enabling during warm-up must not revive a released utterance.");
                settings.Enabled = false;
                SetField(app, "_wakeCancelled", false); SetField(app, "_wakeHeld", true);
                SetField(app, "_wakeSamples", new float[] { 0.1f });
                Invoke(app, "CancelRecording");
                Assert(Field<bool>(app, "_wakeCancelled") && Field<float[]>(app, "_wakeSamples") == null, "Esc while holding must discard buffered wake audio.");
                NemoAsr reloaded = new NemoAsr();
                Invoke(app, "FinishWake", reloaded, null);
                Assert(Field<AppState>(app, "_state") == AppState.Idle && Field<NemoAsr>(app, "_asr") == reloaded, "Cancelled wake may leave a ready model but must not record or paste.");
                Assert(Field<System.Windows.Forms.Timer>(app, "_pasteTimer") == null && Field<AudioCapture>(app, "_mic") == null, "Pausing during wake must leave no pending insertion or microphone.");
                Assert(!Field<HoldHotkey>(app, "_hotkey").IsHooked, "Lifecycle unit tests must not install a hook.");
            }
            finally { Invoke(app, "Quit"); }

            File.Delete(weights);
        }
        static void CancelQueuedWakeTests(string cache)
        {
            TrayApp app = new TrayApp(true, false);
            using (CountdownEvent started = new CountdownEvent(2))
            using (ManualResetEventSlim release = new ManualResetEventSlim())
            {
                try
                {
                    Settings settings = Field<Settings>(app, "_settings");
                    settings.Enabled = true;
                    SetField(app, "_loadedModelPath", Path.Combine(cache, "queued-wake.gguf"));
                    WorkCoordinator work = Field<WorkCoordinator>(app, "_work");
                    for (int i = 0; i < 2; i++)
                        work.Submit(delegate { started.Signal(); release.Wait(); return null; }, null);
                    Assert(started.Wait(5000), "Wake cancellation test must occupy both workers.");
                    Invoke(app, "SetState", AppState.Sleeping, "Queued wake test");
                    Invoke(app, "StartRecording");
                    Assert(Field<AppState>(app, "_state") == AppState.Waking, "Wake must queue behind the blocked workers.");
                    Invoke(app, "CancelRecording");
                    settings.Enabled = false; // Completion must not install an input sink in this unit test.
                    release.Set();
                    WaitFor(delegate { return Field<AppState>(app, "_state") == AppState.Sleeping; });
                    Assert(Field<IAsrEngine>(app, "_asr") == null && Field<AudioCapture>(app, "_mic") == null,
                        "Pre-cancelled wake must return safely to sleep without opening mic/ASR or indexing a null result.");
                    Assert(!Field<PillForm>(app, "_pill").Visible, "Cancelled wake must hide its capsule.");
                }
                finally { release.Set(); Invoke(app, "Quit"); }
            }
        }
        static void ModelCatalogTests(string cache)
        {
            Assert(ModelCatalog.All.Length == 4, "Expected four compatible ASR choices.");
            foreach (SpeechModel model in ModelCatalog.All)
            {
                Assert(model.Url.StartsWith("https://huggingface.co/nvidia/") && model.Revision.Length == 40, "Download must use a pinned official artifact.");
                Assert(model.Sha256.Length == 64 && model.Bytes > 0, "Model must have size and hash verification.");
                Assert(!ModelCatalog.IsInstalled(model, cache), "An absent model must not be reported as downloaded.");
                Assert(model.Multilingual == (model.LanguageCode == null), "Multilingual model must use auto language detection.");
            }
        }
        static SpeechModel TinyModel(byte[] bytes)
        {
            string hash;
            using (SHA256 sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            SpeechModel display = ModelCatalog.All[0];
            return new SpeechModel(display.Id, display.Name, display.Description, display.Repo, display.Revision,
                "test.gguf", bytes.Length, hash, display.License, false, 0.16f, 1);
        }
        static void DownloadIntegrity(string cache)
        {
            byte[] bytes = new byte[] { 1, 2, 3, 4, 5, 6 };
            SpeechModel model = TinyModel(bytes);
            string partial = Path.Combine(cache, "verified.partial");
            File.WriteAllBytes(partial, bytes);
            Assert(ModelCatalog.VerifyFile(partial, model, delegate { return false; }) == null, "Valid file must verify.");
            File.WriteAllBytes(partial, new byte[] { 6, 5, 4, 3, 2, 1 });
            Assert(ModelCatalog.VerifyFile(partial, model, delegate { return false; }) != null, "Wrong hash must fail.");
            File.WriteAllBytes(partial, new byte[] { 1 });
            Assert(ModelCatalog.VerifyFile(partial, model, delegate { return false; }) != null, "Wrong size must fail.");
            bool cancelled = false;
            try { ModelCatalog.VerifyFile(partial, model, delegate { return true; }); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled, "Verification must honor cancellation.");
            File.WriteAllBytes(partial, bytes);
            using (ModelSetupForm form = new ModelSetupForm(model, cache))
            {
                form.Show();
                SetField(form, "_partialPath", partial); Invoke(form, "SetBusy", true);
                Invoke(form, "OnComplete", null, new AsyncCompletedEventArgs(null, false, null));
                WaitFor(delegate { return form.DialogResult == DialogResult.OK; });
                Assert(form.SelectedModel == model && File.Exists(Path.Combine(cache, model.FileName)), "Only a verified file may be promoted and selected.");
            }
            string target = Path.Combine(cache, model.FileName);
            File.WriteAllBytes(partial, new byte[] { 6, 5, 4, 3, 2, 1 });
            using (ModelSetupForm form = new ModelSetupForm(model, cache))
            {
                form.Show(); SetField(form, "_partialPath", partial); Invoke(form, "SetBusy", true);
                Invoke(form, "OnComplete", null, new AsyncCompletedEventArgs(null, false, null));
                WaitFor(delegate { return Field<HexButton>(form, "_download").Enabled; });
                Assert(form.DialogResult != DialogResult.OK && File.ReadAllBytes(target)[0] == 1, "Failed verification must preserve the previous model.");
                Assert(!File.Exists(partial), "Failed partial file must be cleaned up.");
                ((HexButton)form.CancelButton).PerformClick();
            }
            File.WriteAllBytes(partial, bytes);
            using (ModelSetupForm form = new ModelSetupForm(model, cache))
            {
                form.Show(); SetField(form, "_partialPath", partial); Invoke(form, "SetBusy", true);
                ((HexButton)form.CancelButton).PerformClick();
                Invoke(form, "OnComplete", null, new AsyncCompletedEventArgs(null, true, null));
                Assert(!File.Exists(partial) && File.ReadAllBytes(target)[0] == 1, "Cancelled downloads must not touch the cached model.");
            }
            File.Delete(target);
        }
        static void ModelUI(string output, string cache)
        {
            using (ModelSetupForm form = new ModelSetupForm(ModelCatalog.All[0], cache))
            {
                form.Show();
                CheckBounds(form);
                ModelChoice[] choices = Field<ModelChoice[]>(form, "_choices");
                Assert(choices.Length == 4 && choices[0].Selected, "Library must show the selected model.");
                choices[2].AccessibilityObject.DoDefaultAction();
                Assert(choices[2].Selected && !choices[0].Selected, "Model choices must select exclusively.");
                Assert((choices[2].AccessibilityObject.State & AccessibleStates.Checked) != 0, "Model choice must expose selection.");
                Assert(Field<HexButton>(form, "_download").Text == "Download & use", "Uninstalled model must offer download.");
                choices[0].ActivateControl();
                Invoke(choices[0], "OnKeyDown", new KeyEventArgs(Keys.Down));
                Assert(choices[1].Selected, "Arrow keys must move the model selection.");
                Invoke(choices[1], "OnKeyDown", new KeyEventArgs(Keys.Up));
                Assert(choices[0].Selected, "Arrow keys must move back through the model choices.");
                Snapshot(form, output, "setup.png");
                Invoke(form, "SetBusy", true);
                Assert(!choices[0].Enabled && !Field<HexButton>(form, "_download").Enabled, "Changing models and starting a second download must be blocked while busy.");
                Assert(!Field<HexButton>(form, "_delete").Enabled, "Delete must be blocked during download/verification.");
                DownloadProgressChangedEventArgs progress = (DownloadProgressChangedEventArgs)Activator.CreateInstance(
                    typeof(DownloadProgressChangedEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new object[] { 50, null, 349936480L, 699872960L }, null);
                Invoke(form, "OnProgress", null, progress);
                Assert(Field<int>(form, "_progressValue") == 50, "Progress track must match bytes received.");
                Snapshot(form, output, "setup-progress.png");
                Invoke(form, "Fail", "Download failed. Check your connection and try again. Your current model has not changed.");
                Assert(Field<HexButton>(form, "_download").Text == "Try again" && choices[0].Enabled, "Failure must offer retry and re-enable model selection.");
                Snapshot(form, output, "setup-error.png");
                ((HexButton)form.CancelButton).PerformClick();
                Assert(form.DialogResult == DialogResult.Cancel && form.SelectedModel == null, "Cancel must not select a model.");
            }
        }
        static void ModelDeletionTests(string output, string cache)
        {
            byte[] bytes = new byte[] { 1, 2, 3, 4, 5, 6 };
            SpeechModel model = TinyModel(bytes);
            string canonical = Path.Combine(cache, model.FileName), legacy = Path.Combine(cache, "nemotron-en.q8_0.gguf");
            string unrelated = Path.Combine(cache, "keep.gguf"), partial = Path.Combine(cache, model.FileName + ".partial");
            File.WriteAllBytes(canonical, bytes); File.WriteAllBytes(legacy, bytes);
            File.WriteAllBytes(unrelated, bytes); File.WriteAllBytes(partial, bytes);
            Assert(ModelCatalog.StoredBytes(model, cache) == bytes.Length * 2, "Disk usage must include both canonical and legacy copies.");
            int confirmations = 0;
            using (ModelSetupForm protectedForm = new ModelSetupForm(model, cache, model.Id, delegate { confirmations++; return true; }))
            {
                protectedForm.Show();
                Assert(!Field<HexButton>(protectedForm, "_delete").Enabled, "Current model's delete action must be disabled.");
                Invoke(protectedForm, "DeleteSelectedModel");
                Assert(confirmations == 0 && File.Exists(canonical) && File.Exists(legacy), "Protected model must not be confirmed or deleted.");
            }
            bool blocked = false;
            try { ModelCatalog.DeleteFiles(model, cache, model.Id); } catch (InvalidOperationException) { blocked = true; }
            Assert(blocked && File.Exists(canonical), "The deletion backend must also protect the current model.");
            using (ModelSetupForm cancelled = new ModelSetupForm(model, cache, "other-model", delegate { confirmations++; return false; }))
            {
                Invoke(cancelled, "DeleteSelectedModel");
                Assert(File.Exists(canonical) && File.Exists(legacy), "Cancelling confirmation must retain all weights.");
            }
            using (ModelSetupForm deletable = new ModelSetupForm(model, cache, "other-model", delegate { confirmations++; return true; }))
            {
                deletable.Show(); CheckBounds(deletable);
                Assert(Field<HexButton>(deletable, "_delete").Enabled, "Unused downloaded models must be deletable.");
                Invoke(deletable, "SetBusy", true); Invoke(deletable, "DeleteSelectedModel");
                Assert(File.Exists(canonical), "Programmatic deletion must also be blocked while busy.");
                Invoke(deletable, "SetBusy", false);
                // Render the downloaded state using a tiny synthetic artifact,
                // rather than writing hundreds of MB just to make a screenshot.
                Field<ModelChoice[]>(deletable, "_choices")[0].Installed = true;
                Snapshot(deletable, output, "models-delete.png");
                Field<HexButton>(deletable, "_delete").PerformClick();
                Assert(!File.Exists(canonical) && !File.Exists(legacy), "Delete must remove canonical and legacy weights.");
                Assert(File.Exists(unrelated) && File.Exists(partial), "Delete must not touch unrelated files or in-progress downloads.");
                Assert(!Field<HexButton>(deletable, "_delete").Enabled && Field<HexButton>(deletable, "_download").Text == "Download & use", "Deleted model must immediately offer download again.");
                Assert(deletable.SelectedModel == null && deletable.DialogResult != DialogResult.OK, "Deleting must not activate a model or close the chooser.");
            }
            Assert(confirmations == 2, "Confirmation must be requested exactly once per permitted deletion attempt.");
            File.Delete(unrelated); File.Delete(partial);
        }
        static void PillTests(string output)
        {
            using (Form backdrop = new Form())
            using (PillForm pill = new PillForm())
            {
                backdrop.ShowInTaskbar = false; backdrop.Show(); Pump(150);
                pill.CaptureLevel = delegate { return 0.035f; };
                pill.ShowListening(backdrop.Handle);
                Assert((GetWindowLong(pill.Handle, -20) & 0x08000028) == 0x08000028, "Pill must be topmost, no-activate and click-through.");
                Pump(200);
                Assert(GetForegroundWindow() != pill.Handle, "Listening must never take focus.");
                SnapshotPill(pill, output, "pill.png");
                pill.ShowWorking(backdrop.Handle, "Processing"); Pump(100);
                Assert(GetForegroundWindow() != pill.Handle, "Processing must never take focus.");
                using (Font font = new Font("Segoe UI", 9.5f, FontStyle.Bold))
                    Assert(pill.Width >= TextRenderer.MeasureText("Processing\u2026", font).Width + 85, "Processing text and brand mark must fit.");
                SnapshotPill(pill, output, "pill-processing.png");
                pill.HidePill(); Assert(!pill.Visible, "Hide must remove the pill.");
            }
        }
        static void SnapshotPill(PillForm pill, string output, string name)
        {
            using (Bitmap frame = pill.RenderFrame())
            {
                Assert(frame.GetPixel(0, 0).A == 0, "Capsule corners must be transparent.");
                if (output == null) return;
                using (Bitmap bitmap = new Bitmap(500, 132))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                    { g.Clear(Color.FromArgb(56, 56, 56)); g.DrawImageUnscaled(frame, (500 - frame.Width) / 2, (132 - frame.Height) / 2); }
                    bitmap.Save(Path.Combine(output, name));
                }
            }
        }
        internal static int Entry(string[] args) { return Main(args); }
        [STAThread]
        static int Main(string[] args)
        {
            string cache = Path.Combine(Path.GetTempPath(), "ukti-test-cache-" + Guid.NewGuid().ToString("N"));
            try
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Directory.CreateDirectory(cache);
                AppData.TestRoot = Path.Combine(cache, "appdata");
                Assert(HotkeyInputTests.Run() == 0, "Native keyboard listener regression tests failed.");
                string output = args.Length > 0 ? args[0] : null;
                if (output != null) Directory.CreateDirectory(output);
                AppDataTests.Run(delegate(bool ok, string message) { Assert(ok, message); });
                WorkCoordinatorTests.Run(delegate(bool ok, string message) { Assert(ok, message); });
                AudioCaptureTests.Run(delegate(bool ok, string message) { Assert(ok, message); });
                ShortcutTests(); ModifierSnapshotTests(); SettingsPersistence(); ModelCatalogTests(cache);
                ChordTests.Run(delegate(bool ok, string message) { Assert(ok, message); });
                ControllerUnitTests.Run(delegate(bool ok, string message) { Assert(ok, message); });
                WerTests.Run(delegate(bool ok, string message) { Assert(ok, message); });
                InteractionTests.DropDownRegression(delegate(bool ok, string message) { Assert(ok, message); });
                SettingsUI(output, cache); ModelUI(output, cache); DownloadIntegrity(cache); ModelDeletionTests(output, cache);
                ControllerTests(); IdleLifecycleTests(cache); CancelQueuedWakeTests(cache); PillTests(output);
                Assert(Theme.MarkGlyph.Width == 512 && Theme.AppIcon != null, "Supplied branding must be embedded.");
                Assert(QuitMenuTests.RunQuitTest() == 0, "Quitting with an open tray menu must not dispose it mid-event.");
                Console.WriteLine("PASS: " + _assertions + " assertions. No network, global hooks or user settings touched.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
        }
    }
}
