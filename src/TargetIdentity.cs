using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Ukti
{
    // Captures the process identity of the window that had focus when dictation
    // started. HWND equality alone is unsafe: Windows can reuse a handle after
    // a window closes, so paste also verifies the owning process and path.
    sealed class TargetIdentity
    {
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        public readonly IntPtr Window;
        public readonly uint ProcessId;
        public readonly string Executable;

        public TargetIdentity(IntPtr window)
        {
            Window = window;
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            ProcessId = pid;
            Executable = ProcessPath(pid);
        }

        public bool Matches(IntPtr currentWindow)
        {
            if (currentWindow == IntPtr.Zero) return false;
            uint pid;
            GetWindowThreadProcessId(currentWindow, out pid);
            if (pid != ProcessId) return false;
            return Executable == null || string.Equals(ProcessPath(pid), Executable, StringComparison.OrdinalIgnoreCase);
        }

        static string ProcessPath(uint pid)
        {
            if (pid == 0) return null;
            IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                uint size = 1024;
                StringBuilder text = new StringBuilder((int)size);
                if (QueryFullProcessImageName(handle, 0, text, ref size) != IntPtr.Zero) return text.ToString();
                return null;
            }
            catch (Exception ex) { TrayApp.Log("process identity check failed: " + ex.Message); return null; }
            finally { CloseHandle(handle); }
        }
    }
}
