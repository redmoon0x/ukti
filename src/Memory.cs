using System;
using System.Diagnostics;

namespace Ukti
{
    static class Memory
    {
        public static long WorkingSetMb()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                    return process.WorkingSet64 / (1024 * 1024);
            }
            catch { return -1; }
        }

        // Worker exit releases native/CUDA allocations. This is only an optional
        // managed-buffer collection after unloading, NOT an EmptyWorkingSet
        // trick that hides committed allocations and faults UI pages back in.
        public static void Trim()
        {
            try { GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false); }
            catch (Exception ex) { TrayApp.Log("memory collection failed: " + ex.Message); }
        }
    }
}
