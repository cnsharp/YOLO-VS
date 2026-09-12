using System;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Resources;
using Microsoft.VisualStudio.PlatformUI;
using Svg;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Renders an agent's icon as a WPF <see cref="ImageSource"/>, reusing IntelliJ's icon
    /// assets verbatim (copied to <c>Resources/icons/agents/</c> and embedded as WPF resources).
    /// PNG/JPG load directly; SVG files are parsed into vector geometry (WPF's path mini-language
    /// is a superset of SVG path data, so no external SVG decoder is needed).
    /// </summary>
    internal static class AgentIconImage
    {
        // Resolve at runtime so the pack URI always matches the real assembly name (avoids a
        // hard-coded name drifting from <AssemblyName> in the csproj, which breaks icon loading).
        private static string AssemblyName => typeof(AgentIconImage).Assembly.GetName().Name ?? "Yolo";

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
            try
            {
                // A rooted filesystem path (absolute, or a downloaded network icon) loads from disk.
                if (Path.IsPathRooted(icon) && File.Exists(icon))
                {
                    if (icon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                        using (var r = new StreamReader(icon))
                            return SvgToImage(r.ReadToEnd());
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
                        using (var r = new StreamReader(rs.Stream))
                            return SvgToImage(r.ReadToEnd());
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

        private static string PackUri(string icon) =>
            $"pack://application:,,,/{AssemblyName};component/{icon.TrimStart('/')}";

        /// <summary>
        /// Current monitor DPI scale (1.0 at 100%, 2.0 at 200%, …). Used to rasterise SVGs at the
        /// device's real pixel density. Falls back to 1.0 when no presentation source is available.
        /// </summary>
        private static double GetDpiScale()
        {
            try
            {
                if (Application.Current?.MainWindow is Visual v)
                {
                    var source = PresentationSource.FromVisual(v);
                    if (source?.CompositionTarget != null)
                        return source.CompositionTarget.TransformToDevice.M22;
                }
            }
            catch
            {
                // fall through to 1.0
            }
            return 1.0;
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
        /// icon. IntelliJ gets this for free from <c>IconLoader</c>; here it is explicit. Only a few
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

        // Rasterise the SVG at the device's pixel resolution (not the 16x16 design size) so the
        // icon stays crisp on HiDPI displays. WPF then downscales this large bitmap to the displayed
        // size with Fant resampling, which is far sharper than stretching a tiny 64px raster.
        private const int SvgBaseSize = 32;   // generous upper bound on the displayed size (DIP)
        private const int SvgSupersample = 8; // extra oversampling for clean downscaling
        private const int SvgMinSize = 256;   // floor so very small / unscaled displays still look sharp

        private static ImageSource? SvgToImage(string svg)
        {
            try
            {
                using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svg));
                var doc = SvgDocument.Open<SvgDocument>(ms);
                // The agent SVGs use a viewBox, so overriding the pixel size just rescales the
                // vector artwork (gradients, circles, rects, url(#…) fills) without distortion.
                int size = (int)(SvgBaseSize * SvgSupersample * GetDpiScale());
                if (size < SvgMinSize) size = SvgMinSize;
                doc.Width = new SvgUnit(SvgUnitType.User, size);
                doc.Height = new SvgUnit(SvgUnitType.User, size);

                using var bitmap = doc.Draw();
                using var png = new MemoryStream();
                bitmap.Save(png, ImageFormat.Png);
                png.Position = 0;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = png;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                // Any parse/load failure just shows no icon rather than crashing the UI.
                return null;
            }
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
    /// Renders an agent icon the way the IntelliJ plugin does in its settings table: a coloured
    /// (lit) icon when the agent is installed, and a desaturated grey icon when it is not. Takes
    /// two values — the icon spec and the installed bool. Grayscale preserves the icon's alpha so
    /// transparent PNGs/SVGs don't pick up a black box.
    /// </summary>
    internal sealed class AgentIconInstalledConverter : IMultiValueConverter
    {
        public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var icon = values.Length > 0 ? values[0] as string : null;
            var installed = values.Length > 1 && values[1] is bool b && b;

            var src = AgentIconImage.FromPath(icon);
            if (src is BitmapSource bs && !installed)
                return ToGrayscale(bs);

            return src ?? DependencyProperty.UnsetValue;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        /// <summary>Per-pixel luminance desaturation that leaves alpha untouched.</summary>
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
    }
}
