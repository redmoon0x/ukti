using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;


namespace Ukti
{
    // WASAPI shared-mode capture in the selected endpoint's native mix format.
    // Decode complete interleaved frames, downmix, then resample to 16 kHz mono
    // for ASR. AudioPipeline.Prepare handles high-pass/normalize/trim.
    public sealed class AudioCapture : IDisposable, IAudioCapture
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateEvent(IntPtr security, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetEvent(IntPtr handle);

        public const int SampleRate = 16000;
        const int MaxCaptureSeconds = 300;
        const int DrainMs = 60;
        const long HundredNanosecondsPerSecond = 10000000L;


        Wasapi.IAudioClient _client;
        Wasapi.IAudioCaptureClient _capture;
        IntPtr _event = IntPtr.Zero;
        Thread _pump;
        volatile bool _pumpRunning;
        readonly List<float> _samples = new List<float>();
        readonly object _gate = new object();
        volatile bool _open;
        volatile bool _capturing;
        AudioPacketConverter _converter;
        int _sourceRate = SampleRate, _blockAlign, _bufferFrames;
        internal string EndpointId { get; private set; }
        volatile float _level;

        public float Level { get { return _level; } }
        int IAudioCapture.SampleRate { get { return SampleRate; } }
        public bool IsOpen { get { return _open; } }
        public bool IsCapturing { get { return _capturing; } }
        public bool Overflow { get; private set; }
        int _deviceIndex = -1;
        public int DeviceIndex { get { return _deviceIndex; } set { _deviceIndex = value; } }

        public static string DefaultDeviceName() { return DeviceName(0); }

        public static int DeviceCount()
        {
            // WASAPI enumerates capture endpoints; MMDeviceEnumerator is created
            // briefly here so the settings UI shows the same device list.
            try
            {
                Wasapi.IMMDeviceEnumerator enumerator = (Wasapi.IMMDeviceEnumerator)new Wasapi.MMDeviceEnumerator();
                IntPtr collection;
                int hr = enumerator.EnumAudioEndpoints(Wasapi.EDataFlow.eCapture, 0x1 /* ACTIVE */, out collection);
                if (hr < 0) return 0;
                uint count = 0;
                if (collection != IntPtr.Zero)
                {
                    // IMMDeviceCollection::GetCount is the third method after
                    // QueryInterface/AddRef/Release in the vtable.
                    IntPtr vtable = Marshal.ReadIntPtr(collection);
                    GetCountDelegate getCount = (GetCountDelegate)Marshal.GetDelegateForFunctionPointer(
                        Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size), typeof(GetCountDelegate));
                    getCount(collection, out count);
                    Marshal.Release(collection);
                }
                Marshal.ReleaseComObject(enumerator);
                return (int)count;
            }
            catch { return 0; }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int GetCountDelegate(IntPtr self, out uint count);

        public static string DeviceName(int index)
        {
            // Names come from the endpoint's friendly-name property. Returning a
            // stable descriptive label is enough for the settings combo; the
            // actual selection is by enumeration index.
            int count = DeviceCount();
            if (count == 0) return "(no microphone)";
            if (index < 0 || index >= count) return "(default)";
            try
            {
                Wasapi.IMMDeviceEnumerator enumerator = (Wasapi.IMMDeviceEnumerator)new Wasapi.MMDeviceEnumerator();
                IntPtr collection;
                if (enumerator.EnumAudioEndpoints(Wasapi.EDataFlow.eCapture, 0x1, out collection) < 0)
                { Marshal.ReleaseComObject(enumerator); return "(unknown)"; }
                IntPtr vtable = Marshal.ReadIntPtr(collection);
                ItemDelegate item = (ItemDelegate)Marshal.GetDelegateForFunctionPointer(
                    Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size), typeof(ItemDelegate));
                IntPtr devicePtr;
                item(collection, (uint)index, out devicePtr);
                Marshal.Release(collection);
                Marshal.ReleaseComObject(enumerator);
                if (devicePtr != IntPtr.Zero)
                {
                    Wasapi.IMMDevice device = (Wasapi.IMMDevice)Marshal.GetObjectForIUnknown(devicePtr);
                    Marshal.Release(devicePtr);
                    try
                    {
                        string name = FriendlyName(device);
                        if (!string.IsNullOrEmpty(name)) return name;
                        string id;
                        device.GetId(out id);
                        if (!string.IsNullOrEmpty(id)) return id;
                    }
                    finally { Marshal.ReleaseComObject(device); }
                }
            }
            catch { }
            return "(microphone " + index + ")";
        }

