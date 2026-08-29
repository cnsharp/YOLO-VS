using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Runtime probe: can the command be resolved on PATH (or as an absolute path)?
    /// Faithful port of the IntelliJ plugin's AgentDetector.kt.
    ///
    /// It ONLY resolves the command via "where" (Windows) / "command -v" through a login
    /// shell (Unix, so nvm / Homebrew-injected PATH is picked up). It deliberately does NOT
    /// execute the command itself — no version probe, no REPL — it just answers "can this
    /// command be found". (Earlier versions of this file spawned the command with --version;
    /// that deviated from the reference and is removed.)
    /// </summary>
    public static class AgentDetector
    {
        /// <summary>Whether the command resolves on PATH (equivalent to ResolvePath != null).</summary>
        public static bool CanExecute(string command) => ResolvePath(command) != null;

        /// <summary>
        /// Resolves the absolute path of the command, or null if not found. Returned value is
        /// the first resolved path. Used both for the existence check and (optionally) to show
        /// a confirmed absolute path in the UI.
        /// </summary>
        public static string? ResolvePath(string command)
        {
            var cmd = (command ?? string.Empty).Trim();
            if (cmd.Length == 0) return null;

            try
            {
                ProcessStartInfo psi;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    psi = new ProcessStartInfo("cmd.exe", $"/c where {cmd.Replace("\"", "")}");
                }
                else
                {
                    // Login shell so PATH injected by nvm / Homebrew profiles is respected.
                    psi = new ProcessStartInfo("bash", $"-lc \"command -v '{cmd.Replace("'", "'\\''")}'\"");
                }

                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using var p = new Process { StartInfo = psi };
                p.Start();
                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(5000);

                if (string.IsNullOrEmpty(output))
                    return null;

                // "where" may return several candidates; take the first.
                foreach (var line in output.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length > 0)
                        return trimmed;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
