using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Background checker for newer versions of the <b>installed</b> agent CLIs.
    ///
    /// Strategy: the <i>installed</i> version is read from the agent's own binary via
    /// <c>&lt;command&gt; --version</c>, while the <i>latest</i> version is queried from the agent's
    /// package source (<c>npm view</c> / <c>pip index versions</c> / <c>brew info</c>) and the two are
    /// compared as semver. <c>shell</c> / <c>url</c> agents (and <c>brew</c> on native Windows, where
    /// Homebrew doesn't exist) have no queryable package source, so they are reported as
    /// <see cref="Status.MANUAL"/> and the Update button opens the agent's site instead.
    ///
        /// Results are kept in an in-memory cache keyed by lower-cased command base name, so the
        /// Settings page can reflect them on open and the terminal dropdown could surface a badge. The cache
        /// is seeded from <see cref="YoloSettings.LatestVersions"/> on first scan (cache-first) and flushed
        /// back after every scan/refresh, so last-known latest versions survive restarts.
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
            public string Error { get; }

            public UpdateInfo(Status status, string current = "", string latest = "", string error = "")
            {
                Status = status;
                Current = current;
                Latest = latest;
                Error = error;
            }
        }

        private static readonly object _lock = new object();

        // Holds the in-flight background scan so its result is observed (VSTHRD110) and the task is rooted.
        private static Task? _scanTask;
        private static readonly Dictionary<string, UpdateInfo> _results =
            new Dictionary<string, UpdateInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<Action> _listeners = new List<Action>();
        private static bool _checking;
        private static bool _seeded;
        /// <summary>Whether an automatic update check has been requested (and started) this session.
        /// Set by <see cref="CheckAll"/> when a scan begins, so both the Options page's auto-check
        /// option and the Manage Agents tab agree on "has a check been done yet".</summary>
        private static bool _autoCheckRequested;
        private static readonly Dictionary<string, string> _dirtyLatest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Dirty current (installed) version detections, flushed once per scan (with a UTC timestamp for TTL).</summary>
        private static readonly Dictionary<string, CurrentVersionCacheEntry> _dirtyCurrent =
            new Dictionary<string, CurrentVersionCacheEntry>(StringComparer.OrdinalIgnoreCase);
        /// <summary>A current-version detection older than this (24h) is treated as stale and re-probed on open.</summary>
        private static readonly long _cacheTtlTicks = TimeSpan.FromHours(24).Ticks;

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

        /// <summary>Whether an automatic update check has been requested (and started) this session.</summary>
        public static bool AutoCheckRequested { get { lock (_lock) return _autoCheckRequested; } }

        /// <summary>Seed the in-memory cache from the persisted last-known versions so surfaces can
        /// show cached current/latest versions immediately, before any network probe lands.</summary>
        public static void Seed()
        {
            SeedFromCache();
        }

        /// <summary>Called when the Manage Agents document tab opens. If a background check was never
        /// requested this session (the tab is normally the first surface shown), start a background scan
        /// so the tab shows real versions. If a check already ran this session (e.g. the user clicked
        /// Check Updates), this is a no-op and the cached results are shown instead (no redundant scan).</summary>
        public static void EnsureCheckedForUi()
        {
            if (AutoCheckRequested) return;
            CheckAll();
        }

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

        /// <summary>Seed the in-memory cache with last-known latest versions so the panel shows them
        ///  immediately (cache-first) before the next network probe lands. Live checks overwrite these.</summary>
        private static void SeedFromCache()
        {
            if (_seeded) return;
            _seeded = true;
            try
            {
                foreach (var e in YoloSettings.Instance.LatestVersions)
                {
                    if (string.IsNullOrEmpty(e.Version)) continue;
                    var k = e.Command.ToLowerInvariant();
                    lock (_lock)
                    {
                        if (!_results.ContainsKey(k))
                            _results[k] = new UpdateInfo(Status.NA, string.Empty, e.Version);
                    }
                }

                // Seed last-known *current* (installed) versions within TTL so the panel shows the
                // installed version immediately, preserving any already-seeded latest version.
                var nowTicks = DateTime.UtcNow.Ticks;
                foreach (var e in YoloSettings.Instance.CurrentVersions)
                {
                    if (string.IsNullOrEmpty(e.Version)) continue;
                    if (nowTicks - e.Ticks > _cacheTtlTicks) continue; // stale -> re-probe on next scan
                    var k = e.Command.ToLowerInvariant();
                    lock (_lock)
                    {
                        if (!_results.ContainsKey(k))
                            _results[k] = new UpdateInfo(Status.NA, e.Version, string.Empty);
                        else
                        {
                            var existing = _results[k];
                            _results[k] = new UpdateInfo(existing.Status, e.Version, existing.Latest);
                        }
                    }
                }
            }
            catch { /* ignore malformed cache */ }
        }

        /// <summary>Record a detected latest version for the end-of-scan flush (one disk write per scan).</summary>
        private static void MarkLatest(string command, string version)
        {
            if (!string.IsNullOrEmpty(version))
                lock (_lock) _dirtyLatest[command.ToLowerInvariant()] = version;
        }

        /// <summary>Record a detected current (installed) version + timestamp for the end-of-scan flush.</summary>
        private static void MarkCurrent(string command, string version)
        {
            if (string.IsNullOrEmpty(version)) return;
            var k = command.ToLowerInvariant();
            lock (_lock) _dirtyCurrent[k] = new CurrentVersionCacheEntry
            {
                Command = k,
                Version = version,
                Ticks = DateTime.UtcNow.Ticks
            };
        }

        /// <summary>Merge dirty detections into the persisted settings and save once.</summary>
        private static void FlushLatest()
        {
            try
            {
                var settings = YoloSettings.Instance;
                if (_dirtyLatest.Count > 0)
                {
                    var latestMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in settings.LatestVersions) latestMap[e.Command.ToLowerInvariant()] = e.Version;
                    foreach (var kv in _dirtyLatest) latestMap[kv.Key] = kv.Value;
                    settings.LatestVersions = latestMap.Select(kv => new VersionCacheEntry { Command = kv.Key, Version = kv.Value }).ToList();
                }
                if (_dirtyCurrent.Count > 0)
                {
                    var currentMap = new Dictionary<string, CurrentVersionCacheEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in settings.CurrentVersions) currentMap[e.Command.ToLowerInvariant()] = e;
                    foreach (var kv in _dirtyCurrent) currentMap[kv.Key] = kv.Value;
                    settings.CurrentVersions = currentMap.Values.ToList();
                }
                if (_dirtyLatest.Count > 0 || _dirtyCurrent.Count > 0)
                    settings.Save();
            }
            catch { /* ignore */ }
            lock (_lock)
            {
                _dirtyLatest.Clear();
                _dirtyCurrent.Clear();
            }
        }

        /// <summary>Check every installed agent that declares an automatable install spec. Safe to call repeatedly.</summary>
        public static void CheckAll()
        {
            SeedFromCache();
            // Build the spec list (read-only) first so we don't hold the guard during I/O.
            var installed = InstalledAgents.CachedCommands; // lower-cased base names
            var specs = AgentRegistry.All
                .Select(def => new KeyValuePair<string, InstallSpec>(
                    ExecutableNames.BaseName(def.Command).ToLowerInvariant(), def.Install))
                .Where(kv => kv.Value != null && (kv.Value.AutomatableOnPlatform || kv.Value.HasLatestSource)
                            && installed.Any(x => string.Equals(x, kv.Key, StringComparison.OrdinalIgnoreCase)))
                .Select(kv => new KeyValuePair<string, InstallSpec>(kv.Key, kv.Value!))
                .ToList();

            if (specs.Count == 0) return;

            // Flip the guard synchronously so two rapid calls can't both spawn a scan.
            lock (_lock)
            {
                if (_checking) return;
                _checking = true;
                _autoCheckRequested = true;
            }

            _scanTask = Task.Run(() =>
            {
                try
                {
                    lock (_lock)
                        foreach (var kv in specs)
                        {
                            // Preserve the last-known current/latest so the UI keeps showing them
                            // (cache-first) instead of blanking to a dash while the probe is in flight.
                            var prev = _results.TryGetValue(kv.Key, out var p) ? p : null;
                            _results[kv.Key] = new UpdateInfo(
                                Status.CHECKING,
                                prev?.Current ?? string.Empty,
                                prev?.Latest ?? string.Empty);
                        }
                    Publish();

                    foreach (var kv in specs)
                    {
                        var info = CheckAgent(kv.Key, kv.Value);
                        MarkLatest(kv.Key, info.Latest);
                        if (!string.IsNullOrEmpty(info.Current)) MarkCurrent(kv.Key, info.Current);
                        lock (_lock) _results[kv.Key] = info;
                    }

                    Publish();
                }
                finally
                {
                    FlushLatest();
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
            MarkLatest(key, info.Latest);
            if (!string.IsNullOrEmpty(info.Current)) MarkCurrent(key, info.Current);
            FlushLatest();
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
            // Latest version could not be determined even though the source is queryable: surface a
            // reason so the card can explain the failure instead of showing a silent dash.
            var reason = spec.Latest?.Npm != null ? "npm registry unreachable"
                       : spec.Latest?.Github != null ? "github release lookup failed"
                       : spec.Latest?.Pypi != null ? "pypi lookup failed"
                       : spec.Latest?.Brew != null ? "brew formula lookup failed"
                       : spec.Latest?.Web != null ? "web version lookup failed"
                       : spec.Type switch
                         {
                             "npm" => "npm view failed",
                             "pip" => "pip version query failed",
                             "brew" => "brew info failed",
                             _ => "version query failed"
                         };
            return new UpdateInfo(Status.ERROR, current, string.Empty, reason);
        }

        /// <summary>Whether this spec's latest version can be queried on the current platform.</summary>
        private static bool CanQueryVersion(InstallSpec spec) =>
            (spec.AutomatableOnPlatform && spec.Type is "npm" or "pip" or "brew") || spec.HasLatestSource;

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
        private static string LatestVersion(InstallSpec spec)
        {
            if (!string.IsNullOrEmpty(spec.Latest?.Npm)) return NpmLatestFromApi(spec.Latest.Npm);
            if (!string.IsNullOrEmpty(spec.Latest?.Github)) return GithubLatest(spec.Latest.Github);
            if (!string.IsNullOrEmpty(spec.Latest?.Pypi)) return PypiLatest(spec.Latest.Pypi);
            if (!string.IsNullOrEmpty(spec.Latest?.Brew)) return BrewLatestFromApi(spec.Latest.Brew);
            if (spec.Latest?.Web != null && !string.IsNullOrEmpty(spec.Latest.Web.Url)) return WebLatest(spec.Latest.Web);
            return spec.Type switch
            {
                "npm" => NpmLatest(spec),
                "pip" => PipLatest(spec),
                "brew" => BrewLatest(spec),
                _ => string.Empty
            };
        }

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
                "npm" => (outp.Trim().Length > 0 && outp.Trim() != "{}"),
                "pip" => Regex.IsMatch(outp, $"Would install\\s+{Regex.Escape(pkg)}[=\\s-]"),
                "brew" => outp.Trim().Length > 0,
                _ => false
            };
        }

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        static AgentUpdateChecker()
        {
            _http.DefaultRequestHeaders.UserAgent.TryParseAdd("agentyolo");
        }

        /// <summary>Fetch a URL body as text with a bounded timeout, or null on any failure (so the caller shows a
        ///  dash instead of hanging the scan on a blocked/slow registry).</summary>
        private static string? FetchText(string url, string? accept = null)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (accept != null) req.Headers.Accept.TryParseAdd(accept);
                using (var resp = _http.SendAsync(req).GetAwaiter().GetResult())
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Latest version from the npm dist-tags endpoint (small payload, unlike the full packument).</summary>
        private static string NpmLatestFromApi(string pkg)
        {
            var url = "https://registry.npmjs.org/-/package/" + pkg.Replace("/", "%2f") + "/dist-tags";
            var text = FetchText(url);
            if (text == null) return string.Empty;
            try
            {
                var obj = JObject.Parse(text);
                var t = obj["latest"];
                return t?.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>Latest version from a GitHub repo's latest release (release name's semver preferred over tag_name).</summary>
        private static string GithubLatest(string repo)
        {
            var url = "https://api.github.com/repos/" + repo + "/releases/latest";
            var text = FetchText(url, "application/vnd.github+json");
            if (text == null) return string.Empty;
            try
            {
                var obj = JObject.Parse(text);
                foreach (var key in new[] { "name", "tag_name" })
                {
                    var raw = obj[key]?.ToString();
                    if (string.IsNullOrEmpty(raw)) continue;
                    var cleaned = raw.StartsWith("v") ? raw.Substring(1) : raw;
                    var v = SemverOf(cleaned);
                    if (v != null && v[0] < 1000) return string.Join(".", v);
                }
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>Latest version from PyPI's JSON API.</summary>
        private static string PypiLatest(string pkg)
        {
            var url = "https://pypi.org/pypi/" + pkg + "/json";
            var text = FetchText(url);
            if (text == null) return string.Empty;
            try
            {
                var obj = JObject.Parse(text);
                var v = obj["info"]?["version"]?.ToString();
                return v ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>Latest version from the Homebrew formula API (no auth, unlike the GitHub API rate limit).</summary>
        private static string BrewLatestFromApi(string formula)
        {
            var url = "https://formulae.brew.sh/api/formula/" + formula + ".json";
            var text = FetchText(url);
            if (text == null) return string.Empty;
            try
            {
                var obj = JObject.Parse(text);
                var v = obj["versions"]?["stable"]?.ToString();
                return v ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>Latest version scraped from a web page that has no structured API. Returns the first capture
        ///  group (or the whole match). Used for date-versioned tools whose install page is server-rendered.</summary>
        private static string WebLatest(WebSource src)
        {
            var html = FetchText(src.Url);
            if (html == null) return string.Empty;
            try
            {
                var m = Regex.Match(html, src.Pattern, RegexOptions.Multiline);
                if (m.Success) return m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
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

        /// <summary>Escape a single-quoted shell argument (the <c>replace("'", "'\\''")</c> idiom).</summary>
        private static string Escape(string s) => (s ?? string.Empty).Replace("'", "'\\''");
    }
}
