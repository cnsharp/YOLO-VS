using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Makes <c>Class.member</c> / <c>Class#member</c> references clickable, navigating to the specific
    /// method/field rather than just the enclosing type. Faithful port of IntelliJ <c>MemberLinkFilter</c>.
    /// Examples: <c>com.foo.Bar.baz</c>, <c>Bar#findById</c>, <c>UserRepository.Save</c>,
    /// <c>MyApp.Services.UserService.SomeMethod</c> (C#), <c>http.Client.Get</c> (Go).
    /// <para>
    /// The class part is gated by <see cref="YoloProjectTypes"/> exactly as in <see cref="TypeLinkFilter"/>, so
    /// a shortcut notation like <c>Ctrl/C</c> — where <c>Ctrl</c> is not a type — is not linked. If the member
    /// cannot be pinned down at click time, <see cref="YoloLinkNavigator"/> falls back to the type declaration.
    /// </para>
    /// </summary>
    internal sealed class MemberLinkFilter
    {
        public List<LinkMatch>? Apply(string text, YoloProjectTypes.Snapshot types)
        {
            if (string.IsNullOrWhiteSpace(text) || YoloLinkPatterns.IsDiffLine(text)) return null;

            var items = new List<LinkMatch>();
            int guard = 0;
            foreach (Match m in YoloLinkPatterns.MemberRefPattern.Matches(text))
            {
                if (guard++ >= YoloLinkPatterns.MaxMatchesPerLine) break;

                var classGroup = m.Groups["class"];
                var memberGroup = m.Groups["member"];
                if (!classGroup.Success || !memberGroup.Success) continue;
                string classRef = classGroup.Value;
                string member = memberGroup.Value;

                if (!types.ContainsSimple(YoloLinkPatterns.LastTypeNameSegment(classRef))) continue;

                items.Add(new LinkMatch
                {
                    Start = m.Index,
                    End = m.Index + m.Length,
                    Target = new LinkTarget
                    {
                        Kind = LinkKind.Member,
                        Raw = classRef + "." + member,
                        TypeName = classRef,
                        MemberName = member
                    }
                });
            }
            return items.Count == 0 ? null : items;
        }
    }
}
