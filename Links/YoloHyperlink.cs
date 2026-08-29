namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// What kind of symbol a terminal link refers to. Mirrors the IntelliJ link types
    /// (file path, stack-trace / bare file name, type name, member, URL).
    /// </summary>
    internal enum LinkKind
    {
        File,    // a file path or bare file name (stack frame) -> open in editor
        Type,    // a class/type name -> navigate to declaration
        Member,  // Class.member -> navigate to the member
        Url      // http(s):// -> open in browser
    }

    /// <summary>
    /// Resolved target of a terminal link. Produced by a link filter from the matched text; actual
    /// resolution against the filesystem / symbol index is deferred to click time (see
    /// <see cref="YoloLinkNavigator"/>), so streaming output is never blocked on I/O.
    /// </summary>
    internal sealed class LinkTarget
    {
        public LinkKind Kind;
        /// <summary>The exact matched substring (used for display / debugging).</summary>
        public string Raw = string.Empty;
        /// <summary>Full or relative file path for a <see cref="LinkKind.File"/> link (null for bare-name stack frames).</summary>
        public string? FilePath;
        /// <summary>Bare file name for a stack-frame link with no directory component.</summary>
        public string? BareFileName;
        /// <summary>1-based line number; 0 when the link has no line.</summary>
        public int Line;
        /// <summary>1-based column number; 0 when the link has no column.</summary>
        public int Column;
        /// <summary>Type name for <see cref="LinkKind.Type"/> / <see cref="LinkKind.Member"/> links.</summary>
        public string? TypeName;
        /// <summary>Member name for a <see cref="LinkKind.Member"/> link.</summary>
        public string? MemberName;
        /// <summary>URL for a <see cref="LinkKind.Url"/> link.</summary>
        public string? Url;
    }

    /// <summary>
    /// A single clickable span on a terminal line: the character range [<see cref="Start"/>, <see cref="End"/>)
    /// and the <see cref="LinkTarget"/> it navigates to. Produced by a link filter's <c>Apply</c>.
    /// </summary>
    internal sealed class LinkMatch
    {
        public int Start;   // inclusive character index in the row text
        public int End;     // exclusive character index
        public LinkTarget Target = null!;
    }
}
