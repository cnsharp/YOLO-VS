using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Lazily-built snapshot of the current solution's source-file names, used by the terminal link filters
    /// to decide which identifiers are worth turning into clickable links.
    /// <para>
    /// Port of IntelliJ <c>YoloProjectTypes</c>. Without this gate the type/member regexes match every
    /// capitalized word — <c>Result</c>, <c>OK</c>, <c>Error</c>, or the <c>Ctrl</c> in a <c>Ctrl/C</c>
    /// shortcut — and paint it as a link even though it is not a project type.
    /// </para>
    /// <para>
    /// IntelliJ sources its type names from the language-agnostic <c>gotoClassContributor</c> extension point.
    /// Visual Studio has no comparable cheap, language-agnostic type index available to a VSIX (Roslyn's
    /// workspace is per-language, heavy, and does not cover every project type), so this port derives the gate
    /// from the solution's <b>source-file base names</b> instead. In C#/Java/Kotlin/TypeScript/Swift the file
    /// base name equals the type name for the large majority of types, so the gate has essentially the same
    /// selectivity, and it doubles as IntelliJ's file-name fallback set (<c>Snapshot.files</c>).
    /// </para>
    /// <para>
    /// IMPORTANT — <see cref="For"/> must never block. It is called from the WPF render path while painting
    /// terminal rows; a synchronous directory walk there would stall the UI thread on every repaint. So it
    /// returns the last computed snapshot immediately and rebuilds on a pooled thread when the solution root
    /// changes or <see cref="Invalidate"/> is called; until that finishes the previous (or empty) snapshot is
    /// returned and links simply stay unpainted.
    /// </para>
    /// </summary>
    internal static class YoloProjectTypes
    {
        /// <summary>
        /// Source-file extensions treated as project references. A curated, code-oriented subset of
        /// <see cref="YoloLinkPatterns.ProgrammingExt"/> so a non-source file's base name (<c>build</c>,
        /// <c>README</c>) is not accidentally linked as a type.
        /// </summary>
        private static readonly HashSet<string> SourceFileExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "kt", "kts", "java", "scala", "sc", "groovy",
            "py", "pyi", "rb", "php", "pl", "pm", "lua",
            "js", "jsx", "mjs", "cjs", "ts", "tsx",
            "go", "rs", "c", "h", "cc", "cpp", "cxx", "hpp", "cs", "m", "mm", "swift", "d", "nim",
            "ex", "exs", "clj", "cljs", "erl", "hs", "ml", "fs", "fsx", "jl", "r",
            "proto", "sol", "graphql", "gql", "dart",
        };

        /// <summary>Directories never worth scanning: build output, VCS metadata, dependency caches.</summary>
        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".vs", ".idea", "node_modules", "packages",
            "dist", "out", "target", "__pycache__", ".gradle", "TestResults",
        };

        /// <summary>Defensive cap so a pathological repository cannot turn the scan into a long walk.</summary>
        private const int MaxFilesScanned = 60000;

        /// <summary>Immutable view of the solution's file names; cheap to hold and query during a row scan.</summary>
        internal sealed class Snapshot
        {
            public static readonly Snapshot Empty = new Snapshot(
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, List<string>>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            /// <summary>Source-file base names (no extension), the type-name gate.</summary>
            private readonly HashSet<string> _baseNames;
            /// <summary>Base name → every file with that name, for the file-name fallback link.</summary>
            private readonly Dictionary<string, List<string>> _byBaseName;
            /// <summary>File name incl. extension → full path, for resolving bare stack-frame names.</summary>
            private readonly Dictionary<string, string> _byFullName;

            internal Snapshot(HashSet<string> baseNames, Dictionary<string, List<string>> byBaseName,
                              Dictionary<string, string> byFullName)
            {
                _baseNames = baseNames;
                _byBaseName = byBaseName;
                _byFullName = byFullName;
            }

            /// <summary>True when <paramref name="name"/> is a source-file base name in the solution.</summary>
            public bool ContainsSimple(string name) => _baseNames.Contains(name);

            /// <summary>Same set as <see cref="ContainsSimple"/>; kept separate to mirror IntelliJ's API shape.</summary>
            public bool ContainsFile(string name) => _baseNames.Contains(name);

            /// <summary>Resolve a source-file base name (no extension) to one of its files, or null.</summary>
            public string? ResolveFile(string name) =>
                _byBaseName.TryGetValue(name, out List<string>? paths) && paths.Count > 0 ? paths[0] : null;

            /// <summary>
            /// Resolve a possibly qualified type reference (<c>Namespace.Type</c>, <c>Project.Type</c>) to a
            /// file. Resolving on the bare last segment alone is ambiguous the moment two projects declare the
            /// same type — <c>ClassLibrary1.Class1.Foo()</c> would jump into <c>ClassLibrary2\Class1.cs</c> —
            /// so when several files share the base name, the winner is the one whose directory chain matches
            /// the qualifier segments (project folder, then namespace folders, innermost first).
            /// </summary>
            public string? ResolveQualified(string qualified)
            {
                string[] segments = qualified.Split('.');
                string simple = segments[segments.Length - 1];
                if (!_byBaseName.TryGetValue(simple, out List<string>? paths) || paths.Count == 0) return null;
                if (paths.Count == 1 || segments.Length == 1) return paths[0];

                string? best = null;
                int bestScore = 0;
                foreach (string path in paths)
                {
                    int score = QualifierScore(path, segments);
                    if (score > bestScore) { bestScore = score; best = path; }
                }
                return best ?? paths[0];
            }

            /// <summary>
            /// How many qualifier segments (all but the last, walked innermost-first) match the directory
            /// chain above <paramref name="path"/> contiguously, e.g. <c>ClassLibrary1.Foo</c> scores 1 for
            /// <c>…\ClassLibrary1\Foo.cs</c>.
            /// </summary>
            private static int QualifierScore(string path, string[] segments)
            {
                string dir = Path.GetDirectoryName(path) ?? string.Empty;
                string[] dirs = dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                int d = dirs.Length - 1;
                int q = segments.Length - 2;   // skip the type name itself
                int score = 0;
                while (q >= 0 && d >= 0)
                {
                    if (dirs[d].Length == 0) { d--; continue; }
                    if (!string.Equals(dirs[d], segments[q], StringComparison.OrdinalIgnoreCase)) break;
                    score++;
                    q--;
                    d--;
                }
                return score;
            }

            /// <summary>Resolve a bare file name including extension (e.g. <c>plugin.xml</c>) to its full path.</summary>
            public string? ResolveFullName(string fileName) =>
                _byFullName.TryGetValue(fileName, out string? path) ? path : null;
        }

        private static volatile Snapshot _current = Snapshot.Empty;
        private static volatile string? _currentRoot;
        private static int _building;

        /// <summary>
        /// The most recent snapshot for <paramref name="rootDir"/>, never blocking. Schedules a background
        /// rebuild when the root changed or the cache was invalidated; returns <see cref="Snapshot.Empty"/>
        /// until the first build for that root completes.
        /// </summary>
        public static Snapshot For(string? rootDir)
        {
            if (string.IsNullOrEmpty(rootDir)) return Snapshot.Empty;
            if (string.Equals(_currentRoot, rootDir, StringComparison.OrdinalIgnoreCase)) return _current;

            if (Interlocked.CompareExchange(ref _building, 1, 0) == 0)
            {
                string root = rootDir!;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        Snapshot built = Build(root);
                        _current = built;
                        _currentRoot = root;
                    }
                    catch (Exception ex)
                    {
                        Log.Write("YoloProjectTypes build failed: " + ex.Message);
                    }
                    finally
                    {
                        Volatile.Write(ref _building, 0);
                    }
                });
            }
            return Snapshot.Empty;
        }

        /// <summary>Forces the next <see cref="For"/> call to rebuild (e.g. after a solution reload).</summary>
        public static void Invalidate() => _currentRoot = null;

        private static Snapshot Build(string root)
        {
            var baseNames = new HashSet<string>(StringComparer.Ordinal);
            var byBaseName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var byFullName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            int scanned = 0;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0 && scanned < MaxFilesScanned)
            {
                string dir = pending.Pop();

                // Enumerate defensively: a single unreadable directory (permissions, a stale junction, a
                // path over MAX_PATH) must not abort the whole scan.
                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { continue; }

                foreach (string file in files)
                {
                    if (++scanned >= MaxFilesScanned) break;
                    string fileName = Path.GetFileName(file);
                    if (fileName.Length == 0) continue;

                    // First writer wins, matching IntelliJ's putIfAbsent: with duplicate names across
                    // projects, a stable pick beats an arbitrary last-one-wins.
                    if (!byFullName.ContainsKey(fileName)) byFullName[fileName] = file;

                    string ext = Path.GetExtension(file);
                    if (ext.Length < 2 || !SourceFileExt.Contains(ext.Substring(1))) continue;
                    string baseName = Path.GetFileNameWithoutExtension(file);
                    if (baseName.Length == 0) continue;
                    baseNames.Add(baseName);
                    // Keep EVERY path per base name (IntelliJ keeps an index of all of them); collisions
                    // across projects are disambiguated at click time by ResolveQualified.
                    if (!byBaseName.TryGetValue(baseName, out List<string>? list))
                        byBaseName[baseName] = list = new List<string>(1);
                    if (!list.Contains(file)) list.Add(file);
                }

                string[] subDirs;
                try { subDirs = Directory.GetDirectories(dir); }
                catch { continue; }
                foreach (string sub in subDirs)
                {
                    string name = Path.GetFileName(sub);
                    if (name.Length > 0 && !SkipDirs.Contains(name)) pending.Push(sub);
                }
            }

            return new Snapshot(baseNames, byBaseName, byFullName);
        }
    }
}
