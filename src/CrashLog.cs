using System;
using System.IO;

namespace Ukti
{
    // Synchronous crash log: must survive an immediate process exit, so it
    // does not go through the async AppLogger queue.
    static class CrashLog
    {
        public static void Write(Exception ex)
        {
            try
            {
                string path = Path.Combine(AppData.Root, "crash.log");
                Directory.CreateDirectory(AppData.Root);
                File.AppendAllText(path,
                    DateTimeOffset.Now.ToString("o") + " " + (ex == null ? "(null)" : ex.ToString()) +
                    Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}
