using System.Collections.Generic;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Shared state for tracking a terminal path that was hard-wrapped across consecutive physical lines.
    /// Port of IntelliJ <c>PathWrapState</c>, adapted to the hand-drawn terminal's render model.
    /// <para>
    /// IntelliJ's JediTerm calls each filter once per physical line, in output order, so it carries one
    /// long-lived <c>PathWrapState</c> per session and writes <see cref="PendingPrefix"/> at the end of each
    /// line for the next line to consume. The hand-drawn terminal here re-evaluates only the visible rows,
    /// top-to-bottom, on every repaint — so <see cref="TerminalSurface"/> owns the single <c>PathWrapState</c>
    /// and threads it through <see cref="YoloLinkFilters.FindLinks(string, PathWrapState)"/> for each visible
    /// row in order. The prefix therefore accumulates exactly as in JediTerm, with no order-dependency hazard.
    /// </para>
    /// <para>
    /// Unlike the naive "store only the previous row's head" approach, the prefix here ACCUMULATES across
    /// every wrapped row via <see cref="WrapActive"/>, so a path split across <i>three or more</i> rows also
    /// reconstructs to its full path (the tail row is the only visible/clickable span, but its target is the
    /// whole path). A pending prefix that is never continued (a false head, e.g. a lone directory name) is
    /// dropped so it cannot poison a later row's reconstruction.
    /// </para>
    /// </summary>
    internal sealed class PathWrapState
    {
        /// <summary>Path fragment at the end of the previous row that had no extension and no line number.</summary>
        public string PendingPrefix = string.Empty;

        /// <summary>
        /// When <see cref="FileLinkFilter"/> reconstructed a wrapped path on the current row, the
        /// <c>[ContinuationStart, ContinuationEnd)</c> range in the current row where the continuation tail
        /// was linked. <see cref="StackTraceLinkFilter"/> suppresses any match overlapping this span so the
        /// same text does not also get a (wrong) bare-name link.
        /// </summary>
        public int ContinuationStart = -1;

        /// <summary>Exclusive end of <see cref="ContinuationStart"/>'s span; -1 when there is no continuation.</summary>
        public int ContinuationEnd = -1;

        /// <summary>True when <see cref="FileLinkFilter"/> linked a wrapped-path continuation on this row.</summary>
        public bool HasContinuation => ContinuationStart >= 0;

        /// <summary>
        /// True while a hard-wrapped path is being reconstructed across consecutive rows. Set when a head
        /// fragment is found and kept true as each continuation row extends the accumulated prefix, until the
        /// path completes (extension + optional line). Lets the normal-pass head detection avoid clobbering an
        /// in-progress reconstruction, so paths wrapped across 3+ rows resolve fully (not just the last two).
        /// </summary>
        public bool WrapActive;

        /// <summary>
        /// Virtual rows whose wrapped-path fragment links are awaiting upgrade to the full file target. Each
        /// row that contributes a visible fragment (the head row and every continuation row) records itself here
        /// so that when the path finally completes, <see cref="TerminalSurface"/> can rewrite every fragment
        /// link's <see cref="LinkMatch.Target"/> to the whole path — making the entire (multi-row) reference
        /// clickable, not just the tail row. Cleared once upgraded or when the wrap is abandoned.
        /// </summary>
        public readonly List<int> HeadRows = new List<int>();

        /// <summary>
        /// The full reconstructed file target, set by <see cref="FileLinkFilter"/> on the row where the wrapped
        /// path completes. <see cref="TerminalSurface"/> consumes and then clears it after upgrading every
        /// <see cref="HeadRows"/> entry.
        /// </summary>
        public LinkTarget? CompletedTarget;
    }
}
