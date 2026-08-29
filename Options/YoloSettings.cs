using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Persistent settings, corresponding to the IntelliJ version AgentExtenderSettings.kt
    /// Settings are saved as an XML file in the local AppData directory, avoiding dependency on the project-level Settings designer.
    /// </summary>
    [XmlRoot("YoloSettings")]
    public class YoloSettings
    {
        private static YoloSettings? _instance;
        private static readonly object _lock = new object();
        // Serializes Save() so a background agent-rescan and a UI-thread toggle cannot serialize
        // the same object concurrently (XmlSerializer over a mutating object can throw or emit
        // a truncated document).
        private static readonly object _saveLock = new object();
        private static readonly string SettingsPath =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YoloVS",
                "settings.xml");

        // Singleton
        public static YoloSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = Load() ?? new YoloSettings();
                        }
                    }
                }
                return _instance;
            }
        }

        // Settings items
        /// <summary>Global Y (skip-permissions) toggle, persisted across sessions (mirrors IDEA's skipEnabled).</summary>
        public bool SkipEnabled { get; set; }

        /// <summary>Global R (resume) toggle, persisted across sessions (mirrors IDEA's resumeEnabled).</summary>
        public bool ResumeEnabled { get; set; }

        /// <summary>ID of the last agent the user launched; restored as the dropdown default on the next session.</summary>
        public string? LastAgent { get; set; }

        public bool AutoRefreshAgents { get; set; } = true;

        /// <summary>
        /// Per-agent skip-permission flags. Keyed by the agent's command base name (e.g. "claude").
        /// Whether a flag is injected is decided by the global YOLO (skip) toggle.
        /// </summary>
        [XmlArray("PermissionRules"), XmlArrayItem("Rule")]
        public List<PermissionRule> PermissionRules { get; set; } = new List<PermissionRule>();

        /// <summary>
        /// Per-agent resume flags (mirrors IDEA's resumeRules). Keyed by command base name.
        /// Applied when the global R (resume) toggle is on.
        /// </summary>
        [XmlArray("ResumeRules"), XmlArrayItem("Rule")]
        public List<PermissionRule> ResumeRules { get; set; } = new List<PermissionRule>();

        /// <summary>User-added custom tools (known agents are seeded separately, not stored here).</summary>
        [XmlArray("CustomTools"), XmlArrayItem("Tool")]
        public List<CustomTool> CustomTools { get; set; } = new List<CustomTool>();

        /// <summary>
        /// Persisted install-state cache: lower-cased command base names that resolved on PATH
        /// (or via execution probe) during the last scan. Mirrors IDEA's installedCommands so the
        /// UI can render instantly and a background rescan only updates when the set actually changes.
        /// </summary>
        [XmlArray("CachedInstalledCommands"), XmlArrayItem("Cmd")]
        public List<string> CachedInstalledCommands { get; set; } = new List<string>();

        /// <summary>
        /// Load settings from disk
        /// </summary>
        public static YoloSettings? Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return null;

                var serializer = new XmlSerializer(typeof(YoloSettings));
                using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read);
                if (serializer.Deserialize(stream) is YoloSettings settings)
                {
                    return settings;
                }
            }
            catch
            {
                // On parse failure return null; caller creates the default instance
            }

            return null;
        }

        /// <summary>
        /// Save settings to disk
        /// </summary>
        public void Save()
        {
            // Hold _saveLock for the whole serialization so the background rescan
            // (InstalledAgents.Persist) and UI toggles cannot write this object at once.
            lock (_saveLock)
            {
                try
                {
                    var directory = Path.GetDirectoryName(SettingsPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    var serializer = new XmlSerializer(typeof(YoloSettings));
                    using var stream = new FileStream(SettingsPath, FileMode.Create, FileAccess.Write);
                    serializer.Serialize(stream, this);
                }
                catch
                {
                    // Save failure does not affect the main flow
                }
            }
        }

        /// <summary>
        /// Reset to default values
        /// </summary>
        public void Reset()
        {
            AutoRefreshAgents = true;
            Save();
        }
    }
}
