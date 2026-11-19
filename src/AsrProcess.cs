using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Ukti
{
    // Native ASR/CUDA never loads into the tray process. Closing this engine
    // terminates its private worker, releasing driver caches as well as weights.
    sealed class AsrProcess : IAsrEngine
    {
        readonly object _gate = new object();
        NamedPipeServerStream _pipe;
        Process _process;
        SafeFileHandle _job;
        BinaryReader _reader;
        BinaryWriter _writer;
        int _disposed;
        public string Version { get; private set; }
        internal int WorkerId { get; private set; }

        internal AsrProcess(SpeechModel model, string path, CancellationToken cancel)
        {
            try
            {
                cancel.ThrowIfCancellationRequested();
                string name = "Ukti.Asr." + Guid.NewGuid().ToString("N");
                PipeSecurity access = new PipeSecurity();
                access.SetAccessRuleProtection(true, false);
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    access.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
                _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 65536, 65536, access);
                _job = CreateJobObject(IntPtr.Zero, null);
                if (_job.IsInvalid) throw new InvalidOperationException("Could not create the speech worker job.");
                JobLimits limits = new JobLimits();
                limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf(typeof(JobLimits))))
                    throw new InvalidOperationException("Could not protect the speech worker lifetime.");
                ProcessStartInfo start = new ProcessStartInfo(Assembly.GetEntryAssembly().Location,
                    "--asr-worker " + name + " " + model.Id + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(path)));
                start.UseShellExecute = false; start.CreateNoWindow = true;
                start.RedirectStandardOutput = true; start.RedirectStandardError = true;
                _process = Process.Start(start);
                WorkerId = _process.Id;
                if (!AssignProcessToJobObject(_job, _process.Handle))
                    throw new InvalidOperationException("Could not attach the speech worker to its owner.");
                // Drain native diagnostics independently from the binary pipe.
                _process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                { if (!string.IsNullOrEmpty(e.Data)) AppLogger.Debug("ASR worker: " + e.Data); };
                _process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                { if (!string.IsNullOrEmpty(e.Data)) AppLogger.Debug("ASR worker: " + e.Data); };
                _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
                using (cancel.Register(Dispose))
                {
                    IAsyncResult connection = _pipe.BeginWaitForConnection(null, null);
                    using (WaitHandle wait = connection.AsyncWaitHandle)
                    {
                        Stopwatch timeout = Stopwatch.StartNew();
                        while (!wait.WaitOne(100))
                        {
                            cancel.ThrowIfCancellationRequested();
                            if (_process.HasExited) throw new InvalidOperationException("The speech worker exited before connecting.");
                            if (timeout.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("The speech worker did not connect.");
                        }
                        _pipe.EndWaitForConnection(connection);
                    }
                    _reader = new BinaryReader(_pipe, Encoding.UTF8, true);
                    _writer = new BinaryWriter(_pipe, Encoding.UTF8, true);
                    // A connected child can still fail during model loading.
                    // Cancellation closes the pipe and kills that child too.
                    Version = ReadResponse();
                    cancel.ThrowIfCancellationRequested();
                }
            }
            catch
            {
                Dispose();
                cancel.ThrowIfCancellationRequested();
                throw;
            }
        }

        string ReadResponse()
        {
            byte status = _reader.ReadByte();
            string text = _reader.ReadString();
            if (status != 0) throw new InvalidOperationException(text);
            return text;
        }
        public string Transcribe(float[] samples, int sampleRate)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            if (sampleRate != AudioCapture.SampleRate || samples.Length > AudioCapture.SampleRate * 300)
                throw new ArgumentException("Speech audio must be 16 kHz mono and at most five minutes long.");
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException("AsrProcess");
                _writer.Write((byte)1); _writer.Write(sampleRate); _writer.Write(samples.Length);
                TransferSamples(_writer, samples);
                _writer.Flush();
                return ReadResponse();
            }
        }
        internal static void TransferSamples(BinaryWriter writer, float[] samples)
        {
            byte[] bytes = new byte[65536];
            int total = checked(samples.Length * sizeof(float));
            for (int offset = 0; offset < total;)
            {
                int count = Math.Min(bytes.Length, total - offset);
                Buffer.BlockCopy(samples, offset, bytes, 0, count);
                writer.Write(bytes, 0, count); offset += count;
            }
        }
        internal static float[] ReceiveSamples(BinaryReader reader, int count)
        {
            if (count < 0 || count > AudioCapture.SampleRate * 300) throw new InvalidDataException("Invalid speech packet length.");
            float[] samples = new float[count];
            byte[] bytes = new byte[65536];
            int total = checked(count * sizeof(float));
            for (int offset = 0; offset < total;)
            {
                int length = Math.Min(bytes.Length, total - offset), read = 0;
                while (read < length)
                {
                    int n = reader.Read(bytes, read, length - read);
                    if (n == 0) throw new EndOfStreamException();
                    read += n;
                }
                Buffer.BlockCopy(bytes, 0, samples, offset, length); offset += length;
            }
            return samples;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // Do not take _gate: killing a hung inference must unblock its read.
            if (_job != null) { _job.Dispose(); _job = null; }
            if (_process != null)
            {
                try
                {
                    if (!_process.HasExited) _process.Kill();
                    if (!_process.WaitForExit(5000)) AppLogger.Warning("Speech worker did not exit promptly.");
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception ex) { AppLogger.Warning("Speech worker cleanup: " + ex.Message); }
                finally { _process.Dispose(); _process = null; }
            }
            if (_pipe != null) { _pipe.Dispose(); _pipe = null; }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint LimitFlags;
            public UIntPtr MinWorkingSet, MaxWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        struct JobLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateJobObject(IntPtr security, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobLimits limits, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    }

    static class AsrWorker
    {
        internal static bool IsWorker(string[] args) { return args.Length > 0 && args[0] == "--asr-worker"; }
        internal static int Run(string[] args)
        {
            if (args.Length != 4) return 1;
            try
            {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut))
                {
                    pipe.Connect(10000);
                    using (BinaryReader reader = new BinaryReader(pipe, Encoding.UTF8, true))
                    using (BinaryWriter writer = new BinaryWriter(pipe, Encoding.UTF8, true))
                    {
                        IAsrEngine engine;
                        try
                        {
                            engine = AsrBackend.CreateInProcess(ModelCatalog.Find(args[2]), Encoding.UTF8.GetString(Convert.FromBase64String(args[3])));
                            writer.Write((byte)0); writer.Write(engine.Version); writer.Flush();
                        }
                        catch (Exception ex) { writer.Write((byte)1); writer.Write(ex.Message); writer.Flush(); return 1; }
                        using (engine)
                        {
                            while (reader.ReadByte() == 1)
                            {
                                int rate = reader.ReadInt32();
                                float[] samples = AsrProcess.ReceiveSamples(reader, reader.ReadInt32());
                                try
                                {
                                    if (rate != AudioCapture.SampleRate) throw new InvalidDataException("Invalid speech sample rate.");
                                    string text = StreamingPrototype.Transcribe(engine, samples, rate, 2) ?? engine.Transcribe(samples, rate);
                                    writer.Write((byte)0); writer.Write(text ?? "");
                                }
                                catch (Exception ex) { writer.Write((byte)1); writer.Write(ex.Message); }
                                writer.Flush();
                            }
                        }
                    }
                }
                return 0;
            }
            catch (IOException) { return 0; } // Parent exited or unloaded the model.
            catch (Exception ex) { AppLogger.Error("Speech worker failed: " + ex.Message); return 1; }
        }
    }
}