        static string FriendlyName(Wasapi.IMMDevice device)
        {
            IntPtr storePtr;
            if (device.OpenPropertyStore(0 /* STGM_READ */, out storePtr) < 0 || storePtr == IntPtr.Zero) return null;
            Wasapi.IPropertyStore store = (Wasapi.IPropertyStore)Marshal.GetObjectForIUnknown(storePtr);
            Marshal.Release(storePtr);
            try
            {
                Wasapi.PROPERTYKEY key = Wasapi.DeviceFriendlyName;
                Wasapi.PROPVARIANT value;
                if (store.GetValue(ref key, out value) < 0) return null;
                try
                {
                    return value.vt == 31 /* VT_LPWSTR */ && value.pointer != IntPtr.Zero
                        ? Marshal.PtrToStringUni(value.pointer) : null;
                }
                finally { Wasapi.PropVariantClear(ref value); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int ItemDelegate(IntPtr self, uint index, out IntPtr device);

        public void Open()
        {
            if (_open) return;
            Wasapi.IMMDeviceEnumerator enumerator = (Wasapi.IMMDeviceEnumerator)new Wasapi.MMDeviceEnumerator();
            Wasapi.IMMDevice device = null;
            try
            {
                int hr = DeviceIndex < 0
                    ? enumerator.GetDefaultAudioEndpoint(Wasapi.EDataFlow.eCapture, Wasapi.ERole.eMultimedia, out device)
                    : GetDeviceByIndex(enumerator, DeviceIndex, out device);
                Check(hr, "find the selected microphone");
                if (device == null) throw new InvalidOperationException("The selected microphone is unavailable.");
                string endpointId;
                Check(device.GetId(out endpointId), "identify the microphone");
                EndpointId = endpointId;
                object clientObj;
                Guid iid = Wasapi.IID_IAudioClient;
                Check(device.Activate(ref iid, 0x1, IntPtr.Zero, out clientObj), "activate the microphone");
                _client = (Wasapi.IAudioClient)clientObj;

                IntPtr mixPtr;
                Check(_client.GetMixFormat(out mixPtr), "read the microphone format");
                if (mixPtr == IntPtr.Zero) throw new InvalidOperationException("The microphone returned no audio format.");
                try
                {
                    Wasapi.WAVEFORMATEX mix = (Wasapi.WAVEFORMATEX)Marshal.PtrToStructure(mixPtr, typeof(Wasapi.WAVEFORMATEX));
                    Guid subtype = mix.wFormatTag == 0xFFFE && mix.cbSize >= 22
                        ? (Guid)Marshal.PtrToStructure(IntPtr.Add(mixPtr, 24), typeof(Guid)) : Guid.Empty;
                    bool floatingPoint = mix.wFormatTag == 3 || subtype == new Guid("00000003-0000-0010-8000-00aa00389b71");
                    bool pcm = mix.wFormatTag == 1 || subtype == new Guid("00000001-0000-0010-8000-00aa00389b71");
                    if (!floatingPoint && !pcm) throw new InvalidOperationException("Unsupported microphone audio encoding.");
                    _sourceRate = (int)mix.nSamplesPerSec;
                    _blockAlign = mix.nBlockAlign;
                    _converter = new AudioPacketConverter(_sourceRate, mix.nChannels, mix.wBitsPerSample, _blockAlign, floatingPoint);
                    Check(_client.Initialize(Wasapi.AudioClientShareMode.Shared, Wasapi.AudioClientStreamFlags.EventCallback,
                        HundredNanosecondsPerSecond / 2, 0, mixPtr, IntPtr.Zero), "initialise the microphone");
                    AppLogger.Info("microphone opened: index=" + DeviceIndex + " endpoint=" + EndpointId +
                        " rate=" + _sourceRate + " channels=" + mix.nChannels + " bits=" + mix.wBitsPerSample +
                        " float=" + floatingPoint);
                }
                finally { Marshal.FreeCoTaskMem(mixPtr); }

                uint bufferFrames;
                Check(_client.GetBufferSize(out bufferFrames), "read the microphone buffer size");
                _bufferFrames = checked((int)bufferFrames);
                _event = CreateEvent(IntPtr.Zero, false, false, null);
                if (_event == IntPtr.Zero) throw new InvalidOperationException("Could not create the capture event.");
                Check(_client.SetEventHandle(_event), "register the capture event");
                object captureObj;
                Guid captureIid = Wasapi.IID_IAudioCaptureClient;
                Check(_client.GetService(ref captureIid, out captureObj), "open the capture client");
                _capture = (Wasapi.IAudioCaptureClient)captureObj;
                Check(_client.Start(), "start the microphone");
                _open = true;
                _pumpRunning = true;
                _pump = new Thread(Pump) { IsBackground = true, Name = "Ukti.Microphone" };
                _pump.Start();
            }
            catch { Close(); throw; }
            finally
            {
                if (device != null) Marshal.ReleaseComObject(device);
                Marshal.ReleaseComObject(enumerator);
            }
        }

        static void Check(int hr, string operation)
        {
            if (hr < 0) throw new InvalidOperationException("Could not " + operation + " (WASAPI error 0x" + hr.ToString("X8") + ").");
        }

        static int GetDeviceByIndex(Wasapi.IMMDeviceEnumerator enumerator, int index, out Wasapi.IMMDevice device)
        {
            device = null;
            IntPtr collection;
            int hr = enumerator.EnumAudioEndpoints(Wasapi.EDataFlow.eCapture, 0x1, out collection);
            if (hr < 0) return hr;
            IntPtr vtable = Marshal.ReadIntPtr(collection);
            ItemDelegate item = (ItemDelegate)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size), typeof(ItemDelegate));
            IntPtr devicePtr;
            hr = item(collection, (uint)index, out devicePtr);
            Marshal.Release(collection);
            if (hr < 0) return hr;
            device = (Wasapi.IMMDevice)Marshal.GetObjectForIUnknown(devicePtr);
            Marshal.Release(devicePtr);
            return 0;
        }

        // Event-driven pump: read frames * blockAlign bytes, never frames ints.
        void Pump()
        {
            byte[] raw = ArrayPool<byte>.Shared.Rent(checked(_bufferFrames * _blockAlign));
            float[] mono = ArrayPool<float>.Shared.Rent(checked((int)Math.Ceiling((double)_bufferFrames * SampleRate / _sourceRate) + 4));
            try
            {
                while (_pumpRunning)
                {
                    if (WaitForSingleObject(_event, 1000) != 0 || !_pumpRunning) continue;
                    while (_pumpRunning)
                    {
                        uint packet;
                        Check(_capture.GetNextPacketSize(out packet), "read the microphone packet size");
                        if (packet == 0) break;
                        IntPtr data; uint frames, flags; ulong position, qpc;
                        int result = _capture.GetBuffer(out data, out frames, out flags, out position, out qpc);
                        Check(result, "read microphone audio");
                        if (result != 0) break; // AUDCLNT_S_BUFFER_EMPTY
                        try
                        {
                            int count = checked((int)frames);
                            int bytes = checked(count * _blockAlign);
                            if (bytes > raw.Length) throw new InvalidOperationException("Microphone packet exceeds the capture buffer.");
                            bool silent = (flags & 0x2) != 0 || data == IntPtr.Zero;
                            if (!silent) Marshal.Copy(data, raw, 0, bytes);
                            int outCount = _converter.Convert(silent ? null : raw, count, mono);
                            double sumSquares = 0;
                            for (int i = 0; i < outCount; i++) sumSquares += mono[i] * mono[i];
                            lock (_gate)
                            {
                                if (_capturing)
                                    for (int i = 0; i < outCount; i++)
                                        if (_samples.Count < SampleRate * MaxCaptureSeconds) _samples.Add(mono[i]);
                                        else Overflow = true;
                            }
                            float level = (float)Math.Sqrt(sumSquares / Math.Max(1, outCount));
                            _level = level > _level ? level : _level * 0.72f + level * 0.28f;
                        }
                        finally { Check(_capture.ReleaseBuffer(frames), "release the microphone packet"); }
                    }
                }
            }
            catch (Exception ex)
            {
                _pumpRunning = false;
                _level = 0;
                TrayApp.Log("audio pump stopped: " + ex.Message);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
                ArrayPool<float>.Shared.Return(mono);
            }
        }

        public void BeginCapture()
        {
            if (!_open) Open();
            if (!_pumpRunning) throw new InvalidOperationException("The microphone stopped responding. Select it again in Settings.");
            lock (_gate) { _samples.Clear(); Overflow = false; _capturing = true; }
        }

        public float[] EndCapture()
        {
            // Let in-flight packets land before stopping acceptance.
            Thread.Sleep(DrainMs);
            lock (_gate)
            {
                _capturing = false;
                float[] result = _samples.ToArray();
                _samples.Clear();
                _samples.TrimExcess();
                return result;
            }
        }

        public void Close()
        {
            _capturing = false;
            _pumpRunning = false;
            if (_event != IntPtr.Zero) SetEvent(_event);
            if (_pump != null && _pump != Thread.CurrentThread) _pump.Join();
            _pump = null;
            if (_client != null) { try { _client.Stop(); } catch { } Marshal.ReleaseComObject(_client); _client = null; }
            if (_capture != null) { Marshal.ReleaseComObject(_capture); _capture = null; }

            if (_event != IntPtr.Zero) { CloseHandle(_event); _event = IntPtr.Zero; }
            _open = false;
            _level = 0;
            _converter = null;
        }

        public void Dispose() { Close(); }
    }
}
