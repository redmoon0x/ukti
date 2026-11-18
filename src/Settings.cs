using System;
using System.IO;
using System.Text;

namespace Ukti
{
    // User settings, stored as a small text file next to the executable next to
    // the binary. Kept deliberately plain: a handful of values does not justify
    // a serialiser, and a readable file can be edited or backed up by hand.
    sealed class Settings
    {
        // -1 means "follow the Windows default device".
        public int MicrophoneIndex = -1;

        // Master switch. When off, the shortcut is inactive and the tray icon
        // shows a muted state.
        public bool Enabled = true;

        // Register with Windows so the app comes up when the user signs in.
        public bool RunAtStartup = false;

        // Closing the window keeps the process alive in the tray. When false,
        // closing the window quits the application.
        public bool RunInBackground = true;

        // Set after the first launch so the preferences window is only forced
        // open once.
        public bool FirstRunDone = false;

        static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt"); }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "microphone":
                            int index;
                            if (int.TryParse(value, out index)) s.MicrophoneIndex = index;
                            break;
                        case "enabled":
                            s.Enabled = value != "false" && value != "0";
                            break;
                        case "run_at_startup":
                            s.RunAtStartup = value == "true" || value == "1";
                            break;
                        case "run_in_background":
                            s.RunInBackground = value == "true" || value == "1";
                            break;
                        case "first_run_done":
                            s.FirstRunDone = value == "true" || value == "1";
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                TrayApp.Log("settings load failed: " + ex.Message);
            }
            return s;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Ukti settings");
                sb.AppendLine("# microphone: capture device index, or -1 for the Windows default");
                sb.AppendLine("microphone=" + MicrophoneIndex);
                sb.AppendLine("enabled=" + (Enabled ? "true" : "false"));
                sb.AppendLine("run_at_startup=" + (RunAtStartup ? "true" : "false"));
                sb.AppendLine("run_in_background=" + (RunInBackground ? "true" : "false"));
                sb.AppendLine("first_run_done=" + (FirstRunDone ? "true" : "false"));
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch (Exception ex)
            {
                TrayApp.Log("settings save failed: " + ex.Message);
            }
        }
    }
}
