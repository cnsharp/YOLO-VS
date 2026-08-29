using System.Xml.Serialization;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// A single agent's permission-bypass rule. Only the agent's skip flag is stored here;
    /// whether the flag is actually injected is decided by the global YOLO (skip) toggle.
    /// Mirrors IntelliJ's PermissionRule.
    /// </summary>
    public class PermissionRule
    {
        [XmlAttribute("agentId")]
        public string AgentId { get; set; } = string.Empty;

        [XmlAttribute("flag")]
        public string Flag { get; set; } = string.Empty;
    }

    /// <summary>
    /// A custom tool the user adds to the agents dropdown (as opposed to the built-in / known agents).
    /// Mirrors IntelliJ's CustomTool.
    /// </summary>
    public class CustomTool
    {
        [XmlAttribute("id")]
        public string Id { get; set; } = string.Empty;

        [XmlAttribute("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [XmlAttribute("command")]
        public string Command { get; set; } = string.Empty;

        [XmlAttribute("baseArgs")]
        public string BaseArgs { get; set; } = string.Empty;

        /// <summary>
        /// Local image path or http(s) URL used as the icon. Empty means use the default bolt.
        /// </summary>
        [XmlAttribute("iconPath")]
        public string IconPath { get; set; } = string.Empty;
    }
}
