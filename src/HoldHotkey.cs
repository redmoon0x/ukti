using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Ukti
{
    // Hold/release chord detector. Key events come from Raw Input (see
    // RawInputKeyboard), not a WH_KEYBOARD_LL hook: Raw Input with INPUTSINK
    // works behind any window and cannot be silently removed for exceeding
    // LowLevelHooksTimeout. HandleKey remains the pure state machine so the
    // chord rules are unit-testable without a system-wide input source.
    public sealed class HoldHotkey : IDisposable
    {
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

        readonly bool[] _down = new bool[256];
        Shortcut _shortcut = Shortcut.Default;
        RawInputKeyboard _rawInput;
        bool _enabled, _armed;
        int _consumedKey = -1;

        public event MethodInvoker Pressed, Released, Cancelled;
        public bool IsHooked { get { return _rawInput != null; } }
        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                _enabled = value;
                if (!value && _armed) { _armed = false; Raise(Cancelled); }
            }
        }
        public HoldHotkey() { }
        internal void Configure(Shortcut shortcut)
        {
            if (shortcut == null) throw new ArgumentNullException("shortcut");
            if (_armed) { _armed = false; Raise(Cancelled); }
            _shortcut = shortcut;
        }
        // Installs a Raw Input sink on the hidden dispatcher window's handle.
        public bool Install(IntPtr windowHandle)
        {
            if (IsHooked) return true;
            if (windowHandle == IntPtr.Zero) return false;
            for (int i = 0; i < _down.Length; i++) _down[i] = GetAsyncKeyState(i) < 0;
            RawInputKeyboard input = new RawInputKeyboard(windowHandle);
            if (!input.Register()) { input.Dispose(); return false; }
            _rawInput = input;
            AppLogger.Info("raw input registered on hwnd=" + windowHandle);
            return true;
        }
        // Legacy overload: callers without a window handle cannot use Raw Input.
        public bool Install() { return Install(GetFakeWindow()); }
        static IntPtr GetFakeWindow()
        {
            // Reserved for tests that call Install() without a dispatcher; a
            // real window is required for input, so report failure cleanly.
            return IntPtr.Zero;
        }
        // Called from the dispatcher window's WndProc. Returns true if handled.
        internal bool HandleRawInput(IntPtr lParam)
        {
            if (_rawInput == null) return false;
            ushort vk; bool down;
            if (!_rawInput.TryRead(lParam, out vk, out down)) return false;

            return HandleKey(vk, down);
        }
        public void Dispose()
        {
            _enabled = _armed = false;
            _consumedKey = -1;
            Array.Clear(_down, 0, _down.Length);
            if (_rawInput != null) { _rawInput.Dispose(); _rawInput = null; }
        }
        bool Any(Keys a, Keys left, Keys right) { return _down[(int)a] || _down[(int)left] || _down[(int)right]; }
        Keys CurrentModifiers()
        {
            Keys result = Keys.None;
            if (Any(Keys.ControlKey, Keys.LControlKey, Keys.RControlKey)) result |= Keys.Control;
            if (Any(Keys.Menu, Keys.LMenu, Keys.RMenu)) result |= Keys.Alt;
            if (Any(Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey)) result |= Keys.Shift;
            return result;
        }
        // Pure key-state entry point, exercised without installing a system input source.
        internal bool HandleKey(int vk, bool down)
        {
            if (vk < 0 || vk >= _down.Length) return false;
            bool repeat = _down[vk];
            _down[vk] = down;
            // Installation snapshots both generic and sided modifiers; Raw
            // Input normalizes events to sided keys. Keep the generic state in
            // sync so a modifier held during installation cannot remain stuck.
            if (down)
            {
                if (vk == (int)Keys.LControlKey || vk == (int)Keys.RControlKey) _down[(int)Keys.ControlKey] = true;
                if (vk == (int)Keys.LShiftKey || vk == (int)Keys.RShiftKey) _down[(int)Keys.ShiftKey] = true;
                if (vk == (int)Keys.LMenu || vk == (int)Keys.RMenu) _down[(int)Keys.Menu] = true;
            }
            else if (vk == (int)Keys.LControlKey || vk == (int)Keys.RControlKey)
            { if (!_down[(int)Keys.LControlKey] && !_down[(int)Keys.RControlKey]) _down[(int)Keys.ControlKey] = false; }
            else if (vk == (int)Keys.LShiftKey || vk == (int)Keys.RShiftKey)
            { if (!_down[(int)Keys.LShiftKey] && !_down[(int)Keys.RShiftKey]) _down[(int)Keys.ShiftKey] = false; }
            else if (vk == (int)Keys.LMenu || vk == (int)Keys.RMenu)
            { if (!_down[(int)Keys.LMenu] && !_down[(int)Keys.RMenu]) _down[(int)Keys.Menu] = false; }
            bool consume = vk == _consumedKey;
            if (!down)
            {
                if (consume) _consumedKey = -1;
                if (_armed && (vk == (int)_shortcut.Key ||
                    (CurrentModifiers() & _shortcut.Modifiers) != _shortcut.Modifiers))
                { _armed = false; Raise(Released); }
                return consume;
            }
            if (vk == (int)Keys.Escape && _armed)
            { _armed = false; Raise(Cancelled); return true; }
            if (consume) return true; // Swallow repeats, even after a modifier was released.
            if (_enabled && !repeat && !_armed && vk == (int)_shortcut.Key &&
                CurrentModifiers() == _shortcut.Modifiers && !_down[(int)Keys.LWin] && !_down[(int)Keys.RWin])
            {
                _consumedKey = vk;
                _armed = true;
                Raise(Pressed);
                return true;
            }
            return false;
        }
        static void Raise(MethodInvoker handler)
        {
            if (handler == null) return;
            try { handler(); } catch (Exception ex) { TrayApp.Log("hotkey handler error: " + ex.Message); }
        }
    }
}
