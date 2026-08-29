using System;
using System.IO;
using System.Text.RegularExpressions;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Performs the actual navigation when a terminal link is clicked. VS-side counterpart of IntelliJ's
    /// <c>YoloNavigation.kt</c> + <c>yoloHyperlink</c>.
    /// <para>
    /// All resolution happens here rather than in the filters, so painting terminal rows never touches the
    /// filesystem — matching IntelliJ, which defers resolution to click time to keep the emulator thread free.
    /// </para>
    /// <para>
    /// Symbol resolution differs from upstream by necessity. IntelliJ resolves types and members through the
    /// language-agnostic <c>gotoClassContributor</c> / <c>gotoSymbolContributor</c> extension points, which
    /// have no VS equivalent reachable from a VSIX (<c>ISymbolSearchService</c> is not shipped in VS2026, and
    /// Roslyn's workspace is per-language and would not cover every project type). Instead a type resolves to
    /// the solution source file whose base name matches (see <see cref="YoloProjectTypes"/>) and the member is
    /// located by a declaration scan of that file — which lands on the right line for the conventional
    /// one-type-per-file layout used by C#, Java, Kotlin, Swift and TypeScript.
    /// </para>
    /// </summary>
    internal static class YoloLinkNavigator
    {
        /// <summary>Largest file the declaration scan will read, so a click can never stall the UI thread.</summary>
        private const int MaxScanBytes = 4 * 1024 * 1024;

        /// <summary>
        /// Navigates to <paramref name="target"/>. Mirrors IntelliJ's <c>yoloHyperlink</c>: a successful
        /// file/type/member navigation hides the YOLO pane so it no longer covers the editor, while a URL does
        /// not (it opens an external browser, so there is no in-IDE destination to reveal).
        /// </summary>
        /// <param name="target">The clicked link.</param>
        /// <param name="baseDirectory">The agent's working directory, used to resolve relative paths.</param>
        public static void Navigate(LinkTarget target, string? baseDirectory)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (target == null) return;

            try
            {
                switch (target.Kind)
                {
                    case LinkKind.Url:
                        OpenUrl(target.Url);
                        break;
                    case LinkKind.File:
                        if (NavigateToFile(target, baseDirectory)) HideYoloWindow();
                        break;
                    case LinkKind.Type:
                    case LinkKind.Member:
                        if (NavigateToSymbol(target, baseDirectory)) HideYoloWindow();
                        break;
                }
            }
            catch (Exception ex)
            {
                // A dead link must never take the terminal down with it.
                Log.Write($"YoloLinkNavigator.Navigate({target.Kind}, '{target.Raw}') failed: {ex.Message}");
            }
        }

        private static bool NavigateToFile(LinkTarget target, string? baseDirectory)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string? path = ResolveFile(target, baseDirectory);
            if (path == null) return false;
            return OpenDocumentAt(path, target.Line, target.Column);
        }

        private static bool NavigateToSymbol(LinkTarget target, string? baseDirectory)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(target.TypeName)) return false;

            string simple = YoloLinkPatterns.LastTypeNameSegment(target.TypeName!);
            string? path = YoloProjectTypes.For(baseDirectory ?? GetSolutionDirectory()).ResolveFile(simple);
            if (path == null || !File.Exists(path)) return false;

            // For a member reference, land on the member; for a type, on the type declaration. Falling back to
            // line 0 opens the top of the file, which is IntelliJ's behaviour when the member can't be pinned.
            string wanted = target.Kind == LinkKind.Member && !string.IsNullOrEmpty(target.MemberName)
                ? target.MemberName!
                : simple;
            int line = FindDeclarationLine(path, wanted);
            return OpenDocumentAt(path, line, 0);
        }

        /// <summary>
        /// Resolves a file reference against the agent's working directory, the solution directory, and the
        /// solution-wide file-name index. Port of IntelliJ's <c>FileLinkFilter.resolve</c> +
        /// <c>StackTraceLinkFilter.resolve</c>.
        /// </summary>
        private static string? ResolveFile(LinkTarget target, string? baseDirectory)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string? solutionDir = GetSolutionDirectory();

            string? raw = target.FilePath;
            if (!string.IsNullOrEmpty(raw))
            {
                foreach (string candidate in Candidates(raw!, baseDirectory, solutionDir))
                {
                    if (SafeIsFile(candidate)) return candidate;
                }
            }

            // A bare stack-frame name (no directory component) needs the solution-wide file-name lookup, the
            // equivalent of IntelliJ's FilenameIndex.getVirtualFilesByName.
            string? bare = target.BareFileName;
            if (!string.IsNullOrEmpty(bare))
            {
                foreach (string candidate in Candidates(bare!, baseDirectory, solutionDir))
                {
                    if (SafeIsFile(candidate)) return candidate;
                }
                string? indexed = YoloProjectTypes.For(baseDirectory ?? solutionDir).ResolveFullName(bare!);
                if (indexed != null && SafeIsFile(indexed)) return indexed;
            }

            return null;
        }

        /// <summary>Candidate absolute paths for a raw reference, in IntelliJ's resolution order.</summary>
        private static System.Collections.Generic.IEnumerable<string> Candidates(
            string raw, string? baseDirectory, string? solutionDir)
        {
            if (raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring("file://".Length);

            if (raw.StartsWith("~/", StringComparison.Ordinal) || raw.StartsWith(@"~\", StringComparison.Ordinal))
            {
                yield return Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), raw.Substring(2));
                yield break;
            }
            if (raw == "~")
            {
                yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                yield break;
            }

            yield return raw;                                   // already absolute
            if (!string.IsNullOrEmpty(baseDirectory)) yield return Combine(baseDirectory!, raw);
            if (!string.IsNullOrEmpty(solutionDir)) yield return Combine(solutionDir!, raw);
        }

        private static string Combine(string dir, string relative)
        {
            // Path.Combine ignores `dir` when `relative` is rooted, and throws on invalid chars; both are
            // realistic for arbitrary terminal text, so normalize defensively.
            try { return Path.GetFullPath(Path.Combine(dir, relative.TrimStart('/', '\\'))); }
            catch { return string.Empty; }
        }

        private static bool SafeIsFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { return File.Exists(path); }
            catch { return false; }
        }

        /// <summary>
        /// Opens <paramref name="path"/> in the editor and places the caret at the given 1-based line/column
        /// (agent output is 1-based; <see cref="IVsTextView"/> is 0-based). A line of 0 means "no line given",
        /// so the file simply opens at the top.
        /// </summary>
        private static bool OpenDocumentAt(string path, int line, int column)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                VsShellUtilities.OpenDocument(
                    ServiceProvider.GlobalProvider,
                    path,
                    VSConstants.LOGVIEWID_TextView,
                    out IVsUIHierarchy _,
                    out uint _,
                    out IVsWindowFrame frame,
                    out IVsTextView view);

                frame?.Show();
                if (view != null && line > 0)
                {
                    int l = line - 1;
                    int c = column > 0 ? column - 1 : 0;
                    view.SetCaretPos(l, c);
                    view.CenterLines(l, 1);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Write($"YoloLinkNavigator.OpenDocumentAt('{path}') failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// The 1-based line where <paramref name="name"/> appears to be declared, or 0 when it is not found.
        /// Stands in for IntelliJ's PSI member resolution: a line that both contains the name as a whole word
        /// and looks like a declaration wins; otherwise the first whole-word occurrence is used.
        /// </summary>
        private static int FindDeclarationLine(string path, string name)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxScanBytes) return 0;

                var word = new Regex(@"\b" + Regex.Escape(name) + @"\b", RegexOptions.CultureInvariant);
                // Declaration keywords across the languages the agents commonly print, plus a `name(`
                // signature shape for languages that declare without a keyword.
                var declaration = new Regex(
                    @"\b(class|struct|interface|record|enum|object|trait|impl|type|namespace|module|" +
                    @"public|private|protected|internal|static|abstract|virtual|override|sealed|partial|" +
                    @"def|fun|func|fn|sub|val|var|const|let|async|export|function)\b",
                    RegexOptions.CultureInvariant);

                int firstHit = 0;
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string text = lines[i];
                    if (!word.IsMatch(text)) continue;
                    if (firstHit == 0) firstHit = i + 1;
                    if (declaration.IsMatch(text)) return i + 1;
                }
                return firstHit;
            }
            catch (Exception ex)
            {
                Log.Write($"YoloLinkNavigator.FindDeclarationLine('{path}', '{name}') failed: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Opens a URL in the system browser. Only <c>http</c>/<c>https</c> is honoured: terminal output is
        /// untrusted text, and handing an arbitrary scheme to the shell would let a printed line launch a
        /// local handler.
        /// </summary>
        private static void OpenUrl(string? url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

            try { System.Diagnostics.Process.Start(uri.AbsoluteUri); }
            catch (Exception ex) { Log.Write($"YoloLinkNavigator.OpenUrl('{url}') failed: {ex.Message}"); }
        }

        /// <summary>
        /// Hides the YOLO pane after navigation, so the editor the user just jumped to is not covered.
        /// Mirrors IntelliJ's <c>yoloHyperlink</c>.
        /// </summary>
        private static void HideYoloWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (!(Package.GetGlobalService(typeof(SVsUIShell)) is IVsUIShell shell)) return;
                var guid = new Guid(Constants.ToolWindowGuid);
                shell.FindToolWindow(0, ref guid, out IVsWindowFrame frame);
                frame?.Hide();
            }
            catch (Exception ex)
            {
                Log.Write("YoloLinkNavigator.HideYoloWindow failed: " + ex.Message);
            }
        }

        private static string? GetSolutionDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
                string? solutionFile = dte?.Solution?.FullName;
                return string.IsNullOrEmpty(solutionFile) ? null : Path.GetDirectoryName(solutionFile);
            }
            catch
            {
                return null;
            }
        }
    }
}
