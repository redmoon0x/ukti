using System;
using System.Windows.Forms;

namespace Ukti
{
    static class ChordTests
    {
        internal static void Run(Action<bool, string> assert)
        {
            assert(RawInputKeyboard.NormalizeVirtualKey(0x11, 0x1D, 0) == (ushort)Keys.LControlKey, "Raw Ctrl must map to left Ctrl.");
            assert(RawInputKeyboard.NormalizeVirtualKey(0x11, 0x1D, 3) == (ushort)Keys.RControlKey, "Raw E0 Ctrl release must map to right Ctrl.");
            assert(RawInputKeyboard.NormalizeVirtualKey(0x12, 0x38, 0) == (ushort)Keys.LMenu, "Raw Alt must map to left Alt.");
            assert(RawInputKeyboard.NormalizeVirtualKey(0x12, 0x38, 2) == (ushort)Keys.RMenu, "Raw E0 Alt must map to right Alt.");
            assert(RawInputKeyboard.NormalizeVirtualKey(0x10, 0x2A, 0) == (ushort)Keys.LShiftKey, "Raw left Shift must use its scan code.");
            assert(RawInputKeyboard.NormalizeVirtualKey(0x10, 0x36, 1) == (ushort)Keys.RShiftKey, "Raw right Shift release must use its scan code.");
            assert(RawInputKeyboard.NormalizeVirtualKey(0x20, 0x39, 0) == (ushort)Keys.Space, "Ordinary raw keys must remain unchanged.");
            Shortcut chord;
            Shortcut.TryParse("Ctrl+Space", out chord);
            using (HoldHotkey hook = new HoldHotkey())
            {
                hook.Configure(chord);
                int pressed = 0, released = 0;
                hook.Pressed += delegate { pressed++; };
                hook.Released += delegate { released++; };
                hook.Enabled = true;
                // Raw Input reports sided keys.
                hook.HandleKey((int)Keys.LControlKey, true);
                hook.HandleKey((int)Keys.Space, true);
                assert(pressed == 1, "Sided Ctrl + Space must arm the chord.");
                hook.HandleKey((int)Keys.Space, false);
                assert(released == 1, "Releasing Space must finish the chord.");
                hook.HandleKey((int)Keys.LControlKey, false);
                assert(released == 1, "Releasing Ctrl afterwards must not double-fire.");

                hook.HandleKey((int)Keys.LControlKey, true);
                hook.HandleKey((int)Keys.Space, true);
                assert(pressed == 2, "A fresh hold must arm again.");
                hook.HandleKey((int)Keys.LControlKey, false);
                assert(released == 2, "Releasing the modifier must finish the hold.");

                // Dispose/re-enable can occur while Space is held (pause/model
                // switch). Its consumed state must not block the next hold.
                hook.Dispose();
                hook.Enabled = true;
                hook.HandleKey((int)Keys.LControlKey, true);
                hook.HandleKey((int)Keys.Space, true);
                assert(pressed == 3, "Re-enabling after teardown must not retain a consumed trigger.");
            }
        }
    }
}
