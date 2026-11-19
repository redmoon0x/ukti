using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Ukti
{
    public enum NemoStatus
    {
        Ok = 0,
        InvalidArgument = 1,
        OutOfMemory = 2,
        Runtime = 3,
        Cancelled = 4,
        ResourceExhausted = 5,
        Unavailable = 6,
    }

    public sealed class NemoStream : IDisposable
    {
        NemoStreamHandle _handle;
        bool _closed;

        internal NemoStream(NemoStreamHandle handle) { _handle = handle; }

        public void Push(float[] samples, int sampleRate)
        {
            if (_closed) return;
            if (samples == null || samples.Length == 0) return;
            IntPtr stream = _handle.DangerousGetHandle();
            int status = NemoAsr.nemo_speech_asr_stream_push_f32(
                stream, samples, (UIntPtr)(uint)samples.Length, sampleRate);
            if (status != 0)
            {
                throw new InvalidOperationException("Audio push failed: " + NemoAsr.Utf8(NemoAsr.nemo_speech_asr_last_error()));
            }
        }

        // Returns the transcript available now, or null when more audio is needed.
        public string Next(out bool isFinal)
        {
            isFinal = false;
            if (_closed) return null;
            IntPtr result;
            int status = NemoAsr.nemo_speech_asr_stream_next(_handle.DangerousGetHandle(), out result);
            if (status != 0)
            {
                throw new InvalidOperationException("Stream decode failed: " + NemoAsr.Utf8(NemoAsr.nemo_speech_asr_last_error()));
            }
            if (result == IntPtr.Zero) return null;
            try
            {
                isFinal = NemoAsr.nemo_speech_asr_result_is_final(result);
                uint alts = (uint)NemoAsr.nemo_speech_asr_result_alternative_count(result);
                if (alts == 0) return string.Empty;
                return NemoAsr.Utf8(NemoAsr.nemo_speech_asr_result_transcript(result, UIntPtr.Zero)) ?? string.Empty;
            }
            finally
            {
                NemoAsr.nemo_speech_asr_result_destroy(result);
            }
        }

        public void Finish()
        {
            if (_closed || _handle.IsInvalid) return;
            NemoAsr.nemo_speech_asr_stream_finish(_handle.DangerousGetHandle());
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            if (_handle != null) _handle.Dispose();
            _handle = null;
        }
    }

    public sealed class NemoAsr : IDisposable, IAsrEngine
    {
        const string Dll = "nemo_speech_asr_c.dll";

        [StructLayout(LayoutKind.Sequential)]
        struct BackendConfig
        {
            public IntPtr size;
            public int gpu;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ModelConfig
        {
            public IntPtr size;
            public IntPtr path;
            public IntPtr name;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct StreamingConfig
        {
            public IntPtr size;
            public float chunk_size;
            public float ctc_left_padding;
            public float ctc_right_padding;
            public int rnnt_right_context;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BatchingConfig
        {
            public IntPtr size;
            [MarshalAs(UnmanagedType.I1)] public bool enable;
            public int max_batch_size;
            public int max_queue_delay_us;
            public int max_queue_depth;
            public int ingress_cohort_delay_us;
            public int state_arena_slots;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RecognizerConfig
        {
            public IntPtr size;
            public IntPtr backend;
            public IntPtr model;
            public IntPtr streaming;
            public IntPtr decoder;
            public IntPtr vad;
            public IntPtr endpointing;
            public IntPtr postproc;
            public IntPtr diar;
            public IntPtr batching;
            public IntPtr salm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RecognitionOptions
        {
            public IntPtr size;
            public IntPtr request_id;
            public IntPtr language_code;
            [MarshalAs(UnmanagedType.I1)] public bool interim_results;
            [MarshalAs(UnmanagedType.I1)] public bool enable_word_time_offsets;
            [MarshalAs(UnmanagedType.I1)] public bool enable_automatic_punctuation;
            [MarshalAs(UnmanagedType.I1)] public bool verbatim_transcripts;
            [MarshalAs(UnmanagedType.I1)] public bool profanity_filter;
            public int stop_history_eou_ms;
            public IntPtr speech_contexts;
            public UIntPtr speech_context_count;
            public int max_alternatives;
            [MarshalAs(UnmanagedType.I1)] public bool enable_speaker_diarization;
            public int max_speaker_count;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int nemo_speech_asr_create(IntPtr cfg, out IntPtr recognizer);

        // nemo_speech_asr_destroy is called by NemoAsrHandle.ReleaseHandle.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int nemo_speech_asr_recognize_f32(
            IntPtr recognizer, IntPtr options, [In] float[] samples, UIntPtr nSamples,
            int sampleRate, out IntPtr result);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void nemo_speech_asr_result_destroy(IntPtr result);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr nemo_speech_asr_result_alternative_count(IntPtr result);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr nemo_speech_asr_result_transcript(IntPtr result, UIntPtr alt);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int nemo_speech_asr_streaming_recognize(
            IntPtr recognizer, IntPtr options, out IntPtr stream);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nemo_speech_asr_stream_push_f32(
            IntPtr stream, [In] float[] samples, UIntPtr nSamples, int sampleRate);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void nemo_speech_asr_stream_finish(IntPtr stream);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nemo_speech_asr_stream_next(IntPtr stream, out IntPtr result);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void nemo_speech_asr_stream_close(IntPtr stream);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr nemo_speech_asr_last_error();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr nemo_speech_asr_version();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool nemo_speech_asr_result_is_final(IntPtr result);

        NemoAsrHandle _recognizer;   // tests reflect the handle; field name preserved
        HGlobalHandle _optionsBuffer;
        HGlobalHandle _languageBuffer;
        bool _disposed;

        public string Version { get { return Utf8(nemo_speech_asr_version()); } }

        public static string Utf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            int len = 0;
            while (Marshal.ReadByte(p, len) != 0) len++;
            if (len == 0) return string.Empty;
            byte[] b = new byte[len];
            Marshal.Copy(p, b, 0, len);
            return Encoding.UTF8.GetString(b);
        }

        static string LastError()
        {
            string e = Utf8(nemo_speech_asr_last_error());
            return string.IsNullOrEmpty(e) ? "(no detail)" : e;
        }

        public static NemoAsr Create(string modelPath, int gpuIndex, float chunkSeconds, int rnntRightContext, string languageCode = "en-US")
        {
            NemoAsr asr = new NemoAsr();
            // SafeHandle allocation wrappers: every AllocHGlobal is owned by a
            // finalizable handle, so no exception path can leak native memory.
            HGlobalHandle backendMem, modelMem, streamingMem, batchingMem, cfgMem, pathMem;
            backendMem = modelMem = streamingMem = batchingMem = cfgMem = pathMem = null;
            try
            {
                BackendConfig backend = new BackendConfig();
                backend.size = (IntPtr)Marshal.SizeOf(typeof(BackendConfig));
                backend.gpu = gpuIndex;
                backendMem = new HGlobalHandle(Marshal.AllocHGlobal(backend.size));
                Marshal.StructureToPtr(backend, backendMem.DangerousGetHandle(), false);

                ModelConfig model = new ModelConfig();
                model.size = (IntPtr)Marshal.SizeOf(typeof(ModelConfig));

                pathMem = Utf8Z(modelPath);
                model.path = pathMem.DangerousGetHandle();
                model.name = IntPtr.Zero;
                modelMem = new HGlobalHandle(Marshal.AllocHGlobal(model.size));
                Marshal.StructureToPtr(model, modelMem.DangerousGetHandle(), false);

                StreamingConfig streaming = new StreamingConfig();
                streaming.size = (IntPtr)Marshal.SizeOf(typeof(StreamingConfig));
                streaming.chunk_size = chunkSeconds;
                streaming.ctc_left_padding = 1.92f;
                streaming.ctc_right_padding = 1.92f;
                streaming.rnnt_right_context = rnntRightContext;
                streamingMem = new HGlobalHandle(Marshal.AllocHGlobal(streaming.size));
                Marshal.StructureToPtr(streaming, streamingMem.DangerousGetHandle(), false);

                RecognizerConfig cfg = new RecognizerConfig();
                cfg.size = (IntPtr)Marshal.SizeOf(typeof(RecognizerConfig));
                cfg.backend = backendMem.DangerousGetHandle();
                cfg.model = modelMem.DangerousGetHandle();
                cfg.streaming = streamingMem.DangerousGetHandle();

                // Dynamic batching exists to combine concurrent requests on one
                // recogniser. This app decodes one utterance at a time, so it
                // buys nothing and reserves a state arena sized for many more
                // streams than will ever exist - several gigabytes of address
                // space that the process would otherwise never touch.
                BatchingConfig batching = new BatchingConfig();
                batching.size = (IntPtr)Marshal.SizeOf(typeof(BatchingConfig));
                batching.enable = false;
                batching.max_batch_size = 1;
                batching.max_queue_delay_us = 0;
                batching.max_queue_depth = 1;
                batching.state_arena_slots = 1;
                batchingMem = new HGlobalHandle(Marshal.AllocHGlobal(batching.size));
                Marshal.StructureToPtr(batching, batchingMem.DangerousGetHandle(), false);
                cfg.batching = batchingMem.DangerousGetHandle();

                cfgMem = new HGlobalHandle(Marshal.AllocHGlobal(cfg.size));
                Marshal.StructureToPtr(cfg, cfgMem.DangerousGetHandle(), false);

                IntPtr rec;
                int status = nemo_speech_asr_create(cfgMem.DangerousGetHandle(), out rec);
                if (status != 0 || rec == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "ASR initialisation failed (status " + status + "): " + LastError());
                }
                asr._recognizer = new NemoAsrHandle(rec);
                asr._optionsBuffer = asr.BuildOptions(languageCode);
                return asr;
            }
            catch
            {
                asr.Dispose();
                throw;
            }
            finally
            {
                if (cfgMem != null) cfgMem.Dispose();
                if (batchingMem != null) batchingMem.Dispose();
                if (streamingMem != null) streamingMem.Dispose();
                if (modelMem != null) modelMem.Dispose();
                if (backendMem != null) backendMem.Dispose();
                if (pathMem != null) pathMem.Dispose();
            }
        }

        static HGlobalHandle Utf8Z(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            HGlobalHandle mem = new HGlobalHandle(Marshal.AllocHGlobal(bytes.Length + 1));
            Marshal.Copy(bytes, 0, mem.DangerousGetHandle(), bytes.Length);
            Marshal.WriteByte(mem.DangerousGetHandle(), bytes.Length, 0);
            return mem;
        }

        HGlobalHandle BuildOptions(string languageCode)
        {
            RecognitionOptions opt = new RecognitionOptions();
            opt.size = (IntPtr)Marshal.SizeOf(typeof(RecognitionOptions));
            opt.request_id = IntPtr.Zero;

            _languageBuffer = string.IsNullOrEmpty(languageCode) ? null : Utf8Z(languageCode);
            opt.interim_results = false;
            opt.enable_word_time_offsets = false;
            opt.enable_automatic_punctuation = true;
            opt.verbatim_transcripts = false;
            opt.profanity_filter = false;
            opt.stop_history_eou_ms = 0;
            opt.speech_contexts = IntPtr.Zero;
            opt.speech_context_count = UIntPtr.Zero;
            opt.max_alternatives = 1;
            opt.enable_speaker_diarization = false;
            opt.max_speaker_count = 0;

            HGlobalHandle mem = new HGlobalHandle(Marshal.AllocHGlobal(opt.size));
            Marshal.StructureToPtr(opt, mem.DangerousGetHandle(), false);
            return mem;
        }

        public string Transcribe(float[] samples, int sampleRate)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            if (samples.Length == 0) return string.Empty;
            if (_recognizer == null || _recognizer.IsInvalid) throw new ObjectDisposedException("NemoAsr");

            IntPtr result;
            int status = nemo_speech_asr_recognize_f32(
                _recognizer.DangerousGetHandle(), _optionsBuffer.DangerousGetHandle(), samples,
                (UIntPtr)(uint)samples.Length, sampleRate, out result);
            if (status != 0)
            {
                throw new InvalidOperationException(
                    "Transcription failed (status " + status + "): " + LastError());
            }
            if (result == IntPtr.Zero) return string.Empty;
            try
            {
                uint alts = (uint)nemo_speech_asr_result_alternative_count(result);
                if (alts == 0) return string.Empty;
                return Utf8(nemo_speech_asr_result_transcript(result, UIntPtr.Zero)) ?? string.Empty;
            }
            finally
            {
                nemo_speech_asr_result_destroy(result);
            }
        }

        public NemoStream StartStream()
        {
            if (_recognizer == null || _recognizer.IsInvalid) throw new ObjectDisposedException("NemoAsr");
            IntPtr stream;
            int status = nemo_speech_asr_streaming_recognize(
                _recognizer.DangerousGetHandle(), _optionsBuffer.DangerousGetHandle(), out stream);
            if (status != 0 || stream == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Could not start audio stream (status " + status + "): " + LastError());
            }
            return new NemoStream(new NemoStreamHandle(stream));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_recognizer != null) { _recognizer.Dispose(); _recognizer = null; }
            if (_optionsBuffer != null) { _optionsBuffer.Dispose(); _optionsBuffer = null; }
            if (_languageBuffer != null) { _languageBuffer.Dispose(); _languageBuffer = null; }
        }
    }
}
