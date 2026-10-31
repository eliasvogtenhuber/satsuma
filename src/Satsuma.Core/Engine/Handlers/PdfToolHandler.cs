using System.Globalization;
using Satsuma.Core.Model;
using Satsuma.Core.Tools;
using Satsuma.Core.Util;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace Satsuma.Core.Engine.Handlers;

/// <summary>PDF tools: merge, split and compress.</summary>
public sealed class PdfToolHandler : IJobHandler
{
    private const string Prefix = "tool.pdf.";

    public bool CanHandle(JobRequest request)
    {
        if (request.Category != FileCategory.Pdf) return false;
        if (request.Item.Kind != WheelKind.Tools) return false;

        return Operation(request.Item.Id) switch
        {
            "merge" => request.Files.Count > 1,
            "split" or "compress" or "pages" => true,
            _ => false
        };
    }

    /// <summary>Merge folds the selection into one file; the others work per file.</summary>
    public bool IsAggregate(JobRequest request) => Operation(request.Item.Id) == "merge";

    public async Task<JobResult> ExecuteAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        return Operation(request.Item.Id) switch
        {
            "merge" => await MergeAsync(request, progress, cancellationToken).ConfigureAwait(false),
            "split" => await SplitAsync(request, progress, cancellationToken).ConfigureAwait(false),
            "compress" => await CompressAsync(request, progress, cancellationToken).ConfigureAwait(false),
            "pages" => await KeepPagesAsync(request, progress, cancellationToken).ConfigureAwait(false),
            _ => JobResult.Fail($"Unknown PDF tool {request.Item.Id}.")
        };
    }

    private static async Task<JobResult> MergeAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var paths = request.Files.Select(f => f.Path).ToList();
        var target = OutputNaming.ForGroup(paths, ".pdf", "merged");

        await Task.Run(() =>
        {
            // Order matters and is the order they were selected in, which is the order
            // Explorer reports them.
            var merged = PdfMerger.Merge(paths.ToArray());
            File.WriteAllBytes(target, merged);
        }, cancellationToken).ConfigureAwait(false);

        progress?.Report(1);
        return JobResult.Ok(target);
    }

    private static async Task<JobResult> SplitAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = request.Files[0];

        var outputs = await Task.Run(() =>
        {
            var written = new List<string>();

            using var document = PdfDocument.Open(source.Path);
            var count = document.NumberOfPages;

            if (count <= 1) return written;

            var padding = Math.Max(2, count.ToString(CultureInfo.InvariantCulture).Length);

            for (var page = 1; page <= count; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var builder = new PdfDocumentBuilder();
                builder.AddPage(document, page);

                var target = OutputNaming.Next(
                    source.Path, ".pdf", page.ToString("D" + padding, CultureInfo.InvariantCulture));

                File.WriteAllBytes(target, builder.Build());
                written.Add(target);

                progress?.Report(page / (double)count);
            }

            return written;
        }, cancellationToken).ConfigureAwait(false);

        return outputs.Count == 0
            ? JobResult.Fail("Nothing to split: this PDF has a single page.")
            : JobResult.Ok(outputs);
    }

    /// <summary>
    /// Keeps a range of pages and discards the rest, into one new PDF.
    ///
    /// This is what people usually mean by splitting a PDF: not one file per page, but
    /// the handful of pages worth keeping. It needs page numbers, which no preset can
    /// guess, so the slice asks for them.
    /// </summary>
    private static async Task<JobResult> KeepPagesAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = request.Files[0];

        if (!request.Item.Options.TryGetValue(WheelItem.CustomOption, out var typed))
        {
            return JobResult.Fail("No pages were given.");
        }

        var result = await Task.Run(() =>
        {
            using var document = PdfDocument.Open(source.Path);
            var count = document.NumberOfPages;

            if (!CustomInput.TryParsePageRange(typed, count, out var pages))
            {
                return JobResult.Fail(count == 1
                    ? "This PDF has a single page."
                    : $"No pages matched. This PDF has {count}.");
            }

            var builder = new PdfDocumentBuilder();

            for (var index = 0; index < pages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                builder.AddPage(document, pages[index]);
                progress?.Report((index + 1) / (double)pages.Count);
            }

            // Named after what was kept, so several goes at one PDF do not collide.
            var label = pages.Count == 1
                ? $"p{pages[0]}"
                : $"p{pages[0]}-{pages[^1]}";

            var target = OutputNaming.Next(source.Path, ".pdf", label);
            File.WriteAllBytes(target, builder.Build());

            return JobResult.Ok(target);
        }, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Compresses by rasterising.
    ///
    /// This is worth being plain about: the pages are rendered and re-encoded as JPEG, so
    /// the result is pictures of the pages. It gets a big, predictable size reduction on
    /// the scanned and image-heavy PDFs people actually want to shrink, and it costs the
    /// text layer, so the result is no longer selectable or searchable. Rewriting the
    /// streams in place would keep the text, but it only helps on PDFs that are already
    /// mostly text, which are already small.
    /// </summary>
    private static async Task<JobResult> CompressAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = request.Files[0];
        var options = request.Item.Options;
        var quality = options.TryGetValue(WheelItem.CustomOption, out var typed)
                      && CustomInput.TryParseQuality(typed, out var custom)
            ? custom
            : ReadInt(options, "quality") ?? 75;

        // Lower quality also means fewer pixels: at the strong end the resolution is what
        // dominates the size, not the JPEG table.
        var dpi = quality switch
        {
            >= 90 => 200,
            >= 70 => 150,
            >= 45 => 110,
            _ => 80
        };

        var target = OutputNaming.Next(source.Path, ".pdf", "compressed");

        var built = await StaExecutor.RunAsync(() =>
        {
            var pages = PdfRenderer.Render(source.Path, dpi, cancellationToken);
            if (pages.Count == 0) return false;

            var builder = new PdfDocumentBuilder();

            for (var index = 0; index < pages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = pages[index];
                var jpeg = ImageCodec.EncodeToJpeg(page.Image, quality);

                // Back to points, so the page keeps the size it had on paper.
                var width = page.Image.PixelWidth * 72.0 / dpi;
                var height = page.Image.PixelHeight * 72.0 / dpi;

                var sheet = builder.AddPage(width, height);
                sheet.AddJpeg(jpeg, new UglyToad.PdfPig.Core.PdfRectangle(0, 0, width, height));

                progress?.Report((index + 1) / (double)pages.Count);
            }

            File.WriteAllBytes(target, builder.Build());
            return true;
        }, cancellationToken).ConfigureAwait(false);

        if (!built) return JobResult.Fail("This PDF has no pages to compress.");

        // A "compressed" file that grew is not a result worth keeping.
        var before = new FileInfo(source.Path).Length;
        var after = new FileInfo(target).Length;

        if (after >= before)
        {
            ImagePipeline.TryDelete(target);
            return JobResult.Fail("Already as small as this can usefully make it.");
        }

        return JobResult.Ok(target);
    }

    private static string? Operation(string id)
    {
        if (!id.StartsWith(Prefix, StringComparison.Ordinal)) return null;

        var operation = id[Prefix.Length..].Split('.')[0];
        return operation is "merge" or "split" or "compress" or "pages" ? operation : null;
    }

    private static int? ReadInt(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var raw)
        && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
