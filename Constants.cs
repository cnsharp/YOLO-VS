using System.Reflection;

namespace CnSharp.VSIX.Yolo
{
    public static class Constants
    {
        // Branded product name. Sourced from AssemblyInfo's [AssemblyTitle] so there is a single
        // source of truth; read at runtime (not a const) because attribute arguments cannot.
        public static string ProductName
        {
            get
            {
                var attr = typeof(Constants).Assembly.GetCustomAttribute<AssemblyTitleAttribute>();
                return attr?.Title ?? "Agent YOLO";
            }
        }

        public const string PackageGuid = "6f4e2c1a-8b3d-4e5f-9a7c-1d2e3f4a5b6c";
        public const string ToolWindowGuid = "7a5b3c2d-1e4f-5a6b-8c9d-0e1f2a3b4c5d";

        // ── Manage Agents document window ──────────────────────────
        // A real document-well tab (the VS analog of IDEA's editor-area Manage Agents tab),
        // hosted by a custom IVsEditorFactory that opens a synthetic, non-file-backed document.
        public const string ManageAgentsEditorGuid = "a1b2c3d4-1111-2222-3333-444455556666";
        /// <summary>Logical (RDT) moniker for the synthetic Manage Agents document — not a real file.</summary>
        public const string ManageAgentsDocName = "ManageAgents.yolo";
        public const string ManageAgentsCaption = "Manage Agents";
        /// <summary>Command id within guidYoloCmdSet that opens the Manage Agents document tab.</summary>
        public const int ManageAgentsCommandId = 0x0101;

        // Command set + icon bitmap for the "View ▸ Other Windows ▸ YOLO" menu command.
        public const string YoloCmdSetGuid = "2b5e1d4a-3c6f-4a8b-9c2d-7e1f4a5b6c7d";
        public const string YoloCommandBitmapGuid = "9c1a2b3d-4e5f-6a7b-8c9d-0e1f2a3b4c5e";
        
        // Terminal color configuration
        public const string TerminalColorPrompt = "#4ec9b0";
        public const string TerminalColorSystem = "#858585";
        public const string TerminalColorError = "#f48771";
        
        // Agent configuration
        public const string DefaultAgent = "claude";
        
        // YOLO mode configuration
        public const string YoloModeEnabled = "yolo_mode_enabled";
        public const string YoloSkipPermissions = "skip_permissions";
    }
}
