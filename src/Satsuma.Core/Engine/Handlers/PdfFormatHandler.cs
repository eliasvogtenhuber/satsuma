using System.Text;
using Satsuma.Core.Model;
using Satsuma.Core.Tools;
using Satsuma.Core.Util;
using UglyToad.PdfPig;

namespace Satsuma.Core.Engine.Handlers;

/// <summary>
/// PDF out to images and to text.
///
/// Pages are rasterised for the image formats and read for the text ones. What is
/// deliberately absent is DOCX, PPTX and XLSX: turning a page description back into an
/// editable document is a layout reconstruction problem, not a conversion, and anything
/// short of that produces a file that looks nothing like the PDF. Those stay off the
/// wheel until they can be done properly.
/// </summary>
public sealed class PdfFormatHandler : IJobHandler
{
    /// <summary>Pages come out at print resolution, as Tangerine's do.</summary>
    private const int RenderDpi = 300;

    public bool CanHandle(JobRequest request)
    {
        if (request.Category != FileCategory.Pdf) return false;
        if (request.Item.Kind != WheelKind.Formats) return false;

        return request.Item.OutputExtension switch
        {
            ".png" or ".jpg" => true,
            ".txt" or ".md" => true,
            _ => false
        };
    }

    public async Task<JobResult> ExecuteAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = request.Files[0];
        var extension = request.Item.OutputExtension!;

        return extension switch
        {
            ".txt" => await ExtractTextAsync(source, plain: true, progress, cancellationToken)
                .ConfigureAwait(false),
            ".md" => await ExtractTextAsync(source, plain: false, progress, cancellationToken)
                .ConfigureAwait(false),
            _ => await RenderPagesAsync(source, extension, progress, cancellationToken)
                .ConfigureAwait(false)
        };
    }

    /// <summary>One image per page, numbered, beside the original.</summary>
    private static async Task<JobResult> RenderPagesAsync(
        SourceFile source, string extension, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        // Rendering and saving both touch imaging, so the whole lot runs on one STA
        // thread rather than hopping per page.
        var outputs = await StaExecutor.RunAsync(() =>
        {
            var written = new List<string>();
            var pages = PdfRenderer.Render(source.Path, RenderDpi, cancellationToken);

            for (var index = 0; index < pages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = pages[index];

                // A single-page PDF keeps the plain name; more than one gets numbered,
                // padded so they sort correctly in Explorer.
                var suffix = pages.Count == 1
                    ? null
                    : page.Number.ToString("D" + Math.Max(2, pages.Count.ToString().Length));

                var target = OutputNaming.Next(source.Path, extension, suffix);

                ImageCodec.SaveWithWic(page.Image, target, quality: 92);
                written.Add(target);

                progress?.Report((index + 1) / (double)pages.Count);
            }

            return written;
        }, cancellationToken).ConfigureAwait(false);

        return outputs.Count == 0
            ? JobResult.Fail("This PDF has no pages to render.")
            : JobResult.Ok(outputs);
    }

    private static async Task<JobResult> ExtractTextAsync(
        SourceFile source, bool plain, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var extension = plain ? ".txt" : ".md";
        var target = OutputNaming.Next(source.Path, extension);

        var text = await Task.Run(() =>
        {
            var builder = new StringBuilder();

            using var document = PdfDocument.Open(source.Path);
            var pages = document.GetPages().ToList();

            if (!plain)
            {
                builder.Append("# ")
                       .AppendLine(Path.GetFileNameWithoutExtension(source.Path))
                       .AppendLine();
            }

            for (var index = 0; index < pages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = pages[index];

                // Markdown gets a heading per page, since a PDF carries no headings of
                // its own to recover and the page is the only structure there is.
                if (!plain) builder.Append("## Page ").Append(page.Number).AppendLine().AppendLine();

                builder.AppendLine(Tidy(page.Text));
                builder.AppendLine();

                progress?.Report((index + 1) / (double)pages.Count);
            }

            return builder.ToString();
        }, cancellationToken).ConfigureAwait(false);

        if (text.Trim().Length == 0)
        {
            return JobResult.Fail("No text in this PDF. It is probably scanned pages.");
        }

        await File.WriteAllTextAsync(target, text, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        return JobResult.Ok(target);
    }

    /// <summary>
    /// PDF text comes out as one run per page with no line breaks of its own. This puts
    /// a break where a sentence ends, which is the only hint of structure available
    /// without reconstructing the layout.
    /// </summary>
    private static string Tidy(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var collapsed = text.Replace("\r", string.Empty);
        var builder = new StringBuilder(collapsed.Length);

        for (var index = 0; index < collapsed.Length; index++)
        {
            var current = collapsed[index];
            builder.Append(current);

            if (current is not ('.' or '!' or '?')) continue;
            if (index + 1 >= collapsed.Length || collapsed[index + 1] != ' ') continue;

            // Only when what follows starts a new sentence, so "e.g. this" survives.
            var next = index + 2 < collapsed.Length ? collapsed[index + 2] : '\0';
            if (char.IsUpper(next))
            {
                builder.Append('\n');
                index++;   // the space is now the break
            }
        }

        return builder.ToString().Trim();
    }
}
