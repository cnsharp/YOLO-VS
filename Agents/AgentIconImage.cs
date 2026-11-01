using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Resources;
using Microsoft.VisualStudio.PlatformUI;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Renders an agent's icon as a WPF <see cref="ImageSource"/>, reusing the original plugin's icon
    /// assets (copied to <c>Resources/icons/agents/</c> and embedded as WPF resources).
    /// PNG/JPG load directly; SVG files are parsed into WPF <b>vector</b> geometry via SharpVectors
    /// and exposed as a <see cref="DrawingImage"/>. Because the result is a true vector, it stays
    /// pixel-perfect at every DPI and size (exactly like the HTML / IDEA renderers), and SVG
    /// features the browser honours — gradients, <c>feDropShadow</c> filters, <c>@media
    /// (prefers-color-scheme)</c> theming, and embedded <c>@font-face</c> wordmarks — all render
    /// correctly with proper transparency (no opaque black background).
    /// </summary>
    internal static class AgentIconImage
    {
        // Resolve at runtime so the pack URI always matches the real assembly name (avoids a
        // hard-coded name drifting from <AssemblyName> in the csproj, which breaks icon loading).
        private static string AssemblyName => typeof(AgentIconImage).Assembly.GetName().Name ?? "Yolo";

        // Icons are immutable once loaded, so cache the parsed ImageSource per (spec, dark-variant).
        private static readonly Dictionary<string, ImageSource?> Cache =
            new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Resolve an agent's icon by id (reads the agents.json icon spec).
        /// <paramref name="darkVariant"/> opts in to the theme-specific <c>_dark</c> twin, and is only
        /// correct for chrome that follows the VS theme (the tool-window panel). The Options page is a
        /// plain WPF control on a light background, so it must keep the light icon.
        /// </summary>
        public static ImageSource? GetIcon(string agentId, bool darkVariant = false) =>
            Resolve(AgentRegistry.IconFor(agentId), darkVariant);

        /// <summary>
        /// Generic terminal glyph used for plain-shell sessions (no agent). Drawn as a vector
        /// <see cref="DrawingImage"/> so it needs no embedded asset and stays crisp at any DPI.
        /// A rounded frame with a <c>&gt;</c> prompt and a cursor — mirrors the look of a shell tab.
        /// </summary>
        public static ImageSource? GetTerminalFallback()
        {
            try
            {
                var stroke = new SolidColorBrush(Color.FromRgb(0x6C, 0x70, 0x7E));
                stroke.Freeze();
                var frame = new RectangleGeometry(new Rect(1.5, 1.5, 13, 13), 3, 3);
                var border = new GeometryDrawing(null, new Pen(stroke, 1.25), frame);

                var prompt = Geometry.Parse("M4.5,5.5 L8,9 L4.5,12.5 M10,12 L12.5,12");
                var promptDraw = new GeometryDrawing(null, new Pen(stroke, 1.25)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                }, prompt);

                var group = new DrawingGroup();
                group.Children.Add(border);
                group.Children.Add(promptDraw);

                var img = new DrawingImage(group);
                img.Freeze();
                return img;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Resolve an icon from an explicit spec. A rooted filesystem path (an absolute path or a
        /// downloaded network icon) loads from disk; otherwise the spec is the resource's physical
        /// path (e.g. <c>Resources/icons/agents/claude.png</c>) and is addressed through a WPF pack
        /// URI — the standard .NET resource convention. The logical path always matches the on-disk
        /// location, so there is no hidden base directory. <paramref name="darkVariant"/> is as in
        /// <see cref="GetIcon(string, bool)"/>: off for the light Options page, on for the panel.
        /// </summary>
        public static ImageSource? FromPath(string? icon, bool darkVariant = false) => Resolve(icon, darkVariant);

        private static ImageSource? Resolve(string? icon, bool darkVariant)
        {
            if (icon is null or "") return null;
            var key = icon + "|" + (darkVariant ? "d" : "l");
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;
                var built = Build(icon, darkVariant);
                Cache[key] = built;
                return built;
            }
        }

        private static ImageSource? Build(string icon, bool darkVariant)
        {
            try
            {
                // A rooted filesystem path (absolute, or a downloaded network icon) loads from disk.
                if (Path.IsPathRooted(icon) && File.Exists(icon))
                {
                    if (icon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var r = new StreamReader(icon))
                        {
                            var v = SvgToImage(r.ReadToEnd());
                            if (v != null) return v;
                        }
                        // SharpVectors can't parse every SVG (e.g. ones embedding a base64
                        // @font-face). Fall back to a same-named .png sibling so the agent still
                        // gets an icon instead of going blank.
                        var png = Path.ChangeExtension(icon, ".png");
                        if (File.Exists(png)) return new BitmapImage(new Uri(png, UriKind.Absolute));
                        return null;
                    }
                    return new BitmapImage(new Uri(icon, UriKind.Absolute));
                }

                // Bundled icons may ship a dark-theme variant; prefer it when VS is dark and
                // fall back to the plain asset otherwise. Opt-in only: the light Options page
                // keeps the plain icon even while VS itself is dark.
                var isDark = darkVariant && IsDarkTheme();
                var spec = isDark ? DarkVariant(icon) ?? icon : icon;

                // Otherwise treat `icon` as a path that matches the embedded resource's physical
                // location (e.g. Resources/icons/agents/claude.png), addressed through a WPF pack URI.
                var pack = PackUri(spec);
                if (pack.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    var rs = Application.GetResourceStream(new Uri(pack, UriKind.Absolute));
                    if (rs != null)
                    {
                        using (var r = new StreamReader(rs.Stream))
                        {
                            var v = SvgToImage(r.ReadToEnd());
                            if (v != null) return v;
                        }
                    }
                    // Same PNG-sibling fallback for bundled resources.
                    var pngPack = PackUri(Path.ChangeExtension(spec, ".png"));
                    try
                    {
                        var prs = Application.GetResourceStream(new Uri(pngPack, UriKind.Absolute));
                        if (prs != null)
                        {
                            prs.Stream.Dispose();
                            return new BitmapImage(new Uri(pngPack, UriKind.Absolute));
                        }
                    }
                    catch
                    {
                        // No PNG twin — leave the icon blank.
                    }
                    return null;
                }
                return new BitmapImage(new Uri(pack, UriKind.Absolute));
            }
            catch
            {
                // Any parse/load failure just shows no icon rather than crashing the UI.
            }
            return null;
        }

        // agents.json stores the icon as a bare filename (e.g. "claude.svg"); the bundled-icons
        // root differs per end, so VS maps it onto its embedded "Resources/icons/agents/<id>.<ext>".
        // A legacy full-path spec ("/icons/agents/<id>.<ext>") is still accepted for safety. A rooted
        // filesystem or downloaded icon never reaches here (handled above); only bundled resource
        // specs do.
        private static string PackUri(string icon)
        {
            var rel = icon.TrimStart('/');
            if (!rel.Contains("/"))
                rel = "Resources/icons/agents/" + rel;
            else if (rel.StartsWith("icons/", StringComparison.OrdinalIgnoreCase))
                rel = "Resources/" + rel;
            return $"pack://application:,,,/{AssemblyName};component/{rel}";
        }

        /// <summary>
        /// True when VS is running a dark theme — same test the panel chrome uses
        /// (<see cref="ToolWindow.YoloPanel"/> derives its tab palette from it), read from the
        /// tool-window background VS itself paints.
        /// </summary>
        private static bool IsDarkTheme()
        {
            try
            {
                var bg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
                return (bg.R + bg.G + bg.B) / 3 < 128;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The dark-theme twin of an embedded icon (<c>codex.svg</c> → <c>codex_dark.svg</c>), but only
        /// when that asset really ships in this assembly; null otherwise, so the caller keeps the plain
        /// icon. The original plugin supplies this automatically; here it is explicit. Only a few
        /// bundled icons have a twin today (e.g. codex), and adding one is just dropping the file in.
        /// </summary>
        private static string? DarkVariant(string icon)
        {
            int dot = icon.LastIndexOf('.');
            if (dot <= 0) return null;
            string name = icon.Substring(0, dot);
            if (name.EndsWith("_dark", StringComparison.OrdinalIgnoreCase)) return null;

            var candidate = name + "_dark" + icon.Substring(dot);

            // Probe-only: the stream is opened just to learn whether the asset exists. A missing
            // resource makes GetResourceStream throw (not return null), hence the catch.
            StreamResourceInfo? info = null;
            try
            {
                info = Application.GetResourceStream(new Uri(PackUri(candidate), UriKind.Absolute));
                info?.Stream?.Dispose();
            }
            catch
            {
                // Fall through: no twin, keep the plain icon.
            }

            return info == null ? null : candidate;
        }

        /// <summary>Current VS tool-window text colour as a hex string (#RRGGBB), or null on failure.</summary>
        private static string? ThemeTextHex()
        {
            try
            {
                var c = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey);
                return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            catch
            {
                return null;
            }
        }

        // Rasterise-free SVG → WPF vector. SharpVectors parses the SVG into a DrawingGroup and we wrap
        // it in a DrawingImage; WPF then strokes/fills that geometry on the fly, so the icon is
        // resolution-independent (never blurry) and every SVG feature renders with correct alpha.
        // Theme-aware glyphs: the SVG `currentColor` keyword resolves to black by default, which is
        // invisible on a dark VS theme — substitute it with the VS tool-window text colour so icons
        // that use currentColor (e.g. the Manage Agents robot) follow the active theme.
        private static ImageSource? SvgToImage(string svg)
        {
            try
            {
                var themed = ThemeTextHex();
                if (themed != null)
                    svg = svg.Replace("currentColor", themed);

                // Some SVGs attach a filter (e.g. feDropShadow) to a group. The vector renderer
                // cannot reproduce every SVG filter and, rather than drawing the source graphic,
                // silently DROPS the whole filtered element — which hides the agent wordmark
                // (e.g. amp). Strip filter attributes and their <defs> so the underlying artwork
                // always renders. Losing a drop-shadow is irrelevant at icon sizes.
                svg = StripSvgFilters(svg);

                using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svg));
                var settings = new WpfDrawingSettings
                {
                    IncludeRuntime = false,
                    TextAsGeometry = true
                };
                var reader = new FileSvgReader(settings);
                var drawing = reader.Read(ms) as DrawingGroup;
                if (drawing == null) return null;

                var img = new DrawingImage(drawing);
                img.Freeze();
                return img;
            }
            catch
            {
                // Any parse failure just shows no icon rather than crashing the UI.
                return null;
            }
        }

        // Remove `filter="url(#…)"` attributes and the <filter> definitions they reference. Done
        // with regexes on the raw text because we operate on the SVG string before parsing; this is
        // a render-time normalisation, not a change to the embedded source asset.
        private static readonly Regex FilterAttrRegex =
            new Regex(@"\sfilter=""url\(#[^ ""]+\)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);


        private static readonly Regex FilterDefRegex =
            new Regex(@"<filter\b[^>]*>.*?</filter>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static string StripSvgFilters(string svg)
        {
            svg = FilterAttrRegex.Replace(svg, "");
            svg = FilterDefRegex.Replace(svg, "");
            return svg;
        }
    }

    /// <summary>Binds an icon spec (relative embedded resource or filesystem path) to a WPF <see cref="Image"/> Source.</summary>
    internal sealed class AgentIconConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => AgentIconImage.FromPath(value as string);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Renders an agent icon for the settings table: a coloured (lit) icon when the agent is
    /// installed, and a desaturated grey icon when it is not. Grayscale preserves the icon's alpha
    /// so transparent PNGs/SVGs don't pick up a black box. Works for both raster (PNG) and vector
    /// (SVG) sources — for a vector <see cref="DrawingImage"/> it is first rasterised to a bitmap at
    /// a fixed thumbnail size, then desaturated.
    /// </summary>
    internal sealed class AgentIconInstalledConverter : IMultiValueConverter
    {
        public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var icon = values.Length > 0 ? values[0] as string : null;
            var installed = values.Length > 1 && values[1] is bool b && b;

            var src = AgentIconImage.FromPath(icon);
            if (installed) return src ?? DependencyProperty.UnsetValue;

            if (src is BitmapSource bs) return ToGrayscale(bs);
            if (src is DrawingImage di) return ToGrayscale(di);
            return src ?? DependencyProperty.UnsetValue;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        /// <summary>Per-pixel luminance desaturation that leaves alpha untouched (raster path).</summary>
        private static BitmapSource ToGrayscale(BitmapSource src)
        {
            try
            {
                var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                int w = bgra.PixelWidth, h = bgra.PixelHeight;
                var pixels = new byte[w * h * 4];
                bgra.CopyPixels(pixels, w * 4, 0);
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                    byte lum = (byte)(0.299 * r + 0.587 * g + 0.114 * b);
                    pixels[i] = lum;
                    pixels[i + 1] = lum;
                    pixels[i + 2] = lum;
                    // pixels[i + 3] (alpha) unchanged
                }
                var wb = new WriteableBitmap(w, h, bgra.DpiX, bgra.DpiY, PixelFormats.Bgra32, null);
                wb.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), pixels, w * 4, 0);
                wb.Freeze();
                return wb;
            }
            catch
            {
                return src;
            }
        }

        /// <summary>Rasterise a vector icon to a bitmap, then desaturate it (vector path).</summary>
        private static ImageSource? ToGrayscale(DrawingImage di)
        {
            try
            {
                const int size = 96; // settings thumbnail; crisp enough after desaturation
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                    dc.DrawDrawing(di.Drawing);
                var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);
                rtb.Freeze();
                return ToGrayscale(rtb);
            }
            catch
            {
                return di;
            }
        }
    }
}
