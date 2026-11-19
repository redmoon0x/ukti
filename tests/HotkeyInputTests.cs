using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Ukti
{
    // Exercises the real Windows registration boundary, not just HandleKey.
    // This test process owns its registration; no keystrokes are injected.
    static class HotkeyInputTests
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE
        {
            public ushort UsagePage, Usage;
            public uint Flags;
            public IntPtr Target;
        }
        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetRegisteredRawInputDevices([Out] RAWINPUTDEVICE[] devices, ref uint count, uint size);

        static void Assert(bool value, string message)
        { if (!value) throw new Exception(message); }

        static IntPtr KeyboardTarget()
        {
            uint count = 0, size = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE));
            Assert(GetRegisteredRawInputDevices(null, ref count, size) != uint.MaxValue, "Cannot query Raw Input registrations.");
            if (count == 0) return IntPtr.Zero;
            RAWINPUTDEVICE[] devices = new RAWINPUTDEVICE[count];
            uint read = GetRegisteredRawInputDevices(devices, ref count, size);
            Assert(read != uint.MaxValue, "Cannot read Raw Input registrations.");
            for (int i = 0; i < read; i++)
                if (devices[i].UsagePage == 1 && devices[i].Usage == 6) return devices[i].Target;
            return IntPtr.Zero;
        }

        internal static int Run()
        {
            try
            {
                using (Control window = new Control())
                using (HoldHotkey hotkey = new HoldHotkey())
                {
                    Assert(hotkey.Install(window.Handle), "Keyboard listener must install on a real window.");
                    Assert(hotkey.IsHooked, "Successful registration must retain the listener so WM_INPUT is not ignored.");
                    Assert(KeyboardTarget() == window.Handle, "Windows must deliver keyboard input to the dispatcher.");
                    Assert(hotkey.Install(window.Handle) && hotkey.IsHooked, "Repeated installation must preserve the listener.");
                    hotkey.Dispose();
                    Assert(!hotkey.IsHooked && KeyboardTarget() == IntPtr.Zero, "Dispose must unregister the Windows keyboard sink.");
                    Assert(!hotkey.Install(), "Installation without a dispatcher must fail cleanly.");
                    Assert(hotkey.Install(window.Handle) && hotkey.IsHooked, "Dictation must be able to re-enable its listener.");
                    hotkey.Dispose();
                    Assert(KeyboardTarget() == IntPtr.Zero, "Re-enabled listener must also unregister.");
                }
                Console.WriteLine("PASS: native keyboard registration, retained listener, teardown and reinstall. No keystrokes injected.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
