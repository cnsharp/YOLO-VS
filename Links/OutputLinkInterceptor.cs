using System;
using System.Collections.Generic;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Thin named entry point over <see cref="YoloLinkFilters"/>, kept because it is the name the rest of the
    /// docs refer to for "intercept a line of terminal output and hand back its links". The terminal itself
    /// (<see cref="TerminalSurface"/> / <see cref="WpfTerminalView"/>) uses <see cref="YoloLinkFilters"/>
    /// directly, since it also needs to pass the preceding row for wrapped-path reconstruction.
    /// </summary>
    internal sealed class OutputLinkInterceptor
    {
        private readonly YoloLinkFilters _filters;

        public OutputLinkInterceptor(Func<YoloProjectTypes.Snapshot> types) => _filters = new YoloLinkFilters(types);

        /// <summary>Analyze one line of output and extract its clickable links.</summary>
        public List<LinkMatch>? ExtractLinks(string text, string? previousLine = null) =>
            _filters.FindLinks(text, previousLine);
    }
}
