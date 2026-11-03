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
            // One file is enough to offer it: the wheel asks for the rest afterwards.
            "merge" => request.Files.Count >= 1,
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

        // Reachable when the gathering was called off, or when a handler is driven
        // directly. Merging one file would quietly produce a copy under a new name.
        if (paths.Count < 2) return JobResult.Fail("Merging needs more than one PDF.");

        var target = OutputNaming.ForGroup(paths, ".pdf", "merged");

        await Task.Run(() =>
        {
            // Order matters, and the order is the caller's: the file the wheel was
            // opened on, then the rest as they were clicked.
            var merged = PdfMerger.Merge(paths.ToArray());
            File.WriteAllBytes(target, merged);
        }, cancellationToken).ConfigureAwait(false);

        progress?.Report(1);
        return JobResult.Ok(target);
    }

    /// <summary>
    /// Cuts one PDF into several, where the slice says.
    ///
    /// Beyond a handful of pieces the results go into a new folder beside the original
    /// rather than loose next to it. One file per page is a legitimate thing to want, but
    /// a 300-page report done that way buries the folder it came from, and there is no
    /// undo for 300 files.
    /// </summary>
    private static async Task<JobResult> SplitAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = request.Files[0];
        var options = request.Item.Options;
        options.TryGetValue(WheelItem.CustomOption, out var typed);

        var result = await Task.Run(() =>
        {
            using var document = PdfDocument.Open(source.Path);
            var count = document.NumberOfPages;

            if (count <= 1) return JobResult.Fail("Nothing to split: this PDF has a single page.");

            var parts = Parts(options, typed, count);

            if (parts.Count <= 1)
            {
                return JobResult.Fail(
                    $"That leaves the PDF in one piece. It has {count} pages.");
            }

            // Loose beside the original while they are still countable at a glance;
            // in their own folder once they are not.
            var folder = parts.Count > 4 ? OutputNaming.NextDirectory(source.Path, "pages") : null;
            if (folder is not null) Directory.CreateDirectory(folder);

            var padding = count.ToString(CultureInfo.InvariantCulture).Length;
            var baseName = Path.GetFileNameWithoutExtension(source.Path);
            var written = new List<string>();

            for (var index = 0; index < parts.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (first, last) = parts[index];
                var builder = new PdfDocumentBuilder();

                for (var page = first; page <= last; page++)
                {
                    builder.AddPage(document, page);
                }

                var label = Label(first, last, padding);
                var target = folder is null
                    ? OutputNaming.Next(source.Path, ".pdf", label)
                    : OutputNaming.NextIn(folder, baseName, ".pdf", label);

                File.WriteAllBytes(target, builder.Build());
                written.Add(target);

                progress?.Report((index + 1) / (double)parts.Count);
            }

            return JobResult.Ok(written);
        }, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// The page spans each output file covers, from whichever way the slice asked for.
    /// </summary>
    private static List<(int First, int Last)> Parts(
        IReadOnlyDictionary<string, string> options, string? typed, int count)
    {
        if (typed is not null && CustomInput.TryParseSplitPoints(typed, count, out var cuts))
        {
            return FromCuts(cuts, count);
        }

        var chunk = ReadInt(options, "chunk");
        if (chunk is > 0)
        {
            var parts = new List<(int, int)>();
            for (var first = 1; first <= count; first += chunk.Value)
            {
                parts.Add((first, Math.Min(count, first + chunk.Value - 1)));
            }

            return parts;
        }

        // "In half", and the fallback: the smallest, least surprising result there is.
        var middle = (count + 1) / 2;
        return FromCuts(new[] { middle + 1 }, count);
    }

    /// <summary>Turns the pages to cut before into the spans between them.</summary>
    private static List<(int First, int Last)> FromCuts(IReadOnlyList<int> cuts, int count)
    {
        var parts = new List<(int, int)>();
        var first = 1;

        foreach (var cut in cuts)
        {
            if (cut <= first || cut > count) continue;

            parts.Add((first, cut - 1));
            first = cut;
        }

        parts.Add((first, count));
        return parts;
    }

    /// <summary>
    /// Named after the pages inside, zero-padded so the pieces sort in reading order.
    /// </summary>
    private static string Label(int first, int last, int padding)
    {
        var from = first.ToString("D" + padding, CultureInfo.InvariantCulture);
        return first == last
            ? "p" + from
            : $"p{from}-{last.ToString("D" + padding, CultureInfo.InvariantCulture)}";
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
