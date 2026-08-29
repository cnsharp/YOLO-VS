namespace CnSharp.VSIX.Yolo
{
    public static class Constants
    {
        public const string PackageGuid = "6f4e2c1a-8b3d-4e5f-9a7c-1d2e3f4a5b6c";
        public const string ToolWindowGuid = "7a5b3c2d-1e4f-5a6b-8c9d-0e1f2a3b4c5d";

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
