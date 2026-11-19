using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Threading;
using System.Windows.Forms;

namespace Ukti
{
    // Opt-in interactive integration tests. Uses an isolated settings directory
    // and a scratch editor; never records audio or pastes into the user's apps.
    static class InteractionTests
    {
        delegate bool EnumWindow(IntPtr handle, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread, EnumWindow callback, IntPtr parameter);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        static T Field<T>(object target, string name)
        { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
        static void SetField(object target, string name, object value)
        { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }
        static void Invoke(object target, string method, params object[] args)
        { target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args); }
        static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        static void Pump(int milliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            do { Application.DoEvents(); Thread.Sleep(5); } while (DateTime.UtcNow < deadline);
        }
        static ContextMenuStrip VisibleMenu()
        {
            ContextMenuStrip result = null;
            EnumThreadWindows(GetCurrentThreadId(), delegate(IntPtr handle, IntPtr parameter)
            {
                ContextMenuStrip menu = Control.FromHandle(handle) as ContextMenuStrip;
                if (menu != null && menu.Visible) result = menu;
                return true;
            }, IntPtr.Zero);
            Assert(result != null, "The dropdown must open a native menu.");
            return result;
        }
        static void ClickMenuItem(ContextMenuStrip menu, int index)
        {
            Rectangle bounds = menu.Items[index].Bounds;
            int x = bounds.Left + bounds.Width / 2, y = bounds.Top + bounds.Height / 2;
            // Exercise ToolStrip's actual mouse-up / close / item-click order.
            // PerformClick on an item bypasses the menu lifecycle bug.
            Invoke(menu, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
            Invoke(menu, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
            Pump(30);
        }
        internal static void DropDownRegression(Action<bool, string> assert)
        {
            Type inputType = typeof(TextInjector).GetNestedType("INPUT", BindingFlags.NonPublic);
            assert(Marshal.SizeOf(inputType) == (IntPtr.Size == 8 ? 40 : 28), "Keyboard injection must use the full native INPUT union.");
            using (SettingsForm form = new SettingsForm(new Settings()))
            {
                form.Show(); Pump(30);
                DeviceRow microphone = Field<DeviceRow>(form, "_device");
                microphone.SelectedIndex = 99; // Does not need real audio hardware.
                int changes = 0;
                microphone.SelectionChanged += delegate { changes++; };
                microphone.ActivateControl();
                ClickMenuItem(VisibleMenu(), 0);
                assert(microphone.SelectedIndex == -1 && changes == 1, "A real menu click must update the microphone without disposing its menu mid-event.");
                IdleTimeoutRow idle = Field<IdleTimeoutRow>(form, "_idleTimeout");
                idle.ActivateControl();
                ClickMenuItem(VisibleMenu(), 5);
                assert(idle.Minutes == 30, "A real menu click must update the idle timeout without a disposed-menu exception.");
                form.Close();
            }
        }
        static void RememberMicrophone()
        {
            Assert(AudioCapture.DeviceCount() > 0, "This integration test needs an available microphone endpoint (it never opens it).");
            TrayApp app = new TrayApp(true, false);
            try
            {
                Settings settings = Field<Settings>(app, "_settings");
                settings.Enabled = false;
                Assert(settings.Save(), "Test preferences must be writable.");
                Invoke(app, "SetState", AppState.Faulted, "Settings integration test");
                Exception failure = null;
                Control dispatcher = Field<Control>(app, "_dispatcher");
                dispatcher.BeginInvoke((MethodInvoker)delegate
                {
                    SettingsForm form = Field<SettingsForm>(app, "_settingsForm");
                    try
                    {
                        DeviceRow row = Field<DeviceRow>(form, "_device");
                        row.ActivateControl();
                        ClickMenuItem(VisibleMenu(), 1); // First explicit microphone, not System default.
                        Assert(row.SelectedIndex == 0, "A mouse menu selection must reach the microphone picker.");
                        row.ActivateControl();
                        ContextMenuStrip reopened = VisibleMenu();
                        Assert(((ToolStripMenuItem)reopened.Items[1]).Checked, "Reopened menu must remember the selected microphone.");
                        reopened.Close();
                        IdleTimeoutRow idle = Field<IdleTimeoutRow>(form, "_idleTimeout");
                        idle.ActivateControl();
                        ClickMenuItem(VisibleMenu(), 5); // 30 minutes, same dropdown-lifetime path.
                        Assert(idle.Minutes == 30, "Idle-timeout menu must also survive a real menu click.");
                        ((HexButton)form.AcceptButton).PerformClick();
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        form.DialogResult = DialogResult.Cancel;
                        form.Close();
                    }
                });
                Invoke(app, "ShowSettings", false);
                if (failure != null) throw new Exception("Microphone menu/save interaction failed.", failure);
                Assert(settings.MicrophoneIndex == 0, "Saving must apply the microphone to the controller.");
                Settings saved = Settings.Load();
                Assert(saved.MicrophoneIndex == 0 && saved.IdleUnloadMinutes == 30, "A fresh settings load must retain the selected microphone and timeout.");
                using (SettingsForm reopened = new SettingsForm(saved))
                    Assert(Field<DeviceRow>(reopened, "_device").SelectedIndex == 0, "Reopened preferences must show the saved microphone.");
                Console.WriteLine("PASS: mouse dropdown selection -> Save -> disk reload -> reopened microphone choice.");
            }
            finally { Invoke(app, "Quit"); }
        }
        sealed class Recognizer : IAsrEngine
        {
            public string Version { get { return "integration-test"; } }
            public string Transcribe(float[] samples, int rate) { return "Ukti integration transcript."; }
            public void Dispose() { }
        }
        static float[] SynthesizedSpeech()
        {
            using (MemoryStream audio = new MemoryStream())
            using (SpeechSynthesizer voice = new SpeechSynthesizer())
            {
                voice.SetOutputToAudioStream(audio, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                voice.Speak("This is a test of voice typing. The microphone is working.");
                byte[] bytes = audio.ToArray();
                float[] samples = new float[bytes.Length / 2];
                new AudioPacketConverter(16000, 1, 16, 2, false).Convert(bytes, samples.Length, samples);
                return samples;
            }
        }
        static void PasteIntoEditor(bool realSpeech = false)
        {
            Type inputType = typeof(TextInjector).GetNestedType("INPUT", BindingFlags.NonPublic);
            Assert(Marshal.SizeOf(inputType) == (IntPtr.Size == 8 ? 40 : 28), "SendInput must receive the native INPUT size (40 bytes on x64), not a keyboard-only union.");
            string previous;
            bool hadClipboard = TextInjector.ReadClipboardForTest(out previous);
            TrayApp app = new TrayApp(true, false);
            using (Form editorWindow = new Form())
            using (TextBox editor = new TextBox())
            {
                try
                {
                    editorWindow.Text = "Ukti isolated paste integration test";
                    editorWindow.ClientSize = new Size(480, 180);
                    editor.Multiline = true; editor.Dock = DockStyle.Fill;
                    editorWindow.Controls.Add(editor);
                    editorWindow.Show(); editorWindow.Activate(); SetForegroundWindow(editorWindow.Handle); editor.Focus(); Pump(100);
                    Assert(TextInjector.CurrentForegroundWindow() == editorWindow.Handle, "Cannot focus the scratch editor; refusing to inject keystrokes elsewhere.");
                    Settings settings = Field<Settings>(app, "_settings");
                    settings.Enabled = true;
                    if (realSpeech)
                    {
                        SpeechModel model = ModelCatalog.Find("nemotron-en");
                        string modelPath = ModelCatalog.PathFor(model, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ukti", "models"));
                        Assert(File.Exists(modelPath), "The real-speech test needs an already-installed model; it never downloads one.");
                        SetField(app, "_asr", AsrBackend.Create(model, modelPath));
                    }
                    else SetField(app, "_asr", new Recognizer());
                    // No native microphone is opened: the test begins at the
                    // captured-sample boundary and uses a deterministic ASR.
                    SetField(app, "_mic", new AudioCapture());
                    SetField(app, "_targetWindow", editorWindow.Handle);
                    SetField(app, "_targetIdentity", new TargetIdentity(editorWindow.Handle));
                    float[] samples = realSpeech ? SynthesizedSpeech() : new float[16000];
                    if (!realSpeech)
                        for (int i = 0; i < samples.Length; i++) samples[i] = 0.1f * (float)Math.Sin(i * 0.1);
                    Assert(TextInjector.CurrentForegroundWindow() == editorWindow.Handle, "Scratch editor lost focus before transcription; refusing to paste elsewhere.");
                    Invoke(app, "TranscribeSamples", (object)samples);
                    DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                    while (editor.Text.Length == 0 && DateTime.UtcNow < deadline) Pump(20);
                    if (realSpeech)
                        Assert(editor.Text.IndexOf("microphone", StringComparison.OrdinalIgnoreCase) >= 0 && editor.Text.IndexOf("working", StringComparison.OrdinalIgnoreCase) >= 0,
                            "The real ASR must recognize the synthesized speech and paste its words into the editor. Actual: " + editor.Text);
                    else Assert(editor.Text == "Ukti integration transcript.", "Recognized transcript must actually appear in the focused editor, not only on the clipboard.");
                    Assert(Field<string>(app, "_lastText") == editor.Text, "Copy last transcript must retain the delivered text.");
                    Pump(600); // Let clipboard restoration finish before cleanup.
                    Console.WriteLine(realSpeech ? "PASS: synthesized speech -> real NeMo GPU ASR -> native paste -> editor text: " + editor.Text :
                        "PASS: captured samples -> ASR worker -> UI callback -> native Ctrl+V -> text in scratch editor.");
                }
                finally
                {
                    Invoke(app, "Quit");
                    if (hadClipboard) TextInjector.CopyToClipboard(previous);
                }
            }
        }
        internal static int Run(string[] args)
        {
            string root = Path.Combine(Path.GetTempPath(), "ukti-interaction-" + Guid.NewGuid().ToString("N"));
            AppData.TestRoot = root;
            try
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                if (args.Length < 2 || args[1] == "menu") RememberMicrophone();
                if (args.Length < 2 || args[1] == "paste") PasteIntoEditor();
                if (args.Length > 1 && args[1] == "speech") PasteIntoEditor(true);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
