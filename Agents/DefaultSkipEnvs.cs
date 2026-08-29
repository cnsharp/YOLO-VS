using System.Collections.Generic;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Default skip environment variables, sourced from <see cref="AgentRegistry"/> (the
    /// <c>skipEnv</c> field of agents.json). Corresponds to the IntelliJ version's
    /// DefaultSkipEnvs.kt.
    /// </summary>
    public static class DefaultSkipEnvs
    {
        /// <summary>
        /// Returns the launch-time environment variables for the agent (keyed by id or
        /// command base name), or null if it uses a flag instead of an env var.
        /// </summary>
        public static IReadOnlyDictionary<string, string>? Get(string agent) =>
            string.IsNullOrWhiteSpace(agent) ? null : AgentRegistry.SkipEnvFor(agent);
    }
}
