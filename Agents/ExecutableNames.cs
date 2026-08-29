using System;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Normalize executable file names, mirroring IntelliJ's ExecutableNames.kt.
    /// Injection rules match by executable base name, so /usr/local/bin/claude and claude.cmd
    /// resolve to the same agent.
    /// </summary>
    public static class ExecutableNames
    {
        private static readonly string[] ExeSuffixes = { ".exe", ".cmd", ".bat", ".ps1" };

        /// <summary>
        /// Extract the executable base name: strip directory and Windows executable suffix
        /// (case-insensitive). npm on Windows ships both foo.cmd and foo.ps1, either of which may
        /// resolve on PATH, so both collapse to the same base name.
        /// </summary>
        public static string BaseName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path ?? string.Empty;
            var name = path.Substring(path.LastIndexOfAny(new[] { '/', '\\' }) + 1);
            foreach (var suffix in ExeSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return name.Substring(0, name.Length - suffix.Length);
            }
            return name;
        }
    }
}
