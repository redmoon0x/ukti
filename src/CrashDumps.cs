using System;
using System.IO;
using Microsoft.Win32;

namespace Ukti
{
    // Enables local Windows Error Reporting mini dumps. Dumps stay on disk
    // under LOCALAPPDATA; nothing is uploaded. No telemetry is collected.
    static class CrashDumps
    {
        public static void Enable()
        {
            try
            {
                string directory = Path.Combine(AppData.Root, "crashes");
                Directory.CreateDirectory(directory);
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\Ukti.exe"))
                {
                    if (key == null) return;
                    key.SetValue("DumpFolder", directory, RegistryValueKind.ExpandString);
                    key.SetValue("DumpCount", 3, RegistryValueKind.DWord);
                    key.SetValue("DumpType", 1, RegistryValueKind.DWord); // mini dump
                }
            }
            catch (Exception ex) { AppLogger.Warning("crash dump setup failed: " + ex.Message); }
        }
    }
}
