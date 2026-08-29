using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// One agent's metadata, deserialized from the embedded <c>agents.json</c> resource.
    /// This is the single source of truth for built-in agent data — it replaces the
    /// previously hardcoded <c>KnownAgents</c> / <c>DefaultSkipFlags</c> /
    /// <c>DefaultSkipEnvs</c> / <c>AgentIcons</c> data, so agent metadata is maintained
    /// in a data file (separated from the program), mirroring the IntelliJ plugin's
    /// <c>AgentRegistry.kt</c>.
    /// </summary>
    public sealed class AgentDef
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string SkipFlag { get; set; } = string.Empty;
        public string ResumeFlag { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public SkipEnvDef? SkipEnv { get; set; }
    }

    /// <summary>Optional environment variable injected at launch (for agents without a skip flag).</summary>
    public sealed class SkipEnvDef
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
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

        /// <summary>Lookup by command base name first, then by id — mirrors the old skip-flag resolution.</summary>
        public static AgentDef? Lookup(string key) => ByCommand(key) ?? ById(key);

        public static string SkipFlagFor(string key) => Lookup(key)?.SkipFlag ?? string.Empty;

        public static string ResumeFlagFor(string key) => Lookup(key)?.ResumeFlag ?? string.Empty;

        /// <summary>Icon path for the agent id, or empty string if unset/unknown.</summary>
        public static string IconFor(string id) => ById(id)?.Icon ?? string.Empty;

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
