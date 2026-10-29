using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Satsuma.App.Glass;

/// <summary>
/// Works out how heavily to tint a pane of glass from how bright the thing behind it is.
///
/// One fixed translucency cannot work: Satsuma's surfaces sit over a white document one
/// moment and a dark desktop the next, and their text is light either way. Measuring what
/// is being covered, and leaning on the tint only as much as that needs, keeps the glass
/// light where it can be and legible where it must be.
///
/// Shared by the wheel and the progress card so the two cannot drift apart.
/// </summary>
public static class GlassTint
{
    /// <summary>Over a dark desktop: all but absent.</summary>
    private const double Darkest = 0x10;

    /// <summary>Over a white window: just enough to seat the text.</summary>
    private const double Brightest = 0x54;

    /// <summary>Used when the backdrop could not be measured.</summary>
    private const double AssumedLuminance = 0.35;

    /// <summary>
    /// A tint for this backdrop. <paramref name="opacityBias"/> multiplies the result, so
    /// a user preference can shift the whole scale without flattening the adaptation.
    /// </summary>
    public static Brush For(BitmapSource? backdrop, double opacityBias = 1.0)
    {
        var luminance = EstimateLuminance(backdrop) ?? AssumedLuminance;
        var baseline = Darkest + (Brightest - Darkest) * Math.Clamp(luminance, 0, 1);
        var alpha = (byte)Math.Clamp(baseline * opacityBias, 0, 255);

        var brush = new SolidColorBrush(Color.FromArgb(alpha, 0x06, 0x06, 0x09));
        brush.Freeze();
        return brush;
    }

    /// <summary>Mean perceived brightness, 0..1, or null when it cannot be read.</summary>
    public static double? EstimateLuminance(BitmapSource? image)
    {
        if (image is null || image.PixelWidth == 0 || image.PixelHeight == 0) return null;

        try
        {
            // Scaled right down first, so this is a handful of pixels rather than a sweep
            // over the whole capture every time something appears.
            const int side = 16;
            var scaled = new TransformedBitmap(image,
                new ScaleTransform((double)side / image.PixelWidth,
                                   (double)side / image.PixelHeight));

            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            converted.Freeze();

            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            if (width == 0 || height == 0) return null;

            var stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            double total = 0;
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                // Rec. 601 weights: how bright this reads, not its raw average.
                total += (0.114 * pixels[offset]        // blue
                          + 0.587 * pixels[offset + 1]  // green
                          + 0.299 * pixels[offset + 2]  // red
                         ) / 255.0;
            }

            return total / (width * height);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
