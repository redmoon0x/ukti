using System;
using System.IO;
using System.Text;

namespace Ukti
{
    sealed class Settings
    {
        public int SchemaVersion = AppData.SchemaVersion;
        public int MicrophoneIndex = -1;
        public bool Enabled = true;
        public bool RunAtStartup;
        public bool RunInBackground = true;
        public bool FirstRunDone;
        public string ShortcutText = Shortcut.Default.ToString();
        public string ModelId = ModelCatalog.All[0].Id;
        public int IdleUnloadMinutes = IdleUnloadPolicy.DefaultMinutes;
        public Shortcut GetShortcut()
        {
            Shortcut shortcut;
            return Shortcut.TryParse(ShortcutText, out shortcut) ? shortcut : Shortcut.Default;
        }
        static string FilePath { get { return AppData.SettingsPath; } }
        public static Settings Load()
        {
            AppData.MigrateLegacy();
            try { return File.Exists(FilePath) ? Parse(File.ReadAllLines(FilePath)) : new Settings(); }
            catch (Exception ex) { TrayApp.Log("settings load failed: " + ex.Message); return new Settings(); }
        }
        internal static Settings Parse(string[] lines)
        {
            Settings settings = new Settings();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string key = line.Substring(0, equals).Trim(), value = line.Substring(equals + 1).Trim();
                switch (key)
                {
                    case "schema_version":
                        int version;
                        if (int.TryParse(value, out version)) settings.SchemaVersion = version;
                        break;
                    case "microphone":
                        int index;
                        if (int.TryParse(value, out index)) settings.MicrophoneIndex = Math.Max(-1, index);
                        break;
                    case "enabled": settings.Enabled = value != "false" && value != "0"; break;
                    case "run_at_startup": settings.RunAtStartup = value == "true" || value == "1"; break;
                    case "run_in_background": settings.RunInBackground = value == "true" || value == "1"; break;
                    case "first_run_done": settings.FirstRunDone = value == "true" || value == "1"; break;
                    case "shortcut": settings.ShortcutText = value; break;
                    case "model": settings.ModelId = value; break;
                    case "idle_unload_minutes":
                        int minutes;
                        if (int.TryParse(value, out minutes)) settings.IdleUnloadMinutes = IdleUnloadPolicy.Normalize(minutes);
                        break;
                }
            }
            settings.ShortcutText = settings.GetShortcut().ToString();
            settings.ModelId = ModelCatalog.Find(settings.ModelId).Id;
            return settings;
        }
        internal string Serialize()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("schema_version=" + SchemaVersion);
            text.AppendLine("microphone=" + MicrophoneIndex);
            text.AppendLine("enabled=" + (Enabled ? "true" : "false"));
            text.AppendLine("run_at_startup=" + (RunAtStartup ? "true" : "false"));
            text.AppendLine("run_in_background=" + (RunInBackground ? "true" : "false"));
            text.AppendLine("first_run_done=" + (FirstRunDone ? "true" : "false"));
            text.AppendLine("shortcut=" + GetShortcut());
            text.AppendLine("model=" + ModelCatalog.Find(ModelId).Id);
            text.AppendLine("idle_unload_minutes=" + IdleUnloadPolicy.Normalize(IdleUnloadMinutes));
            return text.ToString();
        }
        public bool Save()
        {
            string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(AppData.Root);
                File.WriteAllText(temporary, Serialize(), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
                else File.Move(temporary, FilePath);
                return true;
            }
            catch (Exception ex) { TrayApp.Log("settings save failed: " + ex.Message); return false; }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }
    }
}
