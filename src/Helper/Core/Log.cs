using System;
using System.IO;

namespace ControllerToggle.Helper
{
    /// <summary>Small append-only log in %ProgramData%\ControllerToggle\helper.log (rotated at 512 KB).</summary>
    internal static class Log
    {
        private const long MaxBytes = 512 * 1024;
        private static readonly object Gate = new object();

        /// <summary>Set to false when the current process can't write the log (non-admin CLI).</summary>
        public static bool FileEnabled = true;

        public static string FilePath
        {
            get { return Path.Combine(Settings.DataDirectory, "helper.log"); }
        }

        public static void Info(string message)
        {
            Write("INFO ", message);
        }

        public static void Warn(string message)
        {
            Write("WARN ", message);
        }

        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        private static void Write(string level, string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + message;
            lock (Gate)
            {
                if (!FileEnabled)
                {
                    return;
                }

                try
                {
                    string path = FilePath;
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        string old = Path.Combine(Settings.DataDirectory, "helper.old.log");
                        if (File.Exists(old))
                        {
                            File.Delete(old);
                        }
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch
                {
                    // Logging must never take the helper down.
                }
            }
        }
    }
}
