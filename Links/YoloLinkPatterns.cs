using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Link-detection building blocks shared by the terminal link filters.
    /// Faithful C# port of IntelliJ <c>YoloLinkPatterns.kt</c>. The regex bodies are copied
    /// verbatim from the Kotlin raw strings (Kotlin raw strings do not process escapes, and C#
    /// verbatim strings also keep backslashes literal, so the two map 1:1). The only substitution
    /// is <c>$PROGRAMMING_EXT</c> → <c>{0}</c>, filled from <see cref="ProgrammingExt"/>.
    /// Possessive quantifiers (<c>*+</c>, <c>++</c>) are not supported by .NET's regex engine and
    /// are downgraded to greedy <c>*</c> / <c>+</c> — behaviour is equivalent for these patterns.
    /// </summary>
    public static class YoloLinkPatterns
    {
        /// <summary>Max hyperlinks a single terminal line may produce before the rest is ignored.</summary>
        internal const int MaxMatchesPerLine = 50;

        /// <summary>
        /// Fixed allowlist of common programming / source / config file extensions, so a dotted name
        /// that is not a real file (e.g. <c>pay.amount.mark</c>, <c>JSON.parseObject</c>, <c>Ctrl/C</c>)
        /// is never mistaken for a file reference.
        /// </summary>
        internal const string ProgrammingExt = "kt|kts|java|scala|sc|groovy|gradle|" +
            "py|pyi|pyw|rb|rake|php|pl|pm|lua|sh|bash|zsh|ksh|" +
            "js|jsx|mjs|cjs|ts|tsx|vue|html|htm|xhtml|css|scss|sass|less|styl|" +
            "go|rs|c|h|cc|cpp|cxx|hpp|hxx|hh|cs|m|mm|swift|d|nim|zig|s|asm|" +
            "ex|exs|clj|cljs|cljc|erl|hs|ml|mli|fs|fsx|fsi|jl|r|proto|sol|graphql|gql|" +
            "xml|xsd|xsl|xslt|wsdl|json|json5|jsonc|yaml|yml|toml|ini|cfg|conf|config|properties|env|lock|csv|tsv|log|" +
            "md|markdown|rst|txt|text|diff|patch|editorconfig|gitignore|dockerignore|tf|tfvars|feature|bnf|avsc|edn|" +
            "iml|ipr|iws|" +
            "csproj|sln|slnx|props|targets|vcxproj|vcxitems|user|ruleset|publishproj|" +
            "vsix|nuspec|appxmanifest|manifest|resx|settings|dbml|edmx|dll|exe";

        /// <summary>
        /// Path references: <c>path</c>, <c>path:line</c>, <c>path:line:column</c>,
        /// <c>path:line-line</c> (range), <c>path:column</c>, and <c>file://</c> URIs. Windows drives,
        /// UNC shares and <c>~</c> home are supported. Completion requirement (extension OR <c>:line</c>):
        /// the matched path must end in a recognized extension or be followed by a line number, which stops
        /// a long path hard-wrapped across lines from being linked as several broken fragments.
        /// <para>Groups: 1 = full path (incl. extension), 2 = extension, 3 = line, 4 = range end, 5 = column.</para>
        /// </summary>
        internal static readonly Regex PathPattern = new Regex(
            string.Format(CultureInfo.InvariantCulture,
                @"(?<![\\/\w.])((?:(?:[A-Za-z]:[\\/]?)|[\\/]|[~][\\/]?|\\\\[A-Za-z0-9._\-]+(?:[\\/][A-Za-z0-9._\-]+)+|[A-Za-z0-9._\-]+[\\/])(?:[A-Za-z0-9._\-]+[\\/])*(?:[A-Za-z0-9._\-]+\.((?i:{0}))(?![\\/\w.])|[A-Za-z0-9._\-]+))(?::(\d+)(?:-(\d+))?(?::(\d+))?)?",
                ProgrammingExt),
            RegexOptions.Compiled);

        /// <summary>
        /// Quoted path (allows embedded spaces), e.g. <c>"/path with space/Bar.kt":5</c>. Requires a
        /// path ending in a recognized programming extension. A <c>…</c> / <c>...</c> inside the quotes marks a
        /// truncated path and is dropped. Groups: 1 = opening quote, 2 = path, 3 = line, 4 = column.
        /// </summary>
        internal static readonly Regex QuotedPathPattern = new Regex(
            string.Format(CultureInfo.InvariantCulture,
                @"([""'])((?:[A-Za-z]:)?[\\/][^""']*?\.(?i:{0}))\1(?::(\d+))?(?::(\d+))?",
                ProgrammingExt),
            RegexOptions.Compiled);

        /// <summary>Bare <c>FileName.ext:line</c> / <c>FileName.ext:line:col</c> with no directory. Groups: 1 = file, 2 = line, 3 = column.</summary>
        internal static readonly Regex StackBarePattern = new Regex(
            string.Format(CultureInfo.InvariantCulture,
                @"(?<![\\/\w.\-])([\w.\-]+\.(?i:{0})):(\d+)(?::(\d+))?",
                ProgrammingExt),
            RegexOptions.Compiled);

        /// <summary>Bare file name with no line number (e.g. <c>plugin.xml</c>). Groups: 1 = file.</summary>
        internal static readonly Regex StackBareNamePattern = new Regex(
            string.Format(CultureInfo.InvariantCulture,
                @"(?<![\\/\w.\-])([\w.\-]+\.(?i:{0}))(?![\\/\w.:])",
                ProgrammingExt),
            RegexOptions.Compiled);

        /// <summary>Python traceback <c>File "path", line N</c> (double-quoted). Groups: 1 = file, 2 = line.</summary>
        internal static readonly Regex StackPyDqPattern = new Regex(
            string.Format(CultureInfo.InvariantCulture,
                @"File ""([^""]+\.(?i:{0}))"", line (\d+)",
                ProgrammingExt),
            RegexOptions.Compiled);

        /// <summary>Python traceback <c>File 'path', line N</c> (single-quoted). Groups: 1 = file, 2 = line.</summary>
        internal static readonly Regex StackPySqPattern = new Regex(
            string.Format(CultureInfo.InvariantCulture,
                @"File '([^']+\.(?i:{0}))', line (\d+)",
                ProgrammingExt),
            RegexOptions.Compiled);

        /// <summary>
        /// Type references: a qualified name or a simple PascalCase identifier. A trailing lowercase
        /// extension is excluded so type links never collide with file-path links. Named groups:
        /// <c>qualified</c>, <c>simple</c> (mutually exclusive).
        /// </summary>
        internal static readonly Regex TypeNamePattern = new Regex(
            @"(?<![.\w/\\])(?<qualified>\\?(?:[A-Za-z_][A-Za-z0-9_]*)(?:(?:\.|::|\\)[A-Za-z_][A-Za-z0-9_]*)+)(?!\.[a-z])" +
            @"|(?<![.\w/\\])(?<simple>(?![A-Z]+\b)[A-Z][a-zA-Z0-9_]*)(?!\.[a-z])",
            RegexOptions.Compiled);

        /// <summary>
        /// <c>Class.member</c> / <c>Class#member</c> references. The class part reuses the multi-separator
        /// qualified form so C# <c>MyApp.Services.UserService.SomeMethod</c>, Python
        /// <c>myapp.models.User.save</c> and Go <c>http.Client.Get</c> are recognized too.
        /// Named groups: <c>class</c>, <c>member</c>.
        /// <para>
        /// Aligned with IntelliJ <c>MEMBER_REF_PATTERN</c>. The member group has no
        /// <c>(?&lt;![.\w])</c> lookbehind: that anchor would be evaluated right after the <c>[.#]</c>
        /// separator (where the preceding char is always <c>.</c> or <c>#</c>), so it could only ever succeed
        /// for the <c>#</c> form and silently rejected the <c>.</c> form — the primary case. The bug was fixed
        /// in the upstream Kotlin source as well, so this is no longer a divergence.
        /// </para>
        /// </summary>
        internal static readonly Regex MemberRefPattern = new Regex(
            @"(?<class>(?<![.\w/\\])(?:\\?(?:[A-Za-z_][A-Za-z0-9_]*)(?:(?:\.|::|\\)[A-Za-z_][A-Za-z0-9_]*)+)|[A-Z][a-zA-Z0-9_]*)[.#](?<member>[A-Za-z_]\w*)",
            RegexOptions.Compiled);

        /// <summary><c>http(s)://</c> URLs (no trailing whitespace/quote/bracket).</summary>
        internal static readonly Regex UrlPattern = new Regex(@"https?://[^\s<>""'\)\]]+", RegexOptions.Compiled);

        /// <summary>
        /// Separators a qualified type name may use across languages: Java/C#/Python/Go <c>.</c>,
        /// Rust/Ruby <c>::</c>, PHP <c>\</c>. Port of IntelliJ <c>QUALIFIED_SEPARATORS</c>.
        /// </summary>
        private static readonly char[] QualifiedSeparators = { '.', ':', '\\' };

        /// <summary>True when <paramref name="name"/> is a qualified (not simple) type name.</summary>
        internal static bool IsQualifiedName(string name) => name.IndexOfAny(QualifiedSeparators) >= 0;

        /// <summary>
        /// Normalizes a qualified name to canonical dotted form so names written in different languages'
        /// conventions compare equal.
        /// </summary>
        internal static string NormalizeTypeName(string name) => name.Replace('\\', '.').Replace("::", ".");

        /// <summary>Last segment of a (possibly qualified) type name, in any language's separator convention.</summary>
        internal static string LastTypeNameSegment(string name)
        {
            int idx = name.LastIndexOfAny(QualifiedSeparators);
            return idx < 0 ? name : name.Substring(idx + 1);
        }

        /// <summary>
        /// True when [text] at [fileStart] is the tail of a truncated path — the char(s) immediately before
        /// it are a <c>…</c> (U+2026) or a <c>...</c> run.
        /// </summary>
        internal static bool IsTruncatedPathHead(string text, int fileStart)
        {
            if (fileStart < 1) return false;
            char c = text[fileStart - 1];
            if (c == '…') return true;
            // Kotlin's `text.startsWith("...", fileStart - 3)` — .NET has no offset-based StartsWith, so
            // check the three chars ending just before the path directly.
            return c == '.' && fileStart >= 3 && text[fileStart - 2] == '.' && text[fileStart - 3] == '.';
        }

        /// <summary>
        /// True when the file reference at [pathEnd] is truncated: the char right after the captured path is a
        /// <c>…</c> or a <c>...</c> run.
        /// </summary>
        internal static bool IsTruncatedPath(string text, int pathEnd)
        {
            if (pathEnd >= text.Length) return false;
            char c = text[pathEnd];
            if (c == '…') return true;
            // Kotlin's `text.startsWith("...", pathEnd)`; see IsTruncatedPathHead for why this is spelled out.
            return c == '.' && pathEnd + 2 < text.Length && text[pathEnd + 1] == '.' && text[pathEnd + 2] == '.';
        }

        /// <summary>
        /// True when [text] is a line of a unified diff's structural metadata (<c>@@</c>, <c>--- </c>,
        /// <c>+++ </c>), so links are suppressed on it.
        /// </summary>
        internal static bool IsDiffLine(string text)
        {
            string t = text.TrimStart();
            if (t.Length == 0) return false;
            return t.StartsWith("@@", StringComparison.Ordinal) ||
                   t.StartsWith("--- ", StringComparison.Ordinal) ||
                   t.StartsWith("+++ ", StringComparison.Ordinal);
        }
    }
}
