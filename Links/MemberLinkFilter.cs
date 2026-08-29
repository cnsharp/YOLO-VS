using System.Text.RegularExpressions;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Member link filter (Phase 2)
    /// Corresponds to the IntelliJ version MemberLinkFilter.kt
    /// </summary>
    public class MemberLinkFilter
    {
        private readonly Regex _memberRegex = new Regex(
            @"(\b[A-Z][\w\.]*\.[a-z][\w]*\b)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        public YoloHyperlink? TryMatch(string text)
        {
            var match = _memberRegex.Match(text);
            if (match.Success)
            {
                var memberName = match.Groups[1].Value;
                
                return new YoloHyperlink
                {
                    Text = text,
                    MemberName = memberName
                };
            }
            
            return null;
        }
    }
}
