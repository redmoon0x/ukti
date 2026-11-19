using System;
using System.Runtime.InteropServices;

namespace Ukti
{
    // SafeHandle wrappers turn raw IntPtr ownership into a runtime-enforced
    // lifetime: a critical finalizer releases the native recognizer/stream even
    // on an exception path, and DangerousAddRef prevents the handle being
    // disposed while a native call is in flight.
    sealed class NemoAsrHandle : SafeHandle
    {
        [DllImport("nemo_speech_asr_c.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern void nemo_speech_asr_destroy(IntPtr recognizer);

        public NemoAsrHandle() : base(IntPtr.Zero, true) { }
        internal NemoAsrHandle(IntPtr handle) : base(IntPtr.Zero, true) { SetHandle(handle); }
        public override bool IsInvalid { get { return handle == IntPtr.Zero; } }
        protected override bool ReleaseHandle()
        {
            if (handle != IntPtr.Zero) nemo_speech_asr_destroy(handle);
            SetHandle(IntPtr.Zero);
            return true;
        }
    }

    sealed class NemoStreamHandle : SafeHandle
    {
        [DllImport("nemo_speech_asr_c.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern void nemo_speech_asr_stream_close(IntPtr stream);

        public NemoStreamHandle() : base(IntPtr.Zero, true) { }
        internal NemoStreamHandle(IntPtr handle) : base(IntPtr.Zero, true) { SetHandle(handle); }
        public override bool IsInvalid { get { return handle == IntPtr.Zero; } }
        protected override bool ReleaseHandle()
        {
            if (handle != IntPtr.Zero) nemo_speech_asr_stream_close(handle);
            SetHandle(IntPtr.Zero);
            return true;
        }
    }

    // Generic unmanaged-memory owner for the AllocHGlobal config buffers.
    sealed class HGlobalHandle : SafeHandle
    {
        public HGlobalHandle() : base(IntPtr.Zero, true) { }
        internal HGlobalHandle(IntPtr handle) : base(IntPtr.Zero, true) { SetHandle(handle); }
        public override bool IsInvalid { get { return handle == IntPtr.Zero; } }
        protected override bool ReleaseHandle()
        {
            if (handle != IntPtr.Zero) Marshal.FreeHGlobal(handle);
            SetHandle(IntPtr.Zero);
            return true;
        }
    }

    // waveIn device handle. Creation/destruction is done by AudioCapture; this
    // class only guarantees the close happens exactly once with a finalizer.
    sealed class WaveInHandle : SafeHandle
    {
        [DllImport("winmm.dll")]
        static extern int waveInClose(IntPtr hwi);

        public WaveInHandle() : base(IntPtr.Zero, true) { }
        internal WaveInHandle(IntPtr handle) : base(IntPtr.Zero, true) { SetHandle(handle); }
        public override bool IsInvalid { get { return handle == IntPtr.Zero || handle == new IntPtr(-1); } }
        protected override bool ReleaseHandle()
        {
            if (!IsInvalid) waveInClose(handle);
            SetHandle(IntPtr.Zero);
            return true;
        }
    }
}
