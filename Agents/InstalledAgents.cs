using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

// The background rescan intentionally uses Task.Run + Dispatcher.BeginInvoke as fire-and-forget
// marshaling; the vs-threading analyzers don't track this pattern, so suppress them here (the
// same convention used by the terminal files).
#pragma warning disable VSTHRD001 // Use SwitchToMainThreadAsync instead of Dispatcher
#pragma warning disable VSTHRD110 // Observe awaitable result (fire-and-forget is intentional)

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Resolved agent configuration used by the tool window. Field-for-field compatible
    /// with the options page's merged agent table (known + custom), so the two surfaces
    /// always agree on command / base args / skip flag / resume flag.
    /// </summary>
    public class AgentConfig
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string BaseArgs { get; set; } = string.Empty;
        public string? SkipFlag { get; set; }
        public string? ResumeFlag { get; set; }
        public string? IconPath { get; set; }

        /// <summary>Whether the agent executable can be found / executed.</summary>
        public bool IsInstalled() => AgentDetector.CanExecute(Command);
    }

    /// <summary>
    /// Single source of truth for the tool window's agent list and for the persisted
    /// installed-agent detection cache (mirrors IDEA's <c>InstalledAgents</c> +
    /// <c>AgentExtenderSettings.installedCommands</c>).
    ///
    /// The merged list is built from:
    ///   • built-in/known agents — <see cref="AgentRegistry.All"/> (from agents.json);
    ///   • user-added tools — <see cref="YoloSettings.CustomTools"/>;
    /// and each agent's skip / resume flag is taken from settings
    /// (<see cref="YoloSettings.PermissionRules"/> / <see cref="YoloSettings.ResumeRules"/>,
    /// keyed by command base name), falling back to <see cref="AgentRegistry"/>.
    ///
    /// Detection results are cached in <see cref="YoloSettings.CachedInstalledCommands"/>
    /// and refreshed by a background rescan that only persists/notify when the set changes.
    /// </summary>
    public class InstalledAgents
    {
        // ── Static persisted detection cache ────────────────────────

        private static readonly object _cacheLock = new object();
        private static HashSet<string> _cachedCommands = LoadCache();
        private static bool _scanKicked;

        private static HashSet<string> LoadCache()
        {
            var list = YoloSettings.Instance.CachedInstalledCommands;
            return new HashSet<string>(list ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Lower-cased command base names currently believed installed (cheap, any thread).</summary>
        public static IReadOnlyCollection<string> CachedCommands => _cachedCommands;

        /// <summary>
        /// Raised on the UI thread whenever a background rescan detects that the installed-agent
        /// set actually changed. Both the options page and the tool window subscribe so their UI
        /// refreshes from the cache without a manual re-open (IDEA's "notify only on change").
        /// </summary>
        public static event Action? CacheChanged;

        /// <summary>Whether the command's base name is in the cached installed set.</summary>
        public static bool IsInstalled(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            var baseCmd = ExecutableNames.BaseName(command);
            lock (_cacheLock) return _cachedCommands.Contains(baseCmd);
        }

        private static IEnumerable<string> AllAgentCommands()
        {
            var cmds = new List<string>();
            foreach (var a in AgentRegistry.All) cmds.Add(a.Command);
            foreach (var t in YoloSettings.Instance.CustomTools)
                if (!string.IsNullOrWhiteSpace(t.Command)) cmds.Add(t.Command);
            return cmds;
        }

        private static HashSet<string> ComputeDetected()
        {
            var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in AllAgentCommands())
            {
                var baseCmd = ExecutableNames.BaseName(c);
                if (detected.Contains(baseCmd)) continue;
                if (AgentDetector.CanExecute(c)) detected.Add(baseCmd);
            }
            return detected;
        }

        private static void Persist(HashSet<string> detected)
        {
            var settings = YoloSettings.Instance;
            settings.CachedInstalledCommands = detected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            settings.Save();
        }

        /// <summary>
        /// Synchronous scan (used the first time the cache is needed, always off the UI thread).
        /// Populates the cache and persists it.
        /// </summary>
        private static void ScanNow()
        {
            var detected = ComputeDetected();
            lock (_cacheLock)
            {
                if (!detected.SetEquals(_cachedCommands))
                {
                    _cachedCommands = detected;
                    Persist(detected);
                }
            }
        }

        /// <summary>
        /// Background rescan of every known/custom agent command. Updates the persisted cache
        /// only when the detected set actually changed, then invokes <paramref name="onChanged"/>
        /// on the UI thread with the new set (IDEA's "notify only on change" behaviour).
        /// </summary>
        public static void RescanCache(Action<HashSet<string>>? onChanged = null)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                var detected = ComputeDetected();
                bool changed;
                lock (_cacheLock)
                {
                    changed = !detected.SetEquals(_cachedCommands);
                    if (changed)
                    {
                        _cachedCommands = detected;
                        Persist(detected);
                    }
                }
                if (!changed) return;

                var dt = Application.Current?.Dispatcher;
                if (dt != null && !dt.CheckAccess())
                    dt.BeginInvoke(new Action(() =>
                    {
                        onChanged?.Invoke(detected);
                        CacheChanged?.Invoke();
                    }));
                else
                {
                    onChanged?.Invoke(detected);
                    CacheChanged?.Invoke();
                }
            });
        }

        // ── Instance: merged agent list for the tool window ─────────

        private List<AgentConfig> _knownAgents = new List<AgentConfig>();
        private bool _isScanned;

        /// <summary>Returns only the agents that are actually installed / runnable (cache-based).</summary>
        public List<AgentConfig> GetInstalledAgents()
        {
            if (!_isScanned)
                ScanAgents();
            return _knownAgents.Where(a => IsInstalled(a.Command)).ToList();
        }

        private void ScanAgents()
        {
            // Make sure we have a cache before filtering; the first scan is synchronous.
            if (!_scanKicked)
            {
                _scanKicked = true;
                if (_cachedCommands.Count == 0)
                    ScanNow();
            }
            _knownAgents = BuildAgentList();
            _isScanned = true;
            // Also kick a background refresh so a freshly-installed tool shows up without restart.
            RescanCache(_ => { });
        }

        /// <summary>
        /// Builds the merged agent list from the agent registry + user custom tools, resolving
        /// every skip / resume flag from settings then the registry.
        /// </summary>
        private static List<AgentConfig> BuildAgentList()
        {
            var settings = YoloSettings.Instance;

            var skipByBase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in settings.PermissionRules)
                if (!string.IsNullOrWhiteSpace(r.AgentId))
                    skipByBase[ExecutableNames.BaseName(r.AgentId)] = r.Flag ?? string.Empty;

            var resumeByBase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in settings.ResumeRules)
                if (!string.IsNullOrWhiteSpace(r.AgentId))
                    resumeByBase[ExecutableNames.BaseName(r.AgentId)] = r.Flag ?? string.Empty;

            var list = new List<AgentConfig>();

            // ① Built-in / known agents (command + id are fixed; skip/resume flag editable).
            foreach (var def in AgentRegistry.All)
            {
                var baseCmd = ExecutableNames.BaseName(def.Command);
                var skip = skipByBase.TryGetValue(baseCmd, out var s) && !string.IsNullOrWhiteSpace(s)
                    ? s
                    : def.SkipFlag;
                var resume = resumeByBase.TryGetValue(baseCmd, out var r) && !string.IsNullOrWhiteSpace(r)
                    ? r
                    : def.ResumeFlag;
                list.Add(new AgentConfig
                {
                    Name = def.Id,
                    DisplayName = def.DisplayName,
                    Command = def.Command,
                    BaseArgs = string.Empty,
                    SkipFlag = skip,
                    ResumeFlag = resume,
                    IconPath = def.Icon
                });
            }

            // ② User-added custom tools (fully configurable).
            foreach (var tool in settings.CustomTools)
            {
                if (string.IsNullOrWhiteSpace(tool.Id) || string.IsNullOrWhiteSpace(tool.Command))
                    continue;
                var baseCmd = ExecutableNames.BaseName(tool.Command);
                var skip = skipByBase.TryGetValue(baseCmd, out var s) ? s : string.Empty;
                var resume = resumeByBase.TryGetValue(baseCmd, out var r) ? r : string.Empty;
                list.Add(new AgentConfig
                {
                    Name = tool.Id,
                    DisplayName = string.IsNullOrWhiteSpace(tool.DisplayName) ? tool.Id : tool.DisplayName,
                    Command = tool.Command,
                    BaseArgs = tool.BaseArgs,
                    SkipFlag = skip,
                    ResumeFlag = resume,
                    IconPath = tool.IconPath
                });
            }

            return list;
        }

        /// <summary>Forces a re-scan on next access (e.g. after settings changed).</summary>
        public void RescanAgents()
        {
            _isScanned = false;
            ScanAgents();
        }

        /// <summary>Persists a new custom tool (and its flags) into settings.</summary>
        public void AddAgent(AgentConfig agent)
        {
            if (agent == null) return;
            var settings = YoloSettings.Instance;

            if (AgentRegistry.ById(agent.Name) == null)
            {
                settings.CustomTools.Add(new CustomTool
                {
                    Id = agent.Name,
                    DisplayName = agent.DisplayName,
                    Command = agent.Command,
                    BaseArgs = agent.BaseArgs,
                    IconPath = agent.IconPath ?? string.Empty
                });
            }

            if (!string.IsNullOrWhiteSpace(agent.SkipFlag))
            {
                var key = ExecutableNames.BaseName(agent.Command);
                settings.PermissionRules.RemoveAll(r =>
                    ExecutableNames.BaseName(r.AgentId).Equals(key, StringComparison.OrdinalIgnoreCase));
                settings.PermissionRules.Add(new PermissionRule { AgentId = key, Flag = agent.SkipFlag ?? string.Empty });
            }

            if (!string.IsNullOrWhiteSpace(agent.ResumeFlag))
            {
                var key = ExecutableNames.BaseName(agent.Command);
                settings.ResumeRules.RemoveAll(r =>
                    ExecutableNames.BaseName(r.AgentId).Equals(key, StringComparison.OrdinalIgnoreCase));
                settings.ResumeRules.Add(new PermissionRule { AgentId = key, Flag = agent.ResumeFlag ?? string.Empty });
            }

            settings.Save();
            _isScanned = false;
        }

        /// <summary>Removes a custom tool from settings (known agents cannot be removed).</summary>
        public void RemoveAgent(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var settings = YoloSettings.Instance;
            settings.CustomTools.RemoveAll(t => t.Id.Equals(name, StringComparison.OrdinalIgnoreCase));
            settings.Save();
            _isScanned = false;
        }

        /// <summary>Looks up an agent by id (case-insensitive), installed or not.</summary>
        public AgentConfig? GetAgent(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (!_isScanned)
                ScanAgents();
            return _knownAgents.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
