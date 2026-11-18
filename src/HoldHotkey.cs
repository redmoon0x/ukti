using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Ukti
{
    // Hold-to-talk shortcut via a low-level keyboard hook (WH_KEYBOARD_LL).
    //
    // RegisterHotKey cannot express this interaction: it only reports the press,
    // so the app could never learn that the user let go. The hook lets us mirror
    // real key state, and it is passive - it observes and never swallows a key,
    // so the shortcut cannot break normal typing anywhere else in Windows.
    public sealed class HoldHotkey : IDisposable
    {
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100;
        const int WM_KEYUP = 0x0101;
        const int WM_SYSKEYDOWN = 0x0104;
        const int WM_SYSKEYUP = 0x0105;

        const int VK_CONTROL = 0x11;
        const int VK_LCONTROL = 0xA2;
        const int VK_RCONTROL = 0xA3;
        const int VK_SPACE = 0x20;
        const int VK_ESCAPE = 0x1B;
        const int VK_MENU = 0x12;

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vk);

        readonly HookProc _proc;             // held so the GC cannot collect it
        IntPtr _hook = IntPtr.Zero;
        bool _armed;                         // Ctrl+Space is down and dictation started
        bool _ctrlDown;
        bool _spaceConsumed;

        public event MethodInvoker Pressed;
        public event MethodInvoker Released;
        public event MethodInvoker Cancelled;

        public bool IsHooked { get { return _hook != IntPtr.Zero; } }

        public HoldHotkey()
        {
            _proc = OnKey;
        }

        public bool Install()
        {
            if (_hook != IntPtr.Zero) return true;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            return _hook != IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        static bool IsCtrl(int vk)
        {
            return vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL;
        }

        static bool CtrlPhysicallyDown()
        {
            return (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 ||
                   (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0 ||
                   (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;
        }

        IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
                return CallNextHookEx(_hook, nCode, wParam, lParam);

            int msg = wParam.ToInt32();
            KBDLLHOOKSTRUCT data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                lParam, typeof(KBDLLHOOKSTRUCT));
            int vk = (int)data.vkCode;

            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            if (isDown)
            {
                if (IsCtrl(vk))
                {
                    _ctrlDown = true;
                }
                else if (vk == VK_SPACE)
                {
                    // Guard against / repeat: only the first transition starts.
                    if (_ctrlDown || CtrlPhysicallyDown())
                    {
                        _spaceConsumed = true;
                        if (!_armed)
                        {
                            _armed = true;
                            Raise(Pressed);
                        }
                        // Swallow the key so the target app never sees a space
                        // typed. Returning non-zero stops further processing.
                        return (IntPtr)1;
                    }
                }
                else if (vk == VK_ESCAPE && _armed)
                {
                    _armed = false;
                    _spaceConsumed = false;
                    Raise(Cancelled);
                }
            }
            else if (isUp)
            {
                if (IsCtrl(vk))
                {
                    _ctrlDown = false;
                    if (_armed && !(GetAsyncKeyState(VK_SPACE) < 0))
                    {
                        _armed = false;
                        _spaceConsumed = false;
                        Raise(Released);
                    }
                }
                else if (vk == VK_SPACE)
                {
                    if (_spaceConsumed)
                    {
                        _spaceConsumed = false;
                        if (_armed)
                        {
                            _armed = false;
                            Raise(Released);
                        }
                        return (IntPtr)1;
                    }
                }
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        static void Raise(MethodInvoker handler)
        {
            if (handler == null) return;
            try { handler(); }
            catch (Exception ex) { TrayApp.Log("hotkey handler error: " + ex.Message); }
        }
    }
}
