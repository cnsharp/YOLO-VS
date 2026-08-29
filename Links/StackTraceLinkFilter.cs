using System.Text.RegularExpressions;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Stack frame link filter, corresponds to the IntelliJ version StackTraceLinkFilter.kt
    /// </summary>
    public class StackTraceLinkFilter
    {
        private readonly Regex _stackFrameRegex = new Regex(
            @"\bat\s+([\w\.]+)\(([^)]+\:\d+)\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        public YoloHyperlink? TryMatch(string text)
        {
            var match = _stackFrameRegex.Match(text);
            if (match.Success)
            {
                var methodName = match.Groups[1].Value;
                var location = match.Groups[2].Value;
                
                // Extract file path and line number from location
                var pathMatch = new Regex(@"(\w+[\w.\-]*\.\w+)\:(\d+)").Match(location);
                if (pathMatch.Success)
                {
                    return new YoloHyperlink
                    {
                        Text = text,
                        FilePath = pathMatch.Groups[1].Value,
                        LineNumber = int.Parse(pathMatch.Groups[2].Value),
                        MethodName = methodName
                    };
                }
            }
            
            return null;
        }
    }
}
