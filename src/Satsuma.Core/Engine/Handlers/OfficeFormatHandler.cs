using Satsuma.Core.Model;
using Satsuma.Core.Tools;
using Satsuma.Core.Util;

namespace Satsuma.Core.Engine.Handlers;

/// <summary>
/// Documents, spreadsheets and slides, through LibreOffice.
///
/// Slides to images take the long way round, via PDF and then Satsuma's own page
/// renderer, because LibreOffice's direct image export writes one file and silently
/// drops the rest of the deck. Going through PDF gets every slide and reuses the
/// rendering that PDF pages already use.
/// </summary>
public sealed class OfficeFormatHandler : IJobHandler
{
    private const int SlideDpi = 150;

    public bool CanHandle(JobRequest request)
    {
        if (request.Item.Kind != WheelKind.Formats) return false;
        if (!LibreOffice.IsAvailable) return false;

        var extension = request.Item.OutputExtension;
        if (extension is null) return false;

        return request.Category switch
        {
            FileCategory.Document => extension is ".docx" or ".pdf" or ".txt" or ".html" or ".epub",
            FileCategory.Spreadsheet => extension is ".xlsx" or ".csv" or ".tsv" or ".ods" or ".pdf",
            FileCategory.Slides => extension is ".pdf" or ".jpg" or ".png",
            _ => false
        };
    }

    public async Task<JobResult> ExecuteAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = request.Files[0];
        var extension = request.Item.OutputExtension!;

        if (request.Category == FileCategory.Slides && extension is ".jpg" or ".png")
        {
            return await SlidesToImagesAsync(source, extension, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        var filter = FilterFor(request.Category, extension);
        if (filter is null) return JobResult.Fail($"No way to write {extension} from this.");

        progress?.Report(0.1);

        var staging = StagingDirectory();
        try
        {
            var produced = await LibreOffice
                .ConvertAsync(source.Path, filter, staging, cancellationToken)
                .ConfigureAwait(false);

            if (produced is null)
            {
                return JobResult.Fail("LibreOffice could not convert this file.");
            }

            var target = OutputNaming.Next(source.Path, extension);
            File.Move(produced, target, overwrite: false);

            progress?.Report(1);
            return JobResult.Ok(target);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>Every slide as its own image, by way of a PDF.</summary>
    private static async Task<JobResult> SlidesToImagesAsync(
        SourceFile source, string extension, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var staging = StagingDirectory();
        try
        {
            var pdf = await LibreOffice
                .ConvertAsync(source.Path, "pdf", staging, cancellationToken)
                .ConfigureAwait(false);

            if (pdf is null) return JobResult.Fail("LibreOffice could not read this deck.");

            progress?.Report(0.5);

            var outputs = await StaExecutor.RunAsync(() =>
            {
                var written = new List<string>();
                var pages = PdfRenderer.Render(pdf, SlideDpi, cancellationToken);

                var padding = Math.Max(2, pages.Count.ToString().Length);

                foreach (var page in pages)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var suffix = pages.Count == 1 ? null : page.Number.ToString("D" + padding);
                    var target = OutputNaming.Next(source.Path, extension, suffix);

                    ImageCodec.SaveWithWic(page.Image, target, quality: 92);
                    written.Add(target);
                }

                return written;
            }, cancellationToken).ConfigureAwait(false);

            progress?.Report(1);

            return outputs.Count == 0
                ? JobResult.Fail("This deck has no slides to render.")
                : JobResult.Ok(outputs);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// The LibreOffice filter that writes a format.
    ///
    /// Named explicitly rather than left to the extension alone, because an extension can
    /// be ambiguous: several filters write .csv, and the separator is an argument to the
    /// filter rather than a format of its own, which is the only way to get tabs.
    /// </summary>
    private static string? FilterFor(FileCategory category, string extension) =>
        (category, extension) switch
        {
            (FileCategory.Document, ".docx") => "docx:MS Word 2007 XML",
            (FileCategory.Document, ".pdf") => "pdf:writer_pdf_Export",
            (FileCategory.Document, ".txt") => "txt:Text (encoded):UTF8",
            (FileCategory.Document, ".html") => "html:HTML (StarWriter)",
            (FileCategory.Document, ".epub") => "epub",

            (FileCategory.Spreadsheet, ".xlsx") => "xlsx:Calc MS Excel 2007 XML",
            (FileCategory.Spreadsheet, ".ods") => "ods",
            (FileCategory.Spreadsheet, ".pdf") => "pdf:calc_pdf_Export",

            // 44 is a comma, 9 a tab; 34 quotes fields, 76 is UTF-8, 1 is the first sheet.
            (FileCategory.Spreadsheet, ".csv") => "csv:Text - txt - csv (StarCalc):44,34,76,1",
            (FileCategory.Spreadsheet, ".tsv") => "csv:Text - txt - csv (StarCalc):9,34,76,1",

            (FileCategory.Slides, ".pdf") => "pdf:impress_pdf_Export",

            _ => null
        };

    private static string StagingDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "Satsuma", "office", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
