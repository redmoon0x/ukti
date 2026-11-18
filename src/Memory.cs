using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ukti
{
    // Trims the process working set when the app is idle.
    //
    // The speech runtime reserves a large amount of address space and touches a
    // good part of it while a model loads and warms up. Those pages are not all
    // needed once the app is sitting in the tray waiting for a shortcut, so they
    // are handed back rather than held while the user works in other programs.
    //
    // Trimming is deliberately kept away from active use: pulling pages out
    // during or just before dictation would cost a page fault and add latency.
    static class Memory
    {
        [DllImport("psapi.dll")]
        static extern bool EmptyWorkingSet(IntPtr process);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        public static long WorkingSetMb()
        {
            try
            {
                using (Process p = Process.GetCurrentProcess())
                    return p.WorkingSet64 / (1024 * 1024);
            }
            catch
            {
                return -1;
            }
        }

        // Releases unreferenced pages back to the OS. Safe to call at any time,
        // but only worth doing when nothing is about to run.
        public static void Trim()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                EmptyWorkingSet(GetCurrentProcess());
            }
            catch (Exception ex)
            {
                TrayApp.Log("memory trim failed: " + ex.Message);
            }
        }
    }
}
