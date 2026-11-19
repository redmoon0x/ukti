using System;

namespace Ukti
{
    // Regression coverage for P0.5: settings/models must live in LOCALAPPDATA,
    // never beside the EXE.
    static class AppDataTests
    {
        internal static void Run(Action<bool, string> assert)
        {
            assert(AppData.SettingsPath.StartsWith(AppData.Root, StringComparison.OrdinalIgnoreCase),
                "Settings must be stored under the app-data root.");
            assert(AppData.ModelDirectory.StartsWith(AppData.Root, StringComparison.OrdinalIgnoreCase),
                "Models must be stored under the app-data root.");
            assert(!string.IsNullOrEmpty(AppData.TestRoot) || AppData.Root.IndexOf(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase) >= 0,
                "The production app-data root must be under LOCALAPPDATA; isolated test roots may live elsewhere.");
            assert(ModelCatalog.DirectoryPath == AppData.ModelDirectory, "ModelCatalog must use the app-data directory.");
        }
    }
}
