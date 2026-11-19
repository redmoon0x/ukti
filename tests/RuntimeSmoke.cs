using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Ukti
{
    static class RuntimeSmoke
    {
        static T Field<T>(object target, string name)
        { return (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }
        static void SetField(object target, string name, object value)
        { target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value); }
        static void Invoke(object target, string name, params object[] args)
        { target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, args); }
        static long PrivateBytes()
        { using (Process process = Process.GetCurrentProcess()) return process.PrivateMemorySize64; }
        static void WaitForSleep(TrayApp app)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (Field<AppState>(app, "_state") != AppState.Sleeping && DateTime.UtcNow < deadline)
            { Application.DoEvents(); Thread.Sleep(10); }
            if (Field<AppState>(app, "_state") != AppState.Sleeping) throw new Exception("Idle unload did not finish.");
        }
        static void TestIdleUnload(SpeechModel model, string path)
        {
            TrayApp app = new TrayApp(true, false);
            try
            {
                Settings settings = Field<Settings>(app, "_settings");
                settings.Enabled = false; settings.ModelId = model.Id; settings.IdleUnloadMinutes = 1;
                SetField(app, "_loadedModelPath", path);
                long baseline = PrivateBytes();
                Console.WriteLine("Tray baseline private memory: " + (baseline / 1048576.0).ToString("F1") + " MiB");
                for (int pass = 0; pass < 3; pass++)
                {
                    AsrProcess resident = (AsrProcess)AsrBackend.Create(model, path);
                    SetField(app, "_asr", resident); // Controller owns cleanup, including on failure.
                    resident.Transcribe(new float[AudioCapture.SampleRate / 2], AudioCapture.SampleRate);
                    using (Process worker = Process.GetProcessById(resident.WorkerId))
                    using (SettingsForm preferences = new SettingsForm(settings))
                    {
                        Console.WriteLine("Cycle " + (pass + 1) + ": worker=" + resident.WorkerId + " private=" +
                            (worker.PrivateMemorySize64 / 1048576.0).ToString("F1") + " MiB; tray=" + (PrivateBytes() / 1048576.0).ToString("F1") + " MiB");
                        if (pass == 0) Invoke(app, "SetState", AppState.Idle, "Idle memory test");
                        else
                        {
                            // Exercise adoption on reload without opening a mic.
                            Invoke(app, "SetState", AppState.Waking, "Reload memory test");
                            SetField(app, "_wakeHeld", false); SetField(app, "_wakeCancelled", true);
                            Invoke(app, "FinishWake", resident, null);
                            if (Field<IAsrEngine>(app, "_asr") != resident) throw new Exception("Reload did not adopt its worker.");
                        }
                        if (pass == 1) Invoke(app, "SetState", AppState.Faulted, "Simulated input failure");
                        if (pass == 2) SetField(app, "_settingsForm", preferences);
                        if (app.TryUnloadIdleModel(TimeSpan.FromSeconds(59))) throw new Exception("Model unloaded before its timeout.");
                        // Deferred-start test contexts have no running timer.
                        // Trigger the real timer's unload entry point explicitly.
                        if (!app.TryUnloadIdleModel(TimeSpan.FromMinutes(1))) throw new Exception("Due model refused to unload.");
                        WaitForSleep(app);
                        SetField(app, "_settingsForm", null);
                        if (!worker.WaitForExit(5000)) throw new Exception("Unload left its CUDA worker alive.");
                        if (Field<IAsrEngine>(app, "_asr") != null) throw new Exception("Unload retained its recognizer.");
                        if (Field<HoldHotkey>(app, "_hotkey").IsHooked || Field<AudioCapture>(app, "_mic") != null)
                            throw new Exception("Paused memory test opened input resources.");
                        if (!File.Exists(path)) throw new Exception("Idle unload deleted cached model weights.");
                        long after = PrivateBytes();
                        Console.WriteLine("Cycle " + (pass + 1) + " unloaded: worker exited; tray private=" + (after / 1048576.0).ToString("F1") + " MiB");
                        if (after > baseline + 128L * 1024 * 1024) throw new Exception("Tray retained excessive memory after worker exit.");
                    }
                }
                // Switching models must retire the old worker before loading
                // another multi-GB runtime, not keep both resident for fallback.
                AsrProcess previousEngine = (AsrProcess)AsrBackend.Create(model, path);
                SetField(app, "_asr", previousEngine);
                Invoke(app, "SetState", AppState.Idle, "Replacement test");
                using (Process previousWorker = Process.GetProcessById(previousEngine.WorkerId))
                {
                    Invoke(app, "StartModelLoad", model, path, false);
                    if (Field<IAsrEngine>(app, "_asr") != null) throw new Exception("Model switch retained its old engine.");
                    DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                    while (Field<AppState>(app, "_state") == AppState.Loading && DateTime.UtcNow < deadline)
                    { Application.DoEvents(); Thread.Sleep(10); }
                    AsrProcess replacement = Field<IAsrEngine>(app, "_asr") as AsrProcess;
                    if (replacement == null || !previousWorker.WaitForExit(5000)) throw new Exception("Replacement did not retire its old worker.");
                    using (Process replacementWorker = Process.GetProcessById(replacement.WorkerId))
                    {
                        if (!app.TryUnloadIdleModel(TimeSpan.FromMinutes(1))) throw new Exception("Replacement refused idle unload.");
                        WaitForSleep(app);
                        if (!replacementWorker.WaitForExit(5000)) throw new Exception("Replacement worker leaked after unload.");
                    }
                }
                Console.WriteLine("PASS: model replacement retires its previous worker and can unload again.");
                VerifyOwnerCrash(model, path);
                using (Process current = Process.GetCurrentProcess())
                    foreach (ProcessModule module in current.Modules)
                        if (module.ModuleName.StartsWith("nemo_speech", StringComparison.OrdinalIgnoreCase) ||
                            module.ModuleName.StartsWith("ggml", StringComparison.OrdinalIgnoreCase) ||
                            module.ModuleName.StartsWith("cublas", StringComparison.OrdinalIgnoreCase))
                            throw new Exception("Native ASR/CUDA leaked into the tray process: " + module.ModuleName);
                Console.WriteLine("PASS: idle/error/settings-open unloading, three worker exits/reloads, bounded tray memory, no native ASR in tray.");
            }
            finally { Invoke(app, "Quit"); }
        }
        internal static int OwnerProbe(string[] args)
        {
            if (args.Length != 3) return 1;
            using (AsrProcess engine = (AsrProcess)AsrBackend.Create(ModelCatalog.Find(args[1]), Encoding.UTF8.GetString(Convert.FromBase64String(args[2]))))
            {
                Console.WriteLine("WORKER " + engine.WorkerId); Console.Out.Flush();
                Thread.Sleep(20000); // Safety bound if the outer test is interrupted.
            }
            return 0;
        }
        static void VerifyOwnerCrash(SpeechModel model, string path)
        {
            ProcessStartInfo start = new ProcessStartInfo(Assembly.GetEntryAssembly().Location,
                "--worker-owner-probe " + model.Id + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(path)));
            start.UseShellExecute = false; start.CreateNoWindow = true;
            start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            using (Process owner = Process.Start(start))
            {
                try
                {
                    System.Threading.Tasks.Task<string> line = owner.StandardOutput.ReadLineAsync();
                    if (!line.Wait(30000) || line.Result == null || !line.Result.StartsWith("WORKER "))
                        throw new Exception("Crash-lifetime probe could not create a speech worker.");
                    using (Process worker = Process.GetProcessById(int.Parse(line.Result.Substring(7))))
                    {
                        owner.Kill(); owner.WaitForExit(5000);
                        if (!worker.WaitForExit(10000)) throw new Exception("A crashed owner left an orphaned speech worker.");
                    }
                    Console.WriteLine("PASS: force-killing the owner also terminates its CUDA worker via the Windows job.");
                }
                finally { if (!owner.HasExited) { owner.Kill(); owner.WaitForExit(5000); } }
            }
        }
        internal static int Entry(string[] args)
        {
            string isolated = Path.Combine(Path.GetTempPath(), "ukti-runtime-" + Guid.NewGuid().ToString("N"));
            try
            {
                SpeechModel model = ModelCatalog.Find(args.Length > 0 ? args[0] : "nemotron-en");
                string path = ModelCatalog.PathFor(model, ModelCatalog.DirectoryPath);
                if (!ModelCatalog.IsInstalled(model, ModelCatalog.DirectoryPath))
                    throw new InvalidOperationException("Download " + model.Name + " through Ukti first. This test never downloads models.");
                string error = ModelCatalog.VerifyFile(path, model, delegate { return false; });
                if (error != null) throw new InvalidOperationException(error);
                AppData.TestRoot = isolated;
                TestIdleUnload(model, path);
                Console.WriteLine("PASS: checksum, CUDA worker load/unload/reload. No microphone or user preferences touched.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally { if (Directory.Exists(isolated)) Directory.Delete(isolated, true); }
        }
    }
}
