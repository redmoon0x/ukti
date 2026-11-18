using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ukti
{
    // Microphone capture through winmm's waveIn API: 16 kHz mono 16-bit PCM,
    // which is exactly what the ASR runtime wants, so no resampling is needed.
    public sealed class AudioCapture : IDisposable
    {
        const int WAVE_MAPPER = -1;
        const int CALLBACK_FUNCTION = 0x00030000;
        const int MM_WIM_DATA = 0x03C0;
        const uint WHDR_DONE = 0x00000001;
        const uint WHDR_PREPARED = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        delegate void WaveCallback(IntPtr hwi, uint msg, IntPtr instance, IntPtr param1, IntPtr param2);

        [DllImport("winmm.dll")]
        static extern int waveInGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        static extern int waveInGetDevCapsW(int device, ref WAVEINCAPS caps, int size);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WAVEINCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
        }

        [DllImport("winmm.dll")]
        static extern int waveInOpen(out IntPtr hwi, int device, ref WAVEFORMATEX format,
                                     WaveCallback callback, IntPtr instance, int flags);

        [DllImport("winmm.dll")]
        static extern int waveInPrepareHeader(IntPtr hwi, IntPtr header, int size);

        [DllImport("winmm.dll")]
        static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr header, int size);

        [DllImport("winmm.dll")]
        static extern int waveInAddBuffer(IntPtr hwi, IntPtr header, int size);

        [DllImport("winmm.dll")]
        static extern int waveInStart(IntPtr hwi);

        [DllImport("winmm.dll")]
        static extern int waveInStop(IntPtr hwi);

        [DllImport("winmm.dll")]
        static extern int waveInReset(IntPtr hwi);

        [DllImport("winmm.dll")]
        static extern int waveInClose(IntPtr hwi);

        public const int SampleRate = 16000;
        const int BufferCount = 8;
        const int BufferSamples = 1600;   // 100 ms per buffer
        const int MaxCaptureSeconds = 300;

        IntPtr _hwi = IntPtr.Zero;
        IntPtr[] _headers;
        IntPtr[] _buffers;
        WaveCallback _callback;           // held so the GC cannot collect it
        readonly List<float> _samples = new List<float>();
        readonly object _gate = new object();
        volatile bool _open;
        volatile bool _capturing;
        volatile float _level;

        // Smoothed input level (0..1), used to drive the on-screen meter.
        public float Level { get { return _level; } }

        public bool IsOpen { get { return _open; } }
        public bool IsCapturing { get { return _capturing; } }
        public bool Overflow { get; private set; }

        public static string DefaultDeviceName()
        {
            return DeviceName(0);
        }

        public static int DeviceCount()
        {
            try { return waveInGetNumDevs(); }
            catch { return 0; }
        }

        public static string DeviceName(int index)
        {
            try
            {
                if (waveInGetNumDevs() == 0) return "(no microphone)";
                WAVEINCAPS caps = new WAVEINCAPS();
                if (waveInGetDevCapsW(index, ref caps, Marshal.SizeOf(typeof(WAVEINCAPS))) == 0)
                    return caps.szPname.Trim();
            }
            catch { }
            return "(unknown)";
        }

        // -1 selects the system default. Stored by index because winmm identifies
        // capture devices this way.
        public int DeviceIndex = -1;

        public void Open()
        {
            if (_open) return;

            WAVEFORMATEX fmt = new WAVEFORMATEX();
            fmt.wFormatTag = 1;                                   // PCM
            fmt.nChannels = 1;
            fmt.nSamplesPerSec = SampleRate;
            fmt.wBitsPerSample = 16;
            fmt.nBlockAlign = (ushort)(fmt.nChannels * fmt.wBitsPerSample / 8);
            fmt.nAvgBytesPerSec = fmt.nSamplesPerSec * fmt.nBlockAlign;
            fmt.cbSize = 0;

            _callback = OnWaveMessage;

            int device = DeviceIndex < 0 ? WAVE_MAPPER : DeviceIndex;
            int err = waveInOpen(out _hwi, device, ref fmt, _callback, IntPtr.Zero, CALLBACK_FUNCTION);
            if (err != 0 || _hwi == IntPtr.Zero)
            {
                _hwi = IntPtr.Zero;
                throw new InvalidOperationException(
                    "Could not open the microphone (waveInOpen error " + err + "). " +
                    "Check that a microphone is connected and that microphone access is allowed in Windows privacy settings.");
            }

            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
            _headers = new IntPtr[BufferCount];
            _buffers = new IntPtr[BufferCount];
            for (int i = 0; i < BufferCount; i++)
            {
                _buffers[i] = Marshal.AllocHGlobal(BufferSamples * 2);
                _headers[i] = Marshal.AllocHGlobal(headerSize);

                WAVEHDR hdr = new WAVEHDR();
                hdr.lpData = _buffers[i];
                hdr.dwBufferLength = (uint)(BufferSamples * 2);
                hdr.dwFlags = 0;
                Marshal.StructureToPtr(hdr, _headers[i], false);

                waveInPrepareHeader(_hwi, _headers[i], headerSize);
                waveInAddBuffer(_hwi, _headers[i], headerSize);
            }

            _open = true;
            waveInStart(_hwi);
        }

        public void BeginCapture()
        {
            if (!_open) Open();
            lock (_gate) _samples.Clear();
            Overflow = false;
            _capturing = true;
        }

        // Waits briefly for buffers already in flight to land, then stops
        // accepting new audio. Without the drain the last fraction of a word is
        // lost whenever the user releases the key mid-syllable.
        public float[] EndCapture()
        {
            Thread.Sleep(DrainMs);
            _capturing = false;
            float[] result;
            lock (_gate)
            {
                result = _samples.ToArray();
                // Release the backing array too. Clearing alone keeps the
                // capacity alive, which holds on to a large buffer after every
                // long dictation.
                _samples.Clear();
                _samples.TrimExcess();
            }
            return result;
        }

        const int DrainMs = 60;

        void OnWaveMessage(IntPtr hwi, uint msg, IntPtr instance, IntPtr param1, IntPtr param2)
        {
            if (msg != MM_WIM_DATA) return;

            WAVEHDR hdr = (WAVEHDR)Marshal.PtrToStructure(param1, typeof(WAVEHDR));
            int bytes = (int)hdr.dwBytesRecorded;
            if (bytes > 0)
            {
                double sumSquares = 0;
                if (_capturing)
                {
                    lock (_gate)
                    {
                        // Hard cap keeps a forgotten session from eating memory.
                        if (_samples.Count < SampleRate * MaxCaptureSeconds)
                        {
                            for (int off = 0; off + 1 < bytes; off += 2)
                            {
                                short s = (short)(Marshal.ReadByte(hdr.lpData, off) |
                                                 (Marshal.ReadByte(hdr.lpData, off + 1) << 8));
                                float f = s / 32768f;
                                _samples.Add(f);
                                sumSquares += f * f;
                            }
                        }
                        else
                        {
                            Overflow = true;
                        }
                    }
                }
                else
                {
                    for (int off = 0; off + 1 < bytes; off += 2)
                    {
                        short s = (short)(Marshal.ReadByte(hdr.lpData, off) |
                                         (Marshal.ReadByte(hdr.lpData, off + 1) << 8));
                        double f = s / 32768f;
                        sumSquares += f * f;
                    }
                }

                int count = Math.Max(1, bytes / 2);
                float level = (float)Math.Sqrt(sumSquares / count);
                // Attack fast, release slowly so the meter feels responsive
                // without flickering on individual buffers.
                _level = level > _level ? level : _level * 0.72f + level * 0.28f;
            }

            if (!_open) return;

            // Recycle the buffer for another pass. WHDR_PREPARED must survive:
            // clearing it makes waveInAddBuffer reject the buffer, which would
            // silently stop capture after the first round of buffers.
            WAVEHDR again = (WAVEHDR)Marshal.PtrToStructure(param1, typeof(WAVEHDR));
            again.dwBytesRecorded = 0;
            again.dwFlags &= ~WHDR_DONE;
            again.dwFlags |= WHDR_PREPARED;
            Marshal.StructureToPtr(again, param1, false);
            waveInAddBuffer(_hwi, param1, Marshal.SizeOf(typeof(WAVEHDR)));
        }

        public void Close()
        {
            _capturing = false;
            _open = false;

            if (_hwi != IntPtr.Zero)
            {
                waveInStop(_hwi);
                waveInReset(_hwi);
                Thread.Sleep(30);
            }

            if (_hwi != IntPtr.Zero && _headers != null)
            {
                int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
                for (int i = 0; i < _headers.Length; i++)
                {
                    if (_headers[i] != IntPtr.Zero)
                    {
                        waveInUnprepareHeader(_hwi, _headers[i], headerSize);
                        Marshal.FreeHGlobal(_headers[i]);
                        _headers[i] = IntPtr.Zero;
                    }
                    if (_buffers[i] != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(_buffers[i]);
                        _buffers[i] = IntPtr.Zero;
                    }
                }
            }
            _headers = null;
            _buffers = null;

            if (_hwi != IntPtr.Zero)
            {
                waveInClose(_hwi);
                _hwi = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            Close();
        }
    }
}
