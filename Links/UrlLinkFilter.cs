using System.Text.RegularExpressions;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// URL link filter (Phase 2)
    /// Corresponds to the IntelliJ version UrlLinkFilter.kt
    /// </summary>
    public class UrlLinkFilter
    {
        private readonly Regex _urlRegex = new Regex(
            @"(https?://[^\s]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        public YoloHyperlink? TryMatch(string text)
        {
            var match = _urlRegex.Match(text);
            if (match.Success)
            {
                return new YoloHyperlink
                {
                    Text = text,
                    Url = match.Groups[1].Value
                };
            }
            
            return null;
        }
    }
}
