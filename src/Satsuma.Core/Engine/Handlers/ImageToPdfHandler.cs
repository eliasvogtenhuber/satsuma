using Satsuma.Core.Model;
using Satsuma.Core.Tools;
using Satsuma.Core.Util;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace Satsuma.Core.Engine.Handlers;

/// <summary>
/// Images into a PDF, one page each.
///
/// Both ways of asking land here: picking PDF on the formats wheel, and Make PDF on the
/// tools wheel. They differ only in how many files they fold together, which is the
/// difference between converting each image and binding a set of them.
/// </summary>
public sealed class ImageToPdfHandler : IJobHandler
{
    private const string MakePdfId = "tool.img.pdf";

    public bool CanHandle(JobRequest request)
    {
        if (request.Category != FileCategory.Image) return false;

        return request.Item.Kind switch
        {
            WheelKind.Formats => request.Item.OutputExtension == ".pdf",
            WheelKind.Tools => request.Item.Id == MakePdfId,
            _ => false
        };
    }

    /// <summary>Make PDF binds the selection into one document; the format converts each.</summary>
    public bool IsAggregate(JobRequest request) => request.Item.Id == MakePdfId;

    public async Task<JobResult> ExecuteAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var files = request.Files;
        var target = files.Count == 1
            ? OutputNaming.Next(files[0].Path, ".pdf")
            : OutputNaming.ForGroup(files.Select(f => f.Path).ToList(), ".pdf", "pages");

        var failures = new List<string>();

        var built = await StaExecutor.RunAsync(() =>
        {
            var builder = new PdfDocumentBuilder();
            var added = 0;

            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var file = files[index];
                var image = ImageCodec.TryDecode(file.Path, out _, out var error);

                if (image is null)
                {
                    failures.Add($"{file.Name}: {error ?? "could not be read"}");
                    continue;
                }

                // The page is the image at 72 dpi, so it comes out the size the picture
                // actually is rather than being forced onto a paper size it is not.
                var width = image.PixelWidth * 72.0 / Math.Max(image.DpiX, 1);
                var height = image.PixelHeight * 72.0 / Math.Max(image.DpiY, 1);

                if (width <= 0 || height <= 0)
                {
                    width = image.PixelWidth;
                    height = image.PixelHeight;
                }

                var page = builder.AddPage(width, height);
                page.AddJpeg(ImageCodec.EncodeToJpeg(image, quality: 90),
                    new PdfRectangle(0, 0, width, height));

                added++;
                progress?.Report((index + 1) / (double)files.Count);
            }

            if (added == 0) return false;

            File.WriteAllBytes(target, builder.Build());
            return true;
        }, cancellationToken).ConfigureAwait(false);

        if (!built)
        {
            return JobResult.Fail(failures.Count > 0
                ? failures[0]
                : "None of these images could be read.");
        }

        return JobResult.Ok(target);
    }
}
