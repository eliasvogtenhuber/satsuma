using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;

namespace Satsuma.Core.Tools;

/// <summary>Rasterises PDF pages.</summary>
public static class PdfRenderer
{
    /// <summary>A PDF point is 1/72 inch, which is what a scale factor is measured against.</summary>
    private const double PdfPointsPerInch = 72.0;

    public sealed record RenderedPage(int Number, BitmapSource Image);

    /// <summary>
    /// Renders every page at the given resolution.
    ///
    /// The pages come back as raw pixels and are turned into frozen bitmaps, so they can
    /// be handed to the imaging code that already knows how to write every format
    /// Satsuma supports rather than growing a second way to save an image.
    /// </summary>
    public static IReadOnlyList<RenderedPage> Render(
        string path, int dpi = 300, CancellationToken cancellationToken = default)
    {
        var scale = dpi / PdfPointsPerInch;
        var pages = new List<RenderedPage>();

        using var library = DocLib.Instance.GetDocReader(path, new PageDimensions(scale));

        var count = library.GetPageCount();
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var page = library.GetPageReader(index);

            var width = page.GetPageWidth();
            var height = page.GetPageHeight();

            // Pages are transparent where nothing is drawn, and a PDF page is paper:
            // flattening onto white is what the page is supposed to look like.
            var pixels = page.GetImage(new NaiveTransparencyRemover(255, 255, 255));
            if (pixels.Length == 0 || width <= 0 || height <= 0) continue;

            var bitmap = BitmapSource.Create(
                width, height, dpi, dpi, PixelFormats.Bgra32, null, pixels, width * 4);

            bitmap.Freeze();
            pages.Add(new RenderedPage(index + 1, bitmap));
        }

        return pages;
    }

    public static int PageCount(string path)
    {
        using var reader = DocLib.Instance.GetDocReader(path, new PageDimensions(1));
        return reader.GetPageCount();
    }
}
