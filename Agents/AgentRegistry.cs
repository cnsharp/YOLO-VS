using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Newtonsoft.Json;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// One agent's metadata, deserialized from the embedded <c>agents.json</c> resource.
    /// This is the single source of truth for built-in agent data — it replaces the
    /// previously hardcoded <c>KnownAgents</c> / <c>DefaultSkipFlags</c> /
    /// <c>DefaultSkipEnvs</c> / <c>AgentIcons</c> data, so agent metadata is maintained
    /// in a data file (separated from the program) rather than hardcoded in code.
    /// </summary>
    public sealed class AgentDef
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string SkipFlag { get; set; } = string.Empty;
        public string ResumeFlag { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public SkipEnvDef? SkipEnv { get; set; }
        public InstallSpec? Install { get; set; }
    }

    /// <summary>Optional environment variable injected at launch (for agents without a skip flag).</summary>
    public sealed class SkipEnvDef
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// How an agent is installed. <c>Type</c> is one of: npm | pip | brew | shell | url.
    /// <list type="bullet">
    ///   <item>npm/pip/brew: install via the named package manager (<c>Pkg</c>).</item>
    ///   <item>shell: run <c>Cmd</c> verbatim through the user's login shell (curl/irm installers, …).
    ///     <c>CmdWin</c> is the Windows-only variant (e.g. a PowerShell one-liner), used on Windows.
    ///     <c>UpdateCmd</c>, when set, is the agent's own self-update subcommand (e.g. <c>claude update</c>);
    ///     the Update button runs it directly instead of re-running the installer.</item>
    ///   <item>url: no automatable package; the Install button opens <c>Url</c> in the browser instead.</item>
    /// </list>
    /// Describes how an agent is installed.
    /// </summary>
    public sealed class InstallSpec
    {
        public string Type { get; set; } = string.Empty;
        public string Pkg { get; set; } = string.Empty;
        public string Cmd { get; set; } = string.Empty;
        public string CmdWin { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string UpdateCmd { get; set; } = string.Empty;

        /// <summary>Whether the plugin can run this install head-less (everything except <c>url</c>).
        /// Data-model field (not platform-aware): whether the plugin can run this install head-less.</summary>
        public bool Automatable =>
            Type is "npm" or "pip" or "brew" or "shell";

        /// <summary>
        /// Whether this install can actually be run on the current OS. Same as <see cref="Automatable"/>
        /// except <c>brew</c> is <b>not</b> automatable on native Windows — Homebrew has no Windows
        /// port (it only runs under WSL2/Linux), so on Windows a <c>brew</c> spec falls back to opening
        /// the agent's site (MANUAL) instead of running a command that would always fail.
        /// </summary>
        public bool AutomatableOnPlatform
        {
            get
            {
                if (!Automatable) return false;
                if (Type == "brew" && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return false;
                return true;
            }
        }

        /// <summary>Human-readable install target shown in the status line, e.g. the package or command.</summary>
        public string Target
        {
            get
            {
                switch (Type)
                {
                    case "npm":
                    case "pip":
                    case "brew":
                        return Pkg;
                    case "shell":
                        return (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && CmdWin.Length > 0) ? CmdWin : Cmd;
                    default:
                        return Url;
                }
            }
        }

        /// <summary>True when the Update button can perform a real upgrade (package-manager spec, or a shell
        ///  agent that declares its own update subcommand).</summary>
        public bool HasUpdateCommand =>
            Type is "npm" or "pip" or "brew" || UpdateCmd.Length > 0;
    }

    /// <summary>
    /// Loads agent metadata from the embedded <c>agents.json</c> resource and exposes
    /// lookup helpers. Thread-safe: loaded once, immutable thereafter.
    /// </summary>
    public static class AgentRegistry
    {
        // Manifest resource name set explicitly in Yolo.csproj via LogicalName.
        private const string ResourceName = "CnSharp.VSIX.Yolo.agents.json";

        /// <summary>All built-in agents, in the order declared in agents.json.</summary>
        public static IReadOnlyList<AgentDef> All { get; } = Load();

        private static readonly Dictionary<string, AgentDef> _byId =
            All.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, AgentDef> _byCommand =
            All.ToDictionary(a => ExecutableNames.BaseName(a.Command), StringComparer.OrdinalIgnoreCase);

        /// <summary>Lookup by id (case-insensitive).</summary>
        public static AgentDef? ById(string id) =>
            id != null && _byId.TryGetValue(id, out var a) ? a : null;

        /// <summary>Lookup by command base name (case-insensitive).</summary>
        public static AgentDef? ByCommand(string command) =>
            command != null && _byCommand.TryGetValue(ExecutableNames.BaseName(command), out var a) ? a : null;

        /// <summary>Lookup by command base name first, then by id — matching how skip-flag resolution is keyed.</summary>
        public static AgentDef? Lookup(string key) => ByCommand(key) ?? ById(key);

        public static string SkipFlagFor(string key) => Lookup(key)?.SkipFlag ?? string.Empty;

        public static string ResumeFlagFor(string key) => Lookup(key)?.ResumeFlag ?? string.Empty;

        /// <summary>Icon path for the agent id, or empty string if unset/unknown.</summary>
        public static string IconFor(string id) => ById(id)?.Icon ?? string.Empty;

        /// <summary>Home/download URL for the agent (keyed by command or id), or empty string if unset/unknown.</summary>
        public static string UrlFor(string key) => Lookup(key)?.Url ?? string.Empty;

        /// <summary>The install specification for the agent, or null if it declares none (custom tools, unknown).</summary>
        public static InstallSpec? InstallFor(string key) => Lookup(key)?.Install;

        /// <summary>
        /// Optional launch-time environment variable for the agent, or null if it has none.
        /// Keyed by command base name or id.
        /// </summary>
        public static IReadOnlyDictionary<string, string>? SkipEnvFor(string key)
        {
            var def = Lookup(key);
            if (def?.SkipEnv == null || string.IsNullOrEmpty(def.SkipEnv.Name))
                return null;
            return new Dictionary<string, string> { [def.SkipEnv.Name] = def.SkipEnv.Value };
        }

        private static IReadOnlyList<AgentDef> Load()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(ResourceName);
                if (stream == null)
                    return Array.Empty<AgentDef>();

                using var reader = new StreamReader(stream);
                var list = JsonConvert.DeserializeObject<List<AgentDef>>(reader.ReadToEnd());
                return list ?? (IReadOnlyList<AgentDef>)Array.Empty<AgentDef>();
            }
            catch
            {
                return Array.Empty<AgentDef>();
            }
        }
    }
}
