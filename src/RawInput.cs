using System;
using System.Runtime.InteropServices;

namespace Ukti
{
    // Raw Input with RIDEV_INPUTSINK: receives keyboard events even when the
    // window is not foreground, without the LowLevelHooksTimeout/silent-removal
    // failure mode of WH_KEYBOARD_LL. Events are delivered as WM_INPUT to the
    // hidden dispatcher window owned by the UI thread.
    sealed class RawInputKeyboard : IDisposable
    {
        const uint RIDEV_INPUTSINK = 0x00000100;
        const uint RIDEV_REMOVE = 0x00000001;
        const uint RIM_TYPEKEYBOARD = 1;
        const uint RID_INPUT = 0x10000003;

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint count, uint size);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData,
            ref uint pcbSize, uint cbSizeHeader);

        readonly IntPtr _window;
        bool _registered;
        int _failures;

        public RawInputKeyboard(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero) throw new ArgumentException("A window handle is required.", "windowHandle");
            _window = windowHandle;
        }

        public bool Register()
        {
            if (_registered) return true;
            RAWINPUTDEVICE[] device = new RAWINPUTDEVICE[1];
            device[0].usUsagePage = 1; // Generic Desktop Controls
            device[0].usUsage = 6;     // Keyboard
            device[0].dwFlags = RIDEV_INPUTSINK;
            device[0].hwndTarget = _window;
            _registered = RegisterRawInputDevices(device, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            return _registered;
        }

        public void Unregister()
        {
            if (!_registered) return;
            RAWINPUTDEVICE[] device = new RAWINPUTDEVICE[1];
            device[0].usUsagePage = 1;
            device[0].usUsage = 6;
            device[0].dwFlags = RIDEV_REMOVE;
            device[0].hwndTarget = IntPtr.Zero;
            RegisterRawInputDevices(device, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            _registered = false;
        }

        // RAWKEYBOARD reports generic Ctrl/Alt/Shift virtual keys. Match the
        // sided GetAsyncKeyState snapshot used when the listener is installed.
        internal static ushort NormalizeVirtualKey(ushort virtualKey, ushort makeCode, ushort flags)
        {
            if (virtualKey == 0x11) return (ushort)((flags & 0x02) != 0 ? 0xA3 : 0xA2); // Ctrl, E0 = right
            if (virtualKey == 0x12) return (ushort)((flags & 0x02) != 0 ? 0xA5 : 0xA4); // Alt
            if (virtualKey == 0x10) return (ushort)(makeCode == 0x36 ? 0xA1 : 0xA0);    // Shift
            return virtualKey;
        }

        public bool TryRead(IntPtr lParam, out ushort virtualKey, out bool down)
        {
            virtualKey = 0; down = false;
            if (lParam == IntPtr.Zero) return false;

            int headerSize = Marshal.SizeOf(typeof(RAWINPUTHEADER));
            uint size = 0;
            // Query the exact byte count Windows needs; RAWINPUT's union can be
            // larger than RAWKEYBOARD (RAWMOUSE/RAWHID are bigger on x64).
            if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, (uint)headerSize) == unchecked((uint)-1)
                || size < headerSize + (uint)Marshal.SizeOf(typeof(RAWKEYBOARD)))
                return false;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, (uint)headerSize) != size)
                { LogFailure("size mismatch"); return false; }
                RAWINPUTHEADER header = (RAWINPUTHEADER)Marshal.PtrToStructure(buffer, typeof(RAWINPUTHEADER));
                if (header.dwType != RIM_TYPEKEYBOARD) return false;
                RAWKEYBOARD keyboard = (RAWKEYBOARD)Marshal.PtrToStructure(
                    IntPtr.Add(buffer, headerSize), typeof(RAWKEYBOARD));
                if (keyboard.Message != 0x0100 && keyboard.Message != 0x0104 &&
                    keyboard.Message != 0x0101 && keyboard.Message != 0x0105) return false;
                virtualKey = NormalizeVirtualKey(keyboard.VKey, keyboard.MakeCode, keyboard.Flags);
                down = keyboard.Message == 0x0100 || keyboard.Message == 0x0104;
                // Raw Input has no LLKHF_INJECTED flag: 0x04 means RI_KEY_E1.
                return true;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        void LogFailure(string reason)
        {
            if (_failures++ < 3) AppLogger.Warning("raw input read failed: " + reason);
        }

        public void Dispose() { Unregister(); }
    }
}
