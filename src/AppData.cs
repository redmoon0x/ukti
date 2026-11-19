using System;
using System.IO;

namespace Ukti
{
    // Single source of truth for writable state. Keeping settings and models
    // out of the install directory is required for standard-user, Program Files
    // and MSIX installs. Existing in-app files are migrated once.
    static class AppData
    {
        public const int SchemaVersion = 1;
        // Set only by the UI test host; tests that toggle dictation must never
        // overwrite the user's saved microphone or other preferences.
        internal static string TestRoot { get; set; }

        public static string Root
        {
            get
            {
                if (!string.IsNullOrEmpty(TestRoot)) return TestRoot;
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local)) return Path.Combine(local, "Ukti");
                // Fallback for service accounts without a roaming profile.
                return Path.Combine(Path.GetTempPath(), "Ukti");
            }
        }

        public static string SettingsPath { get { return Path.Combine(Root, "settings.txt"); } }
        public static string ModelDirectory { get { return Path.Combine(Root, "models"); } }

        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(ModelDirectory);
        }

        // Copies legacy files from beside the EXE into LOCALAPPDATA exactly once.
        // Never overwrites a newer local file; originals are left in place so a
        // failed migration can be retried after the next launch.
        public static void MigrateLegacy()
        {
            EnsureDirectories();
            string legacySettings = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt");
            if (!File.Exists(SettingsPath) && File.Exists(legacySettings))
            {
                try { File.Copy(legacySettings, SettingsPath); }
                catch (Exception ex) { TrayApp.Log("settings migration failed: " + ex.Message); }
            }
            string legacyModels = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
            if (Directory.Exists(legacyModels))
            {
                try
                {
                    foreach (string file in Directory.GetFiles(legacyModels, "*.gguf"))
                    {
                        string target = Path.Combine(ModelDirectory, Path.GetFileName(file));
                        if (!File.Exists(target)) File.Copy(file, target);
                    }
                }
                catch (Exception ex) { TrayApp.Log("model migration failed: " + ex.Message); }
            }
        }
    }
}
