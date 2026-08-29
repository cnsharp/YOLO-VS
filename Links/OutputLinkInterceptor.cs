using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Terminal output link interceptor
    /// Intercept the terminal output stream, apply regex, generate clickable links
    /// </summary>
    public class OutputLinkInterceptor
    {
        private readonly List<Regex> _patterns = new List<Regex>
        {
            YoloLinkPatterns.PathPattern,
            YoloLinkPatterns.StackFramePattern,
            YoloLinkPatterns.MethodInvocationPattern
        };
        
        /// <summary>
        /// Analyze the text and extract links
        /// </summary>
        public List<YoloHyperlink> ExtractLinks(string text)
        {
            var links = new List<YoloHyperlink>();
            
            foreach (var pattern in _patterns)
            {
                var matches = pattern.Matches(text);
                foreach (Match match in matches)
                {
                    var hyperlink = CreateHyperlink(match, pattern);
                    if (hyperlink != null)
                    {
                        links.Add(hyperlink);
                    }
                }
            }
            
            return links;
        }
        
        /// <summary>
        /// Create a hyperlink from a match
        /// </summary>
        private YoloHyperlink? CreateHyperlink(Match match, Regex pattern)
        {
            try
            {
                if (pattern == YoloLinkPatterns.PathPattern)
                {
                    var filePath = match.Groups[1].Value;
                    var lineNumber = int.Parse(match.Groups[2].Value);
                    
                    return new YoloHyperlink
                    {
                        Text = match.Value,
                        FilePath = filePath,
                        LineNumber = lineNumber
                    };
                }
                
                if (pattern == YoloLinkPatterns.StackFramePattern)
                {
                    var methodName = match.Groups[1].Value;
                    var location = match.Groups[2].Value;
                    
                    var pathMatch = YoloLinkPatterns.PathPattern.Match(location);
                    if (pathMatch.Success)
                    {
                        return new YoloHyperlink
                        {
                            Text = match.Value,
                            FilePath = pathMatch.Groups[1].Value,
                            LineNumber = int.Parse(pathMatch.Groups[2].Value),
                            MethodName = methodName
                        };
                    }
                }
                
                if (pattern == YoloLinkPatterns.MethodInvocationPattern)
                {
                    var methodName = match.Groups[1].Value;
                    var filePath = match.Groups[2].Value;
                    var lineNumber = int.Parse(match.Groups[3].Value);
                    
                    return new YoloHyperlink
                    {
                        Text = match.Value,
                        FilePath = filePath,
                        LineNumber = lineNumber,
                        MethodName = methodName
                    };
                }
            }
            catch
            {
                // Parsing failed, ignore
            }
            
            return null;
        }
    }
}
