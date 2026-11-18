using System;
using Microsoft.Win32;

namespace Ukti
{
    // Registers the app to run at sign-in, via the per-user Run key.
    //
    // The per-user key is used deliberately: it needs no administrator rights and
    // is trivially reversible, which is the right trade for a dictation utility.
    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "Ukti";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    return key.GetValue(ValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool Apply(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return false;

                    if (enabled)
                    {
                        string exe = System.IO.Path.Combine(
                            AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'), "Ukti.exe");
                        // --background keeps a sign-in launch from opening the
                        // settings window on top of whatever the user is doing.
                        key.SetValue(ValueName, "\"" + exe + "\" --background");
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                TrayApp.Log("startup registration failed: " + ex.Message);
                return false;
            }
        }
    }
}
