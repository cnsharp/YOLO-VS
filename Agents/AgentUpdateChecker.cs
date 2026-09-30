using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Background checker for newer versions of the <b>installed</b> agent CLIs. Faithful port of
    /// IntelliJ's <c>AgentUpdateChecker.kt</c>.
    ///
    /// Strategy: the <i>installed</i> version is read from the agent's own binary via
    /// <c>&lt;command&gt; --version</c>, while the <i>latest</i> version is queried from the agent's
    /// package source (<c>npm view</c> / <c>pip index versions</c> / <c>brew info</c>) and the two are
    /// compared as semver. <c>shell</c> / <c>url</c> agents (and <c>brew</c> on native Windows, where
    /// Homebrew doesn't exist) have no queryable package source, so they are reported as
    /// <see cref="Status.MANUAL"/> and the Update button opens the agent's site instead.
    ///
    /// Results are kept in a transient in-memory cache keyed by lower-cased command base name, so the
    /// Settings page can reflect them on open and the terminal dropdown could surface a badge. The cache
    /// is not persisted. <see cref="ResultsChanged"/> is raised on the background thread, so subscribers
    /// must marshal to the UI thread themselves.
    /// </summary>
    public static class AgentUpdateChecker
    {
        /// <summary>Per-agent update status.</summary>
        public enum Status { NA, CHECKING, UP_TO_DATE, UPDATE_AVAILABLE, MANUAL, ERROR }

        /// <summary>Result for one agent: status plus the detected installed/latest versions (for display).</summary>
        public sealed class UpdateInfo
        {
            public Status Status { get; }
            public string Current { get; }
            public string Latest { get; }

            public UpdateInfo(Status status, string current = "", string latest = "")
            {
                Status = status;
                Current = current;
                Latest = latest;
            }
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, UpdateInfo> _results =
            new Dictionary<string, UpdateInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<Action> _listeners = new List<Action>();
        private static bool _checking;

        /// <summary>Full result for a command (lower-cased base name), or null when never checked.</summary>
        public static UpdateInfo? InfoFor(string command)
        {
            var key = ExecutableNames.BaseName(command ?? string.Empty).ToLowerInvariant();
            lock (_lock) return _results.TryGetValue(key, out var i) ? i : null;
        }

        /// <summary>Just the status for a command.</summary>
        public static Status StatusFor(string command) => InfoFor(command)?.Status ?? Status.NA;

        /// <summary>Whether a background scan is currently running (so callers avoid overlapping scans).</summary>
        public static bool IsChecking { get { lock (_lock) return _checking; } }

        /// <summary>Immutable snapshot of all known results, keyed by lower-cased command base name.</summary>
        public static IReadOnlyDictionary<string, UpdateInfo> Snapshot()
        {
            lock (_lock) return new Dictionary<string, UpdateInfo>(_results, StringComparer.OrdinalIgnoreCase);
        }

        public static void AddListener(Action listener)
        {
            lock (_lock) _listeners.Add(listener);
        }

        public static void RemoveListener(Action listener)
        {
            lock (_lock) _listeners.Remove(listener);
        }

        private static void Publish()
        {
            Action[] snapshot;
            lock (_lock) snapshot = _listeners.ToArray();
            foreach (var l in snapshot)
            {
                try { l(); }
                catch { /* a misbehaving subscriber must not break the scan */ }
            }
        }

        /// <summary>Check every installed agent that declares an automatable install spec. Safe to call repeatedly.</summary>
        public static void CheckAll()
        {
            // Build the spec list (read-only) first so we don't hold the guard during I/O.
            var installed = InstalledAgents.CachedCommands; // lower-cased base names
            var specs = AgentRegistry.All
                .Select(def => new KeyValuePair<string, InstallSpec>(
                    ExecutableNames.BaseName(def.Command).ToLowerInvariant(), def.Install))
                .Where(kv => kv.Value != null && kv.Value.AutomatableOnPlatform
                            && installed.Any(x => string.Equals(x, kv.Key, StringComparison.OrdinalIgnoreCase)))
                .Select(kv => new KeyValuePair<string, InstallSpec>(kv.Key, kv.Value!))
                .ToList();

            if (specs.Count == 0) return;

            // Flip the guard synchronously so two rapid calls can't both spawn a scan.
            lock (_lock)
            {
                if (_checking) return;
                _checking = true;
            }

            Task.Run(() =>
            {
                try
                {
                    lock (_lock)
                        foreach (var kv in specs)
                            _results[kv.Key] = new UpdateInfo(Status.CHECKING);
                    Publish();

                    foreach (var kv in specs)
                        lock (_lock) _results[kv.Key] = CheckAgent(kv.Key, kv.Value);

                    Publish();
                }
                finally
                {
                    lock (_lock) _checking = false;
                }
            });
        }

        /// <summary>Re-check a single command (e.g. right after a successful upgrade) and publish the new status.</summary>
        public static void Refresh(string command, InstallSpec spec)
        {
            var key = ExecutableNames.BaseName(command ?? string.Empty).ToLowerInvariant();
            var info = CheckAgent(key, spec);
            lock (_lock) _results[key] = info;
            Publish();
        }

        /// <summary>Read the installed version, compare with the package source.</summary>
        private static UpdateInfo CheckAgent(string command, InstallSpec spec)
        {
            var current = CurrentVersion(command);
            if (!CanQueryVersion(spec))
                return new UpdateInfo(Status.MANUAL, current, string.Empty);

            var latest = LatestVersion(spec);
            if (latest.Length > 0 && current.Length > 0)
            {
                var cmp = CompareSemver(SemverOf(current), SemverOf(latest));
                // current >= latest (incl. a newer-than-latest local build) => up to date.
                return cmp < 0
                    ? new UpdateInfo(Status.UPDATE_AVAILABLE, current, latest)
                    : new UpdateInfo(Status.UP_TO_DATE, current, latest);
            }
            if (latest.Length > 0)
            {
                // Installed version unreadable; fall back to the package manager's outdated probe.
                var outdated = ProbeOutdated(spec);
                return new UpdateInfo(outdated ? Status.UPDATE_AVAILABLE : Status.UP_TO_DATE, current, latest);
            }
            return new UpdateInfo(Status.ERROR, current, string.Empty);
        }

        /// <summary>Whether this spec's latest version can be queried on the current platform.</summary>
        private static bool CanQueryVersion(InstallSpec spec) =>
            spec.AutomatableOnPlatform && spec.Type is "npm" or "pip" or "brew";

        /// <summary>Run <c>&lt;command&gt; --version</c> (falling back to <c>-v</c>) and extract the first semver token.</summary>
        private static string CurrentVersion(string command)
        {
            foreach (var flag in new[] { "--version", "-v" })
            {
                var (ok, outp) = AgentInstaller.RunCommand($"{command} {flag}");
                if (ok)
                {
                    var v = SemverOf(outp);
                    if (v != null) return string.Join(".", v);
                }
            }
            return string.Empty;
        }

        /// <summary>Latest published version for a package-manager spec. Empty if it cannot be determined.</summary>
        private static string LatestVersion(InstallSpec spec) => spec.Type switch
        {
            "npm" => NpmLatest(spec),
            "pip" => PipLatest(spec),
            "brew" => BrewLatest(spec),
            _ => string.Empty
        };

        private static string NpmLatest(InstallSpec spec)
        {
            var (ok, outp) = AgentInstaller.RunCommand($"npm view '{Escape(spec.Pkg)}' version");
            if (ok)
            {
                var v = SemverOf(outp);
                return v != null ? string.Join(".", v) : string.Empty;
            }
            return string.Empty;
        }

        private static string BrewLatest(InstallSpec spec)
        {
            var (ok, outp) = AgentInstaller.RunCommand($"brew info '{Escape(spec.Pkg)}' --json=v2");
            if (ok)
            {
                // JSON: { "versions": { "stable": "1.2.3" } }
                var m = Regex.Match(outp, "\"stable\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success)
                {
                    var v = SemverOf(m.Groups[1].Value);
                    return v != null ? string.Join(".", v) : m.Groups[1].Value;
                }
            }
            return string.Empty;
        }

        /// <summary>pip latest: prefer <c>pip index versions</c> (newest listed first), fall back to a dry-run's "Would install".</summary>
        private static string PipLatest(InstallSpec spec)
        {
            var pkg = Escape(spec.Pkg);
            var (ok1, out1) = AgentInstaller.RunCommand($"python3 -m pip index versions '{pkg}'");
            if (ok1)
            {
                var m = Regex.Match(out1, "Available versions:\\s*([0-9][^,\\s]*)");
                if (m.Success)
                {
                    var v = SemverOf(m.Groups[1].Value);
                    return v != null ? string.Join(".", v) : m.Groups[1].Value;
                }
            }
            var (ok2, out2) = AgentInstaller.RunCommand($"python3 -m pip install --upgrade '{pkg}' --dry-run");
            if (ok2)
            {
                var m2 = Regex.Match(out2,
                    $"Would install\\s+{Regex.Escape(pkg)}[=\\s-]+([0-9][0-9A-Za-z.\"-]*)");
                if (m2.Success)
                {
                    var g = m2.Groups[1].Value.Trim('-', '"');
                    var v = SemverOf(g);
                    return v != null ? string.Join(".", v) : g;
                }
            }
            return string.Empty;
        }

        /// <summary>Legacy read-only "is there a newer version" probe, used as a fallback when the installed
        ///  version is unreadable. Returns true when the package manager reports an available upgrade.</summary>
        private static bool ProbeOutdated(InstallSpec spec)
        {
            var pkg = Escape(spec.Pkg);
            var cmd = spec.Type switch
            {
                "npm" => $"npm outdated -g '{pkg}' --json",
                "pip" => $"python3 -m pip install --upgrade --dry-run '{pkg}'",
                "brew" => $"brew outdated '{pkg}'",
                _ => string.Empty
            };
            if (cmd.Length == 0) return false;

            var (ok, outp) = AgentInstaller.RunCommand(cmd);
            if (!ok) return false;

            return spec.Type switch
            {
                "npm" => { var t = outp.Trim(); return t.Length > 0 && t != "{}"; }
                "pip" => Regex.IsMatch(outp, $"Would install\\s+{Regex.Escape(pkg)}[=\\s-]"),
                "brew" => outp.Trim().Length > 0,
                _ => false
            };
        }

        /// <summary>Extract the first <c>major.minor[.patch]</c> token from arbitrary text (ignoring any pre-release suffix).</summary>
        private static int[]? SemverOf(string text)
        {
            var m = Regex.Match(text, @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return null;
            return new[]
            {
                int.TryParse(m.Groups[1].Value, out var a) ? a : 0,
                int.TryParse(m.Groups[2].Value, out var b) ? b : 0,
                int.TryParse(m.Groups[3].Value, out var c) ? c : 0
            };
        }

        /// <summary>Compare two semver arrays lexicographically; 0 if equal, &lt;0 if a&lt;b, &gt;0 if a&gt;b.</summary>
        private static int CompareSemver(int[]? a, int[]? b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            var n = Math.Min(a.Length, b.Length);
            for (var i = 0; i < n; i++)
                if (a[i] != b[i]) return a[i] - b[i];
            return a.Length - b.Length;
        }

        /// <summary>Escape a single-quoted shell argument (mirrors the IntelliJ <c>replace("'", "'\\''")</c>).</summary>
        private static string Escape(string s) => (s ?? string.Empty).Replace("'", "'\\''");
    }
}
