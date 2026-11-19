using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Ukti
{
    // Local, structured, rotating diagnostics. No telemetry, no network.
    // Writes are queued and flushed by one background thread so no caller's
    // thread blocks on disk I/O. Files rotate at 1 MB with seven retained.
    static class AppLogger
    {
        public enum Level { Debug, Info, Warning, Error }

        const long MaxFileBytes = 1024 * 1024;
        const int RetainedFiles = 7;
        static readonly string SessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
        static readonly BlockingCollection<string> Queue = new BlockingCollection<string>(new ConcurrentQueue<string>());
        static readonly Thread Writer;

        static AppLogger()
        {
            Writer = new Thread(WriteLoop) { IsBackground = true, Name = "Ukti.Logger" };
            Writer.Start();
        }

        public static void Log(Level level, string message)
        {
            string stamp = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
            Queue.Add(string.Concat(stamp, " [", SessionId, "] ", level.ToString().ToUpperInvariant(), " ", message));
        }

        public static void Debug(string message) { Log(Level.Debug, message); }
        public static void Info(string message) { Log(Level.Info, message); }
        public static void Warning(string message) { Log(Level.Warning, message); }
        public static void Error(string message) { Log(Level.Error, message); }

        static void WriteLoop()
        {
            foreach (string line in Queue.GetConsumingEnumerable())
            {
                try
                {
                    string directory = AppData.Root;
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, "ukti.log");
                    if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes) Rotate(path);
                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch { /* Diagnostics must never crash the app. */ }
            }
        }

        static void Rotate(string path)
        {
            for (int i = RetainedFiles - 1; i >= 1; i--)
            {
                string from = path + "." + (i - 1);
                if (!File.Exists(from)) continue;
                string to = path + "." + i;
                if (File.Exists(to)) File.Delete(to);
                File.Move(from, to);
            }
            if (File.Exists(path)) File.Move(path, path + ".0");
        }
    }
}
