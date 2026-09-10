using System;
using System.Collections.Generic;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Runs the terminal link filters over a row of terminal text and returns the merged, de-overlapped set of
    /// clickable <see cref="LinkMatch"/> spans. This is the single integration point the terminal renderer and
    /// the click handler both call, so a painted link and a clicked link can never disagree.
    /// <para>
    /// Filter order mirrors IntelliJ's HyperlinkFilter registration order — file, stack-trace, type, member,
    /// URL — because the earlier filters' results suppress the later ones (see the wrapped-path continuation
    /// span, and the overlap pruning below).
    /// </para>
    /// </summary>
    internal sealed class YoloLinkFilters
    {
        private readonly FileLinkFilter _file = new FileLinkFilter();
        private readonly StackTraceLinkFilter _stack = new StackTraceLinkFilter();
        private readonly TypeLinkFilter _type = new TypeLinkFilter();
        private readonly MemberLinkFilter _member = new MemberLinkFilter();
        private readonly UrlLinkFilter _url = new UrlLinkFilter();

        private readonly Func<YoloProjectTypes.Snapshot> _types;

        /// <param name="types">
        /// Supplies the project-name gate for the type/member filters. Must not block — it is called from the
        /// render path (see <see cref="YoloProjectTypes.For"/>).
        /// </param>
        public YoloLinkFilters(Func<YoloProjectTypes.Snapshot> types)
        {
            _types = types ?? throw new ArgumentNullException(nameof(types));
        }

        /// <summary>
        /// Detect all links on a single terminal row; returns null when there are none.
        /// </summary>
        /// <param name="text">The row text.</param>
        /// <param name="previousLine">
        /// The row directly above, used to reconstruct a path the terminal hard-wrapped across the two rows.
        /// Pass null to skip wrap handling.
        /// </param>
        public List<LinkMatch>? FindLinks(string text, string? previousLine = null)
        {
            var wrap = new PathWrapState();
            if (!string.IsNullOrEmpty(previousLine))
                wrap.PendingPrefix = FileLinkFilter.ComputePendingPrefix(previousLine!);
            return FindLinks(text, wrap);
        }

        /// <summary>
        /// Detect all links on a single terminal row, threading <paramref name="wrap"/> so a path hard-wrapped
        /// across several consecutive rows reconstructs correctly. The caller owns <paramref name="wrap"/> and
        /// passes the SAME instance for each successive row, top to bottom — matching IDEA's JediTerm, which
        /// carries one mutable <c>PathWrapState</c> per session. (Our terminal re-evaluates visible rows per
        /// repaint rather than streaming lines, so the caller threads the state instead of a long-lived field.)
        /// </summary>
        public List<LinkMatch>? FindLinks(string text, PathWrapState wrap, int virtualRow = -1)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                // A blank row breaks any pending wrap sequence.
                wrap.PendingPrefix = string.Empty;
                wrap.ContinuationStart = wrap.ContinuationEnd = -1;
                return null;
            }

            var types = _types() ?? YoloProjectTypes.Snapshot.Empty;

            var all = new List<LinkMatch>();
            Collect(_file.Apply(text, wrap, virtualRow), all);
            Collect(_stack.Apply(text, wrap), all);
            Collect(_type.Apply(text, types), all);
            Collect(_member.Apply(text, types), all);
            Collect(_url.Apply(text), all);

            if (all.Count == 0) return null;

            // Stable order by start offset, then drop any span fully contained in one already kept, so a
            // reference is never painted or clicked twice (e.g. the `Bar` type inside `com.foo.Bar.baz`).
            all.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
            var merged = new List<LinkMatch>();
            foreach (var m in all)
            {
                bool contained = false;
                foreach (var kept in merged)
                {
                    if (m.Start >= kept.Start && m.End <= kept.End) { contained = true; break; }
                }
                if (!contained) merged.Add(m);
            }
            return merged.Count == 0 ? null : merged;
        }

        private static void Collect(List<LinkMatch>? matches, List<LinkMatch> into)
        {
            if (matches != null) into.AddRange(matches);
        }
    }
}
