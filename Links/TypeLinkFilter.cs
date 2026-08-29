using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Makes type references printed by agents clickable: qualified names (<c>com.foo.Bar</c>,
    /// <c>MyApp.Services.UserService</c>, <c>foo::Bar</c>, <c>\App\Models\User</c>) and simple PascalCase
    /// names (<c>Bar</c>). Faithful port of IntelliJ <c>TypeLinkFilter</c>.
    /// <para>
    /// A name is only linked when it is gated by <see cref="YoloProjectTypes"/> — a qualified reference by its
    /// trailing segment, a simple reference by the name itself. This gate is not optional: without it the
    /// pattern matches every capitalized word (<c>Result</c>, <c>Error</c>, the <c>Ctrl</c> in <c>Ctrl/C</c>)
    /// and paints it as a link.
    /// </para>
    /// <para>
    /// IntelliJ additionally has a "file-name fallback" branch for a simple name that is a source-file base
    /// name but not a class (e.g. a Kotlin file facade). That branch is unreachable here by construction: this
    /// port's type gate <i>is</i> the source-file base-name set, so any name reaching the fallback has already
    /// matched the type branch, and <see cref="YoloLinkNavigator"/> resolves both the same way (open the file
    /// whose base name matches). Emitting one link kind instead of duplicating the branch keeps the two paths
    /// from drifting.
    /// </para>
    /// </summary>
    internal sealed class TypeLinkFilter
    {
        public List<LinkMatch>? Apply(string text, YoloProjectTypes.Snapshot types)
        {
            if (string.IsNullOrWhiteSpace(text) || YoloLinkPatterns.IsDiffLine(text)) return null;

            var items = new List<LinkMatch>();
            int guard = 0;
            foreach (Match m in YoloLinkPatterns.TypeNamePattern.Matches(text))
            {
                if (guard++ >= YoloLinkPatterns.MaxMatchesPerLine) break;

                var qualified = m.Groups["qualified"];
                var simple = m.Groups["simple"];
                string? name = qualified.Success ? qualified.Value : simple.Success ? simple.Value : null;
                if (name == null) continue;

                // Gate on the trailing segment: for a simple name that is the name itself, and for a
                // qualified one it avoids materializing the full qualified set (resolution stays on click).
                if (!types.ContainsSimple(YoloLinkPatterns.LastTypeNameSegment(name))) continue;

                items.Add(new LinkMatch
                {
                    Start = m.Index,
                    End = m.Index + m.Length,
                    Target = new LinkTarget
                    {
                        Kind = LinkKind.Type,
                        Raw = name,
                        TypeName = name
                    }
                });
            }
            return items.Count == 0 ? null : items;
        }
    }
}
