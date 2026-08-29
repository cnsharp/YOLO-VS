using System;
using System.IO;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Minimal file+debug logger so we can diagnose the terminal pipeline at runtime.
    /// Log file: %LocalAppData%\YoloVS\yolo.log  (also mirrored to Debug output).
    /// </summary>
    internal static class Log
    {
        /// <summary>
        /// Opt-in verbosity. When false (default) the noisiest per-frame diagnostics (e.g. the
        /// pty output preview) are suppressed so yolo.log stays readable; flip to true to debug
        /// the terminal pipeline. Error/diagnostic lines are always written.
        /// </summary>
        public static bool Verbose { get; set; }

        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YoloVS", "yolo.log");

        private static readonly object _lock = new object();

        static Log()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? "."); }
            catch { }
        }

        public static void Write(string msg)
        {
            WriteCore(msg);
        }

        /// <summary>Writes only when <see cref="Verbose"/> is enabled.</summary>
        public static void WriteVerbose(string msg)
        {
            if (Verbose) WriteCore(msg);
        }

        private static void WriteCore(string msg)
        {
            // Serialize all appenders so the pty reader thread and the UI thread can't race on
            // the shared log file (concurrent File.AppendAllText can throw a sharing violation).
            try
            {
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}";
                lock (_lock)
                {
                    File.AppendAllText(FilePath, line);
                }
                System.Diagnostics.Debug.WriteLine("[Yolo] " + msg);
            }
            catch { }
        }

        /// <summary>Clears the log so each run starts fresh.</summary>
        public static void Reset()
        {
            try { File.WriteAllText(FilePath, $"[{DateTime.Now:HH:mm:ss.fff}] === Yolo log start ==={Environment.NewLine}"); }
            catch { }
        }
    }
}
