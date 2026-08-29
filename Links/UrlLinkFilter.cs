using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Makes <c>http(s)://</c> URLs printed by agents clickable, opening them in the system browser.
    /// Faithful port of IntelliJ <c>UrlLinkFilter</c> (this does NOT hide the YOLO pane, since a URL
    /// opens an external browser rather than the IDE editor).
    /// </summary>
    internal sealed class UrlLinkFilter
    {
        public List<LinkMatch>? Apply(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || YoloLinkPatterns.IsDiffLine(text)) return null;

            var items = new List<LinkMatch>();
            var matches = YoloLinkPatterns.UrlPattern.Matches(text);
            int guard = 0;
            foreach (Match m in matches)
            {
                if (guard++ >= YoloLinkPatterns.MaxMatchesPerLine) break;
                items.Add(new LinkMatch
                {
                    Start = m.Groups[0].Index,
                    End = m.Groups[0].Index + m.Groups[0].Length,
                    Target = new LinkTarget
                    {
                        Kind = LinkKind.Url,
                        Raw = m.Groups[0].Value,
                        Url = m.Groups[0].Value
                    }
                });
            }
            return items.Count == 0 ? null : items;
        }
    }
}
