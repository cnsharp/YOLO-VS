namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Hyperlink model, corresponds to the IntelliJ version YoloHyperlink.kt
    /// </summary>
    public class YoloHyperlink
    {
        /// <summary>
        /// Display text
        /// </summary>
        public string Text { get; set; } = string.Empty;
        
        /// <summary>
        /// File path
        /// </summary>
        public string? FilePath { get; set; }
        
        /// <summary>
        /// Line number (1-based)
        /// </summary>
        public int LineNumber { get; set; }
        
        /// <summary>
        /// Column number (1-based, optional)
        /// </summary>
        public int ColumnNumber { get; set; }
        
        /// <summary>
        /// Method name (stack frame link)
        /// </summary>
        public string? MethodName { get; set; }
        
        /// <summary>
        /// Member name (member link)
        /// </summary>
        public string? MemberName { get; set; }
        
        /// <summary>
        /// Type name (Phase 2)
        /// </summary>
        public string? TypeName { get; set; }
        
        /// <summary>
        /// URL (Phase 2)
        /// </summary>
        public string? Url { get; set; }
        
        /// <summary>
        /// Link type
        /// </summary>
        public enum LinkType
        {
            File,
            Type,
            Member,
            Url
        }
        
        public LinkType Type => Url != null ? LinkType.Url :
                                 TypeName != null ? LinkType.Type :
                                 LinkType.File;
        
        /// <summary>
        /// Check whether this is a valid file link
        /// </summary>
        public bool IsValid()
        {
            return !string.IsNullOrEmpty(FilePath) || 
                   !string.IsNullOrEmpty(Url) ||
                   !string.IsNullOrEmpty(TypeName);
        }
    }
}
