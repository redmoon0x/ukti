using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Ukti
{
    // Pastes text into whatever window currently has keyboard focus.
    //
    // Uses the clipboard + simulated Ctrl+V because that is far more reliable
    // across Windows apps than synthesising individual character keystrokes.
    // The previous clipboard contents are restored afterwards so dictation does
    // not silently destroy what the user had copied.
    public static class TextInjector
    {
        const uint CF_UNICODETEXT = 13;
        const uint INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const ushort VK_CONTROL = 0x11;
        const ushort VK_V = 0x56;

        [DllImport("user32.dll", SetLastError = true)]
        static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindWindow(string cls, string win);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool OpenClipboard(IntPtr owner);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetClipboardData(uint format, IntPtr mem);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr GetClipboardData(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalFree(IntPtr mem);

        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalLock(IntPtr mem);

        [DllImport("kernel32.dll")]
        static extern bool GlobalUnlock(IntPtr mem);

        [DllImport("kernel32.dll")]
        static extern UIntPtr GlobalSize(IntPtr mem);

        const uint GMEM_MOVEABLE = 0x0002;

        public static IntPtr CurrentForegroundWindow()
        {
            return GetForegroundWindow();
        }

        static bool OpenClipboardRetry(int attempts)
        {
            for (int i = 0; i < attempts; i++)
            {
                if (OpenClipboard(IntPtr.Zero)) return true;
                Thread.Sleep(15);
            }
            return false;
        }

        static string ReadClipboardText()
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
            if (!OpenClipboardRetry(6)) return null;
            try
            {
                IntPtr h = GetClipboardData(CF_UNICODETEXT);
                if (h == IntPtr.Zero) return null;
                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero) return null;
                try
                {
                    int len = 0;
                    while (Marshal.ReadByte(p, len * 2) != 0 || Marshal.ReadByte(p, len * 2 + 1) != 0) len++;
                    if (len == 0) return string.Empty;
                    byte[] raw = new byte[len * 2];
                    Marshal.Copy(p, raw, 0, raw.Length);
                    return Encoding.Unicode.GetString(raw);
                }
                finally { GlobalUnlock(h); }
            }
            finally { CloseClipboard(); }
        }

        static bool WriteClipboardText(string text)
        {
            if (text == null) text = string.Empty;
            if (!OpenClipboardRetry(10)) return false;
            try
            {
                if (!EmptyClipboard()) return false;
                byte[] bytes = Encoding.Unicode.GetBytes(text);
                int size = bytes.Length + 2;
                IntPtr mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)size);
                if (mem == IntPtr.Zero) return false;
                IntPtr p = GlobalLock(mem);
                if (p == IntPtr.Zero) { GlobalFree(mem); return false; }
                Marshal.Copy(bytes, 0, p, bytes.Length);
                Marshal.WriteInt16(p, bytes.Length, 0);
                GlobalUnlock(mem);
                if (SetClipboardData(CF_UNICODETEXT, mem) == IntPtr.Zero)
                {
                    GlobalFree(mem);
                    return false;
                }
                return true;
            }
            finally { CloseClipboard(); }
        }

        static void SendCtrlV()
        {
            keybd_event((byte)VK_CONTROL, 0, 0, UIntPtr.Zero);
            Thread.Sleep(8);
            keybd_event((byte)VK_V, 0, 0, UIntPtr.Zero);
            Thread.Sleep(12);
            keybd_event((byte)VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(8);
            keybd_event((byte)VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        // Pastes text. Returns null if the paste was dispatched, or a
        // human-readable reason if it was refused.
        //
        // `targetWindow` is the window that had focus when dictation started;
        // pasting only proceeds if that same window still has focus, so text can
        // never land in an unexpected application.
        //
        // Clipboard restoration happens on a background thread: the target
        // application services WM_PASTE asynchronously, so overwriting the
        // clipboard immediately would race the paste itself.
        public static string Paste(string text, IntPtr targetWindow)
        {
            if (string.IsNullOrEmpty(text)) return null;

            IntPtr now = GetForegroundWindow();
            if (targetWindow != IntPtr.Zero && now != targetWindow)
            {
                return "The active window changed, so the text was left on the clipboard.";
            }
            if (now != IntPtr.Zero && !IsWindow(now))
            {
                return "The target window closed, so the text was left on the clipboard.";
            }

            string previous = ReadClipboardText();

            if (!WriteClipboardText(text))
            {
                return "Could not access the clipboard.";
            }

            SendCtrlV();

            if (previous != null)
            {
                Thread restore = new Thread(delegate ()
                {
                    Thread.Sleep(450);
                    // Restore only if the clipboard still holds our text; if the
                    // user or another app replaced it meanwhile, leave it alone.
                    string current = ReadClipboardText();
                    if (current != null && current == text)
                    {
                        WriteClipboardText(previous);
                    }
                });
                restore.IsBackground = true;
                restore.Start();
            }
            return null;
        }

        public static bool CopyToClipboard(string text)
        {
            return WriteClipboardText(text);
        }

        internal static bool ReadClipboardForTest(out string text)
        {
            text = ReadClipboardText();
            return text != null;
        }
    }
}
