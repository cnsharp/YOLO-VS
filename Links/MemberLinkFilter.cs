using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Makes <c>Class.Member</c> / <c>Class#member</c> references clickable, navigating to the specific
    /// method/field rather than just the enclosing type. In C# the separator is <c>.</c> (e.g.
    /// <c>MyApp.Services.UserService.SomeMethod</c>, <c>UserRepository.Save</c>); the shared link regex also
    /// accepts the Java/Kotlin <c>#</c>. Faithful port of IntelliJ <c>MemberLinkFilter</c>, including the
    /// hard-wrap reconstruction that stitches a <c>Class</c> / <c>Class.Member</c> the terminal split across
    /// consecutive rows back into one reference (e.g. <c>OrderService.Sub</c> + <c>mitAsync</c> →
    /// <c>OrderService.SubmitAsync</c>).
    /// <para>
    /// The class part is gated by <see cref="YoloProjectTypes"/> exactly as in <see cref="TypeLinkFilter"/>, so
    /// a shortcut notation like <c>Ctrl/C</c> — where <c>Ctrl</c> is not a type — is not linked. If the member
    /// cannot be pinned down at click time, <see cref="YoloLinkNavigator"/> falls back to the type declaration.
    /// </para>
    /// </summary>
    internal sealed class MemberLinkFilter
    {
        public List<LinkMatch>? Apply(string text, YoloProjectTypes.Snapshot types, PathWrapState? wrap = null, int virtualRow = -1)
        {
            if (string.IsNullOrWhiteSpace(text) || YoloLinkPatterns.IsDiffLine(text)) return null;

            var items = new List<LinkMatch>();
            int guard = 0;

            // --- Hard-wrap reconstruction: stitch a `Class` (or `Class.Member`) split across physical rows. ---
            // (c.f. the type filter's reconstruction.) The head line left PendingMember; we prepend it, re-match
            // MemberRefPattern on the joined text, and link only the tail portion visible on THIS row. When the
            // head already carried its member part the terminal split *inside* the member name, in which case
            // the continuation must be a plain identifier remainder — a next line that is itself a complete
            // reference (`UserService.FindById`) is a NEW token and must not be glued on.
            string prefix = wrap?.PendingMember ?? string.Empty;
            bool prefixHasMember = wrap?.PendingMemberHasMember ?? false;
            if (wrap != null) { wrap.PendingMember = string.Empty; wrap.PendingMemberHasMember = false; }
            if (prefix.Length > 0)
            {
                string trimmed = text.TrimStart();
                int leading = text.Length - trimmed.Length;
                string combined = prefix + trimmed;
                foreach (Match cm in YoloLinkPatterns.MemberRefPattern.Matches(combined))
                {
                    if (guard++ >= YoloLinkPatterns.MaxMatchesPerLine) break;
                    int mStart = cm.Index;
                    int mEnd = cm.Index + cm.Length;
                    if (mStart >= prefix.Length || mEnd <= prefix.Length) continue;

                    var classGroup = cm.Groups["class"];
                    var memberGroup = cm.Groups["member"];
                    if (!classGroup.Success || !memberGroup.Success) continue;

                    if (prefixHasMember)
                    {
                        // The head already carried its `#member` part, so the wrap split inside the member name.
                        // Detect a continuation that is really a new token by the separator that follows its
                        // leading identifier.
                        int leadLen = 0;
                        while (leadLen < trimmed.Length &&
                               (char.IsLetterOrDigit(trimmed[leadLen]) || trimmed[leadLen] == '_'))
                            leadLen++;
                        string after = leadLen < trimmed.Length ? trimmed.Substring(leadLen) : string.Empty;
                        if (leadLen == 0 || after.StartsWith(".") || after.StartsWith("#")) continue;
                    }

                    string classRef = classGroup.Value;
                    string member = memberGroup.Value;
                    bool known = YoloLinkPatterns.IsQualifiedName(classRef)
                        ? types.ContainsSimple(YoloLinkPatterns.LastTypeNameSegment(classRef))
                        : types.ContainsSimple(classRef);
                    if (!known) continue;

                    int tailStart = leading;
                    int tailEnd = leading + (mEnd - prefix.Length);
                    if (tailEnd > text.Length) continue;

                    items.Add(Make(tailStart, tailEnd, classRef, member));
                    if (wrap != null)
                    {
                        // Publish the completed reference so the head row's link (created on the previous row
                        // before this wrap was known) can be upgraded to navigate to the real member.
                        wrap.ContinuationStart = tailStart;
                        wrap.ContinuationEnd = tailEnd;
                        wrap.CompletedTarget = MakeTarget(classRef, member);
                        if (virtualRow >= 0) wrap.HeadRows.Add(virtualRow);
                    }
                    break; // at most one reconstruction per row
                }
            }
            // --- End hard-wrap reconstruction ---

            int contStart = wrap?.ContinuationStart ?? -1;
            int contEnd = wrap?.ContinuationEnd ?? -1;
            bool linkedThisLine = false;
            foreach (Match m in YoloLinkPatterns.MemberRefPattern.Matches(text))
            {
                if (guard++ >= YoloLinkPatterns.MaxMatchesPerLine) break;
                // Skip a match overlapping the reconstructed continuation tail to avoid a duplicate span.
                if (contStart >= 0 && m.Index < contEnd - 1 && m.Index + m.Length > contStart) continue;

                var classGroup = m.Groups["class"];
                var memberGroup = m.Groups["member"];
                if (!classGroup.Success || !memberGroup.Success) continue;
                string classRef = classGroup.Value;
                string member = memberGroup.Value;

                // Gate the class part: a qualified ref when its trailing segment is a known project type
                // (cheap, derived from the simple-name set); a simple ref must be a known project type.
                bool known = YoloLinkPatterns.IsQualifiedName(classRef)
                    ? types.ContainsSimple(YoloLinkPatterns.LastTypeNameSegment(classRef))
                    : types.ContainsSimple(classRef);
                if (!known) continue;

                items.Add(Make(m.Index, m.Index + m.Length, classRef, member));
                linkedThisLine = true;

                // A reference reaching end-of-line may be a terminal hard-wrap that split it. Remember it so the
                // next row can reconstruct, and mark this (potential) head row for upgrade when the continuation
                // completes. (The link is still created here — a complete single-line `Class#method` must keep
                // working — and it resolves the completed member if the next line turns out to continue it.)
                if (wrap != null && m.Index + m.Length >= text.TrimEnd().Length)
                {
                    string refText = text.Substring(m.Index, m.Length);
                    wrap.PendingMember = refText;
                    wrap.PendingMemberHasMember = true;
                    if (virtualRow >= 0) wrap.HeadRows.Add(virtualRow);
                }
            }

            // Hard-wrap head detection: if a `Class` reaches end-of-line with no member part yet, remember it
            // so the next row can reconstruct (e.g. `MyApp.Services.Use` + `rService.FindById`, or a wrap that
            // split right before the member separator: `UserService.User` + `.FindById`). Skipped when this row
            // already linked a complete member reference (that case hands the reference over in the loop above).
            if (!linkedThisLine && wrap != null)
            {
                var head = YoloLinkPatterns.MemberHeadPattern.Match(text);
                if (head.Success)
                {
                    var headClass = head.Groups["class"].Value;
                    if (!string.IsNullOrEmpty(headClass))
                    {
                        wrap.PendingMember = headClass;
                        wrap.PendingMemberHasMember = false;
                    }
                }
            }

            return items.Count == 0 ? null : items;
        }

        private static LinkMatch Make(int start, int end, string classRef, string member) => new LinkMatch
        {
            Start = start,
            End = end,
            Target = MakeTarget(classRef, member)
        };

        private static LinkTarget MakeTarget(string classRef, string member) => new LinkTarget
        {
            Kind = LinkKind.Member,
            Raw = classRef + "." + member,
            TypeName = classRef,
            MemberName = member
        };
    }
}
