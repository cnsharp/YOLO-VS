using System;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

        /// <summary>Resolve an agent's icon by id (reads the agents.json icon spec).</summary>
        public static ImageSource? GetIcon(string agentId) => Resolve(AgentRegistry.IconFor(agentId));

        /// <summary>Resolve an icon from an explicit spec — <c>res://…</c> or a filesystem path.</summary>
        public static ImageSource? FromPath(string? icon) => Resolve(icon);

        private static ImageSource? Resolve(string? icon)
        {
            if (string.IsNullOrEmpty(icon)) return null;
            try
            {
                if (icon.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
                {
                    var pack = $"pack://application:,,,/{AssemblyName};component/Resources/icons/{icon.Substring("res://".Length)}";
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

                if (File.Exists(icon))
                {
                    if (icon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                        using (var r = new StreamReader(icon))
                            return SvgToImage(r.ReadToEnd());
                    return new BitmapImage(new Uri(icon, UriKind.Absolute));
                }
            }
            catch
            {
                // Any parse/load failure just shows no icon rather than crashing the UI.
            }
            return null;
        }

        // Rasterise the SVG at a higher resolution than its 16x16 design size so the icon
        // stays crisp on HiDPI displays; WPF downscales it to the displayed size.
        private const int SvgRenderSize = 64;

        private static ImageSource? SvgToImage(string svg)
        {
            try
            {
                using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svg));
                var doc = SvgDocument.Open<SvgDocument>(ms);
                // The agent SVGs use a viewBox, so overriding the pixel size just rescales the
                // vector artwork (gradients, circles, rects, url(#…) fills) without distortion.
                doc.Width = new SvgUnit(SvgUnitType.User, SvgRenderSize);
                doc.Height = new SvgUnit(SvgUnitType.User, SvgRenderSize);

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

    /// <summary>Binds an icon spec (res://… or filesystem path) to a WPF <see cref="Image"/> Source.</summary>
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
