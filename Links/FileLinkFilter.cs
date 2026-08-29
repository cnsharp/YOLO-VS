using System.Text.RegularExpressions;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// File path link filter, corresponds to the IntelliJ version FileLinkFilter.kt
    /// </summary>
    public class FileLinkFilter
    {
        private readonly Regex _pathRegex = new Regex(
            @"(\b(?:(?:[a-zA-Z]:|\\)(?:[/\\][\w.\-]+)+|\.?\.?[/\\][\w.\-]+)+)\:(\d+)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        public YoloHyperlink? TryMatch(string text)
        {
            var match = _pathRegex.Match(text);
            if (match.Success)
            {
                var filePath = match.Groups[1].Value;
                var lineNumber = int.Parse(match.Groups[2].Value);
                
                return new YoloHyperlink
                {
                    Text = text,
                    FilePath = filePath,
                    LineNumber = lineNumber
                };
            }
            
            return null;
        }
    }
}
