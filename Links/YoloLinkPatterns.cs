using System.Text.RegularExpressions;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Terminal output link pattern matcher
    /// Corresponds to the IntelliJ version YoloLinkPatterns.kt
    /// </summary>
    public static class YoloLinkPatterns
    {
        // File path pattern
        // Match: path/to/file.cs:123, C:\path\file.cs:123, /path/file.cs:123
        public static readonly Regex PathPattern = new Regex(
            @"(\b(?:(?:[a-zA-Z]:|\\)(?:[/\\][\w.\-]+)+|\.?\.?[/\\][\w.\-]+)+)\:(\d+)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        // Stack frame pattern
        // Match: at Project.Class.Method(file.cs:123)
        public static readonly Regex StackFramePattern = new Regex(
            @"\bat\s+([\w\.]+)\(([^)]+\:\d+)\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        // Method invocation pattern
        // Match: Method() at file.cs:123
        public static readonly Regex MethodInvocationPattern = new Regex(
            @"([\w\.]+)\(\)\s+at\s+([^\s\(:]+)\:(\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        // URL pattern (Phase 2)
        public static readonly Regex UrlPattern = new Regex(
            @"https?://[^\s]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        /// <summary>
        /// Check whether the text contains a file link
        /// </summary>
        public static bool HasFileLink(string text)
        {
            return PathPattern.IsMatch(text) || StackFramePattern.IsMatch(text);
        }
        
        /// <summary>
        /// Extract file link
        /// </summary>
        public static YoloHyperlink? ExtractFileLink(string text)
        {
            // Try matching the path pattern
            var pathMatch = PathPattern.Match(text);
            if (pathMatch.Success)
            {
                var filePath = pathMatch.Groups[1].Value;
                var lineNumber = int.Parse(pathMatch.Groups[2].Value);
                
                return new YoloHyperlink
                {
                    Text = text,
                    FilePath = filePath,
                    LineNumber = lineNumber,
                    ColumnNumber = 0
                };
            }
            
            // Try matching the stack frame pattern
            var stackMatch = StackFramePattern.Match(text);
            if (stackMatch.Success)
            {
                var methodName = stackMatch.Groups[1].Value;
                var location = stackMatch.Groups[2].Value;
                
                // Extract file path and line number from location
                var locationMatch = PathPattern.Match(location);
                if (locationMatch.Success)
                {
                    return new YoloHyperlink
                    {
                        Text = text,
                        FilePath = locationMatch.Groups[1].Value,
                        LineNumber = int.Parse(locationMatch.Groups[2].Value),
                        MethodName = methodName
                    };
                }
            }
            
            return null;
        }
    }
}
