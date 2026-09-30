using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Runs agent install / update commands head-less and opens agent home pages in the browser.
    /// Provides the install / upgrade command builders (package-manager and shell installers) plus the
    /// shared head-less command runner used for both install and version checks.
    ///
    /// Platform notes:
    ///   • Windows — commands run through <c>powershell -NoProfile -Command</c> (so <c>irm</c>,
    ///     <c>winget</c>, npm/pip all work). <c>curl</c> is also built into Windows 10+ but we
    ///     route <c>shell</c> specs through their <c>cmdWin</c> variant which uses PowerShell.
    ///   • macOS / Linux — commands run through the user's login shell (<c>$SHELL -lic</c>) so PATH
    ///     injected by nvm / Homebrew / pyenv profiles is respected.
    ///   • <c>brew</c> is intentionally NOT run on native Windows (Homebrew has no Windows port);
    ///     callers fall back to opening the agent's site. See <see cref="InstallSpec.AutomatableOnPlatform"/>.
    /// </summary>
    public static class AgentInstaller
    {
        /// <summary>Open a URL in the OS-registered handler (the default browser). Best-effort.</summary>
        public static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                // UseShellExecute routes the URL to the OS-registered protocol handler (browser).
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // Best-effort: a missing handler or locked-down shell shouldn't fault the caller.
            }
        }

        /// <summary>
        /// The full install command for a spec: the package-manager or shell one-liner. For the
        /// <c>url</c> type (and unknown types) this returns the URL string, so callers should
        /// <see cref="OpenUrl"/> it rather than running it as a command.
        /// </summary>
        public static string BuildInstallCommand(InstallSpec spec) => spec.Type switch
        {
            "npm" => $"npm install -g '{Escape(spec.Pkg)}'",
            "pip" => $"python3 -m pip install '{Escape(spec.Pkg)}'",
            "brew" => $"brew install '{Escape(spec.Pkg)}'",
            "shell" => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && spec.CmdWin.Length > 0
                ? spec.CmdWin
                : spec.Cmd,
            _ => spec.Url // url (or unknown) -> browser target
        };

        /// <summary>
        /// Mutating upgrade command. For <c>shell</c> agents that declare <c>updateCmd</c>, run that
        /// (the agent's own self-update subcommand, e.g. <c>claude update</c>); otherwise fall back to
        /// re-running the installer, which also fetches the latest version. For <c>url</c> type this
        /// returns the URL (callers open the site instead of running a command).
        /// </summary>
        public static string BuildUpdateCommand(InstallSpec spec) => spec.Type switch
        {
            "npm" => $"npm install -g '{Escape(spec.Pkg)}'",
            "pip" => $"python3 -m pip install --upgrade '{Escape(spec.Pkg)}'",
            "brew" => $"brew upgrade '{Escape(spec.Pkg)}'",
            "shell" => !string.IsNullOrEmpty(spec.UpdateCmd)
                ? spec.UpdateCmd
                : (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && spec.CmdWin.Length > 0
                    ? spec.CmdWin
                    : spec.Cmd),
            _ => spec.Url
        };

        /// <summary>
        /// Run a shell command head-less and return (<c>ok</c>, last non-empty output line).
        /// <c>ok</c> is true only when the process finished and exited 0. A 600-second timeout stops a
        /// stuck installer (or a pager triggered by <c>--version</c>) from hanging the caller; PAGER is
        /// forced to <c>cat</c> so a pager never blocks waiting on a TTY.
        /// </summary>
        public static (bool ok, string lastLine) RunCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return (false, "empty command");

            ProcessStartInfo psi;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // PowerShell parses its own -Command string, so wrap the command in double quotes and
                // escape any literal double quote inside it.
                psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"{command.Replace("\"", "`\"")}\"");
            }
            else
            {
                // Login shell so profile-injected PATH (nvm / Homebrew / pyenv) is picked up.
                var shell = Environment.GetEnvironmentVariable("SHELL") ?? string.Empty;
                var slash = shell.LastIndexOf('/');
                if (slash >= 0) shell = shell.Substring(slash + 1);
                if (string.IsNullOrWhiteSpace(shell)) shell = "bash";
                psi = new ProcessStartInfo(shell, $"-lic \"{command.Replace("\"", "\\\"")}\"");
            }

            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.EnvironmentVariables["PAGER"] = "cat";
            psi.EnvironmentVariables["GIT_PAGER"] = "cat";

            try
            {
                using var p = new Process { StartInfo = psi };
                p.Start();
                var stdout = p.StandardOutput.ReadToEnd();
                var stderr = p.StandardError.ReadToEnd();
                var finished = p.WaitForExit(600_000);
                var combined = string.IsNullOrEmpty(stderr) ? stdout : stdout + "\n" + stderr;
                var last = combined.Split('\n');
                string? lastLine = null;
                for (var i = last.Length - 1; i >= 0; i--)
                {
                    if (last[i].Trim().Length > 0) { lastLine = last[i].Trim(); break; }
                }
                return (finished && p.ExitCode == 0, lastLine ?? string.Empty);
            }
            catch (Exception ex)
            {
                return (false, ex.Message ?? "exception");
            }
        }

        /// <summary>Escape a single-quoted shell argument (the <c>replace("'", "'\\''")</c> idiom).</summary>
        private static string Escape(string s) => (s ?? string.Empty).Replace("'", "'\\''");
    }
}
