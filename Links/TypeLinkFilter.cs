using System.Text.RegularExpressions;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Type name link filter (Phase 2)
    /// Corresponds to the IntelliJ version TypeLinkFilter.kt
    /// </summary>
    public class TypeLinkFilter
    {
        private readonly Regex _typeRegex = new Regex(
            @"(\b[A-Z][\w\.]*\.[A-Z][\w\.]*\b)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        public YoloHyperlink? TryMatch(string text)
        {
            var match = _typeRegex.Match(text);
            if (match.Success)
            {
                var typeName = match.Groups[1].Value;
                
                return new YoloHyperlink
                {
                    Text = text,
                    TypeName = typeName
                };
            }
            
            return null;
        }
    }
}
