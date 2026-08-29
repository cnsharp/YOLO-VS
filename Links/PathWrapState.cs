namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Shared state for tracking a terminal path that was hard-wrapped across two consecutive physical
    /// lines. Port of IntelliJ <c>PathWrapState</c>, with one deliberate difference described below.
    /// <para>
    /// IntelliJ's JediTerm calls each filter once per physical line, in output order, so it can carry a
    /// single long-lived <c>PathWrapState</c> per session and write <see cref="PendingPrefix"/> at the end
    /// of each line for the next line to consume. The hand-drawn terminal here does not stream lines through
    /// the filters: it re-evaluates whichever rows are currently visible, in arbitrary order, on every
    /// repaint. A long-lived mutable prefix would therefore be filled from an unrelated row and produce
    /// nondeterministic links. So this state is created per <see cref="YoloLinkFilters.FindLinks(string, string?)"/>
    /// call and <see cref="PendingPrefix"/> is derived from the caller-supplied previous row text
    /// (see <see cref="FileLinkFilter.ComputePendingPrefix"/>) — same behaviour, no order dependency.
    /// </para>
    /// <para>
    /// Consequence of that choice: a path wrapped across <i>three or more</i> rows is not reconstructed
    /// (IntelliJ chains it by re-storing the prefix). Two-row wraps — the overwhelmingly common case at
    /// normal terminal widths — behave identically.
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
    }
}
