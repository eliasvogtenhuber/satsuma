using System.Windows.Media;
using System.Windows.Media.Imaging;
using Satsuma.Core.Catalog;
using Satsuma.Core.Engine;
using Satsuma.Core.Model;
using Satsuma.Core.Tools;

namespace Satsuma.SelfTest;

/// <summary>
/// Drives the engine over generated sample files and reports what really converts on this
/// machine. It exists because the wheel cannot be clicked from a test: this covers
/// everything behind it, so a failure after a drag is a UI problem and nothing else.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static int _skipped;

    private static readonly JobRunner Runner = new();

    private static async Task<int> Main()
    {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

        var workspace = Path.Combine(Path.GetTempPath(), "Satsuma.SelfTest",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(workspace);

        Console.WriteLine("Satsuma self-test");
        Console.WriteLine($"  workspace : {workspace}");
        Console.WriteLine($"  ffmpeg    : {FfmpegLocator.Ffmpeg ?? "not found"}");
        Console.WriteLine($"  ffprobe   : {FfmpegLocator.Ffprobe ?? "not found"}");
        Console.WriteLine();

        try
        {
            TestCustomInput();
            TestExplorerTitles();
            TestHitTesting();
            await TestWheelBuilding(workspace);
            await TestImages(workspace);
            await TestImageTools(workspace);
            await TestPdf(workspace);
            await TestOffice(workspace);
            await TestSubtitles(workspace);
            await TestArchives(workspace);

            if (FfmpegLocator.IsAvailable)
            {
                await TestAudio(workspace);
                await TestVideo(workspace);
                await TestVideoTools(workspace);
                await TestAudioTools(workspace);
            }
            else
            {
                Section("Audio and video");
                Skip("everything", "ffmpeg not installed");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"Harness blew up: {ex}");
            _failed++;
        }

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed, {_skipped} skipped");
        Console.WriteLine($"Output left in {workspace}");

        return _failed == 0 ? 0 : 1;
    }

    // ---- Typed values -------------------------------------------------------

    /// <summary>
    /// What people type into a Custom slice. Driven directly, because the worth of it is
    /// in the odd spellings rather than in the window that collects them.
    /// </summary>
    private static void TestCustomInput()
    {
        Section("Typed values");

        // Sizes, in all the ways a size gets written.
        Check("a percentage", Size("50%")?.Scale == 0.5, "50% is half");
        Check("a percentage with a space", Size("50 %")?.Scale == 0.5, "spaces ignored");
        Check("a bare small number is a percentage", Size("50")?.Scale == 0.5,
            "nobody types 50 meaning fifty pixels");
        Check("a bare large number is a long edge", Size("1080")?.LongEdge == 1080,
            "1080 is a long edge");
        Check("exact dimensions", Size("1200x800") is { Width: 1200, Height: 800 },
            "1200x800");
        Check("exact dimensions with spaces and a cross",
            Size("1200 × 800") is { Width: 1200, Height: 800 }, "1200 × 800");
        Check("a decimal comma", Size("12,5%")?.Scale == 0.125, "12,5% is an eighth");
        Check("nonsense is refused", Size("wide") is null && Size("") is null, "no size");
        Check("zero is refused", Size("0%") is null, "nothing would be left");

        // Quality.
        Check("a quality", CustomInput.TryParseQuality("80", out var q) && q == 80, "80");
        Check("quality is bounded",
            !CustomInput.TryParseQuality("0", out _) && !CustomInput.TryParseQuality("101", out _),
            "1 to 100");

        // Bitrate.
        Check("a bitrate", CustomInput.TryParseBitrate("128", out var b) && b == "128k", "128 -> 128k");
        Check("a bitrate already in k",
            CustomInput.TryParseBitrate("96k", out var b2) && b2 == "96k", "96k stays");

        // Speed.
        Check("a speed", CustomInput.TryParseSpeed("1.5", out var s) && Math.Abs(s - 1.5) < 0.001, "1.5");
        Check("a speed with an x",
            CustomInput.TryParseSpeed("2x", out var s2) && Math.Abs(s2 - 2) < 0.001, "2x");

        // Page ranges, which is where the comma means something else entirely.
        Check("a single page", Pages("3", 10) is [3], "3");
        Check("a range", Pages("3-5", 10) is [3, 4, 5], "3-5");
        Check("a list", Pages("1,4,9", 10) is [1, 4, 9], "1,4,9");
        Check("a list is not a decimal", Pages("2,5", 10) is [2, 5],
            "2,5 is two pages, not two and a half");
        Check("open ended upwards", Pages("8-", 10) is [8, 9, 10], "8- to the end");
        Check("open ended downwards", Pages("-3", 10) is [1, 2, 3], "-3 from the start");
        Check("mixed and tidied", Pages("5,3,3-4", 10) is [3, 4, 5], "sorted, no repeats");
        Check("a backwards range still works", Pages("7-5", 10) is [5, 6, 7], "7-5 reads as 5-7");
        Check("past the end is trimmed", Pages("9-20", 10) is [9, 10], "clamped to the document");
        Check("entirely past the end is refused", Pages("20-30", 10) is null, "no pages matched");

        // Split points, which are page numbers to cut before rather than pages to keep.
        Check("one cut", Cuts("5", 10) is [5], "5");
        Check("several cuts", Cuts("3,6,9", 10) is [3, 6, 9], "3,6,9");
        Check("cuts may be spaced", Cuts("3 6 9", 10) is [3, 6, 9], "spaces separate too");
        Check("cutting before page one does nothing", Cuts("1", 10) is null,
            "nothing precedes page 1");
        Check("cuts are sorted and deduplicated", Cuts("7,3,7", 10) is [3, 7], "3, 7");
        Check("cuts past the end are dropped", Cuts("4,40", 10) is [4], "4 survives");
        Check("a single page cannot be cut", Cuts("2", 1) is null, "one page, no cuts");
        Check("a range is not a cut point", Cuts("3-6", 10) is null, "cuts are single numbers");

        return;

        static SizeRequest? Size(string text) =>
            CustomInput.TryParseSize(text, out var size) ? size : null;

        static IReadOnlyList<int>? Pages(string text, int count) =>
            CustomInput.TryParsePageRange(text, count, out var pages) ? pages : null;

        static IReadOnlyList<int>? Cuts(string text, int count) =>
            CustomInput.TryParseSplitPoints(text, count, out var cuts) ? cuts : null;
    }

    // ---- Office ------------------------------------------------------------

    /// <summary>
    /// Documents, spreadsheets and slides. Skipped wholesale when LibreOffice is absent,
    /// the same way the wheel hides those slices.
    /// </summary>
    private static async Task TestOffice(string workspace)
    {
        Section("Documents, spreadsheets and slides");

        if (!LibreOffice.IsAvailable)
        {
            Skip("everything", "LibreOffice not installed");

            // The important half still holds without it: nothing is offered.
            var hidden = Runner.BuildWheel(WheelKind.Formats, FileCategory.Document,
                new[] { new SourceFile(Path.Combine(workspace, "x.docx")) });

            Check("document slices hidden without LibreOffice", hidden.Count == 0, "nothing offered");
            return;
        }

        // A document, starting from text, which LibreOffice reads directly.
        var txt = Path.Combine(workspace, "office-source.txt");
        await File.WriteAllTextAsync(txt,
            "Satsuma converts files.\r\nIt runs entirely on this machine.\r\n");

        var documentFormats = FormatCatalog.For(FileCategory.Document);

        var docx = await Run(documentFormats.First(i => i.OutputExtension == ".docx"),
            new[] { new SourceFile(txt) }, FileCategory.Document);

        Check("txt -> docx", docx.Outputs.Count == 1 && File.Exists(docx.Outputs[0]), Describe(docx));

        if (docx.Outputs.Count == 1)
        {
            // A real DOCX is a zip; anything else means LibreOffice wrote something odd.
            using var archive = System.IO.Compression.ZipFile.OpenRead(docx.Outputs[0]);
            Check("the docx is a real Office package",
                archive.Entries.Any(e => e.FullName == "word/document.xml"),
                "contains word/document.xml");

            var source = new[] { new SourceFile(docx.Outputs[0]) };

            var pdf = await Run(documentFormats.First(i => i.OutputExtension == ".pdf"),
                source, FileCategory.Document);

            Check("docx -> pdf", pdf.Outputs.Count == 1, Describe(pdf));

            if (pdf.Outputs.Count == 1)
            {
                Check("the pdf has a page", PdfRenderer.PageCount(pdf.Outputs[0]) >= 1,
                    $"{PdfRenderer.PageCount(pdf.Outputs[0])} pages");
            }

            var backToText = await Run(documentFormats.First(i => i.OutputExtension == ".txt"),
                source, FileCategory.Document);

            Check("docx -> txt", backToText.Outputs.Count == 1, Describe(backToText));

            if (backToText.Outputs.Count == 1)
            {
                var content = await File.ReadAllTextAsync(backToText.Outputs[0]);
                Check("the words survive the round trip",
                    content.Contains("Satsuma converts files"), "text recovered");
            }

            await RunAndCheck("docx -> html",
                documentFormats.First(i => i.OutputExtension == ".html"),
                docx.Outputs[0], FileCategory.Document);

            await RunAndCheck("docx -> epub",
                documentFormats.First(i => i.OutputExtension == ".epub"),
                docx.Outputs[0], FileCategory.Document);
        }

        // A spreadsheet, starting from CSV.
        var csv = Path.Combine(workspace, "office-sheet.csv");
        await File.WriteAllTextAsync(csv, "Name,Size\r\nholiday.png,2048\r\nreport.pdf,4096\r\n");

        var sheetFormats = FormatCatalog.For(FileCategory.Spreadsheet);

        var xlsx = await Run(sheetFormats.First(i => i.OutputExtension == ".xlsx"),
            new[] { new SourceFile(csv) }, FileCategory.Spreadsheet);

        Check("csv -> xlsx", xlsx.Outputs.Count == 1, Describe(xlsx));

        if (xlsx.Outputs.Count == 1)
        {
            var source = new[] { new SourceFile(xlsx.Outputs[0]) };

            var tsv = await Run(sheetFormats.First(i => i.OutputExtension == ".tsv"),
                source, FileCategory.Spreadsheet);

            Check("xlsx -> tsv", tsv.Outputs.Count == 1, Describe(tsv));

            if (tsv.Outputs.Count == 1)
            {
                var content = await File.ReadAllTextAsync(tsv.Outputs[0]);

                // The separator is the whole point of tsv being its own slice.
                Check("tsv really is tab separated",
                    content.Contains('\t') && content.Contains("holiday.png"),
                    "tabs, and the rows came through");
            }

            await RunAndCheck("xlsx -> ods",
                sheetFormats.First(i => i.OutputExtension == ".ods"),
                xlsx.Outputs[0], FileCategory.Spreadsheet);

            await RunAndCheck("xlsx -> pdf",
                sheetFormats.First(i => i.OutputExtension == ".pdf"),
                xlsx.Outputs[0], FileCategory.Spreadsheet);
        }

        // A deck to test against, produced by Impress from a plain outline.
        await TestSlides(workspace);
    }

    /// <summary>
    /// Slides, including the image export, which is the one that goes the long way round
    /// through PDF so that every slide comes out rather than only the first.
    /// </summary>
    private static async Task TestSlides(string workspace)
    {
        var staging = Path.Combine(workspace, "deck-staging");
        Directory.CreateDirectory(staging);

        // A deck has to come from somewhere, and a plain text outline will not do: the
        // extension decides the import filter, so .txt is read by Writer whatever is
        // asked for. Flat ODF is a presentation in a single XML file, which Impress reads
        // natively, so two slides can be written here and turned into a real PPTX.
        var flat = Path.Combine(workspace, "deck.fodp");
        await File.WriteAllTextAsync(flat,
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document
              xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
              xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0"
              xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
              xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0"
              office:version="1.2"
              office:mimetype="application/vnd.oasis.opendocument.presentation">
             <office:body>
              <office:presentation>
               <draw:page draw:name="page1">
                <draw:frame svg:width="20cm" svg:height="3cm" svg:x="2cm" svg:y="4cm">
                 <draw:text-box><text:p>First slide</text:p></draw:text-box>
                </draw:frame>
               </draw:page>
               <draw:page draw:name="page2">
                <draw:frame svg:width="20cm" svg:height="3cm" svg:x="2cm" svg:y="4cm">
                 <draw:text-box><text:p>Second slide</text:p></draw:text-box>
                </draw:frame>
               </draw:page>
              </office:presentation>
             </office:body>
            </office:document>
            """);

        var pptx = await LibreOffice.ConvertAsync(flat, "pptx:Impress MS PowerPoint 2007 XML", staging);

        if (pptx is null)
        {
            Skip("slides", "could not produce a deck to test with");
            return;
        }

        var deck = Path.Combine(workspace, "deck.pptx");
        File.Move(pptx, deck, overwrite: true);

        var slideFormats = FormatCatalog.For(FileCategory.Slides);
        var source = new[] { new SourceFile(deck) };

        var pdf = await Run(slideFormats.First(i => i.OutputExtension == ".pdf"),
            source, FileCategory.Slides);

        Check("pptx -> pdf", pdf.Outputs.Count == 1, Describe(pdf));

        var png = await Run(slideFormats.First(i => i.OutputExtension == ".png"),
            source, FileCategory.Slides);

        Check("pptx -> one png per slide", png.Outputs.Count == 2, Describe(png));

        if (png.Outputs.Count >= 1)
        {
            var image = Sta(() => ImageCodec.TryDecode(png.Outputs[0], out _));
            Check("the slide image is a real picture",
                image is not null && image.PixelWidth > 500,
                image is null ? "unreadable" : $"{image.PixelWidth}x{image.PixelHeight}");
        }
    }

    // ---- PDF ---------------------------------------------------------------

    /// <summary>
    /// PDF in both directions. The sample is built by Satsuma itself, from images, which
    /// means the images-to-PDF path has to work before anything else can be tested and
    /// nothing external has to be checked in.
    /// </summary>
    private static async Task TestPdf(string workspace)
    {
        Section("PDF");

        var first = Path.Combine(workspace, "pdf-page-1.png");
        var second = Path.Combine(workspace, "pdf-page-2.png");
        WriteTestPng(first, 600, 800);
        WriteTestPng(second, 600, 800);

        var makePdf = Leaf(ToolCatalog.For(FileCategory.Image), "tool.img.pdf");
        var bound = await Run(makePdf,
            new[] { new SourceFile(first), new SourceFile(second) }, FileCategory.Image);

        Check("images -> one PDF", bound.Outputs.Count == 1 && File.Exists(bound.Outputs[0]),
            Describe(bound));

        if (bound.Outputs.Count != 1) return;

        var pdf = bound.Outputs[0];
        Check("the PDF has a page per image", PdfRenderer.PageCount(pdf) == 2,
            $"{PdfRenderer.PageCount(pdf)} pages");

        // Single image through the formats wheel, which is the other way in.
        var pdfFormat = FormatCatalog.For(FileCategory.Image).First(i => i.OutputExtension == ".pdf");
        await RunAndCheck("one image -> PDF", pdfFormat, first, FileCategory.Image);

        // Back out again, as images.
        var toPng = FormatCatalog.For(FileCategory.Pdf).First(i => i.OutputExtension == ".png");
        var rendered = await Run(toPng, new[] { new SourceFile(pdf) }, FileCategory.Pdf);

        Check("PDF -> one PNG per page", rendered.Outputs.Count == 2, Describe(rendered));

        if (rendered.Outputs.Count == 2)
        {
            var page = Sta(() => ImageCodec.TryDecode(rendered.Outputs[0], out _));
            Check("rendered pages are at print resolution",
                page is not null && page.PixelWidth > 1500,
                page is null ? "unreadable" : $"{page.PixelWidth}x{page.PixelHeight} at 300 dpi");
        }

        var tools = ToolCatalog.For(FileCategory.Pdf);

        // Split, then merge the halves back, which checks both against each other.
        var split = await Run(Leaf(tools, "tool.pdf.split.1"),
            new[] { new SourceFile(pdf) }, FileCategory.Pdf);

        Check("PDF -> one file per page", split.Outputs.Count == 2, Describe(split));

        if (split.Outputs.Count == 2)
        {
            Check("each split file holds one page",
                PdfRenderer.PageCount(split.Outputs[0]) == 1
                && PdfRenderer.PageCount(split.Outputs[1]) == 1,
                "both single-page");

            var merged = await Run(Leaf(tools, "tool.pdf.merge"),
                split.Outputs.Select(p => new SourceFile(p)).ToList(), FileCategory.Pdf);

            Check("merge puts them back together",
                merged.Outputs.Count == 1 && PdfRenderer.PageCount(merged.Outputs[0]) == 2,
                Describe(merged));
        }

        await TestSplitting(workspace, pdf, tools);

        // Keeping a range, which is what people usually mean by splitting a PDF.
        var keep = Leaf(tools, "tool.pdf.pages") with
        {
            Options = new Dictionary<string, string> { [WheelItem.CustomOption] = "2" }
        };

        var kept = await Run(keep, new[] { new SourceFile(pdf) }, FileCategory.Pdf);

        Check("keep a page range", kept.Outputs.Count == 1, Describe(kept));

        if (kept.Outputs.Count == 1)
        {
            Check("only the asked-for page is kept",
                PdfRenderer.PageCount(kept.Outputs[0]) == 1,
                $"{PdfRenderer.PageCount(kept.Outputs[0])} of 2 pages");
        }

        var noSuchPage = await Run(
            keep with
            {
                Options = new Dictionary<string, string> { [WheelItem.CustomOption] = "40-50" }
            },
            new[] { new SourceFile(pdf) }, FileCategory.Pdf);

        Check("pages past the end are reported",
            noSuchPage.Outputs.Count == 0
            && noSuchPage.Failures.Count == 1
            && noSuchPage.Failures[0].Contains("No pages matched"),
            noSuchPage.Failures.Count > 0 ? noSuchPage.Failures[0] : "wrote a file anyway");

        // Merge needs more than one file to mean anything.
        var singleMerge = new JobRequest(
            Leaf(tools, "tool.pdf.merge"), new[] { new SourceFile(pdf) }, FileCategory.Pdf);
        Check("merge hidden for one file", !Runner.CanHandle(singleMerge), "slice hidden");

        // Compression needs a PDF worth compressing: a page carrying far more pixels than
        // its paper size needs, which is the shape of the scans people want to shrink.
        var dense = Path.Combine(workspace, "pdf-dense.png");
        WriteTestPng(dense, 1800, 2400, dpi: 300);

        var densePdf = await Run(pdfFormat, new[] { new SourceFile(dense) }, FileCategory.Image);

        if (densePdf.Outputs.Count == 1)
        {
            var compressed = await Run(Leaf(tools, "tool.pdf.compress.50"),
                new[] { new SourceFile(densePdf.Outputs[0]) }, FileCategory.Pdf);

            Check("compress a page-heavy PDF", compressed.Outputs.Count == 1, Describe(compressed));

            if (compressed.Outputs.Count == 1)
            {
                var before = new FileInfo(densePdf.Outputs[0]).Length;
                var after = new FileInfo(compressed.Outputs[0]).Length;

                Check("compress shrinks it", after < before, $"{Kb(before)} -> {Kb(after)}");
                Check("compressed PDF keeps its page",
                    PdfRenderer.PageCount(compressed.Outputs[0]) == 1, "still one page");
            }
        }

        // Refusing to make a file bigger is a result, not a failure.
        var alreadySmall = await Run(Leaf(tools, "tool.pdf.compress.50"),
            new[] { new SourceFile(pdf) }, FileCategory.Pdf);

        Check("compress refuses to grow a file",
            alreadySmall.Outputs.Count == 0 && alreadySmall.Failures.Count == 1,
            alreadySmall.Failures.Count > 0 ? alreadySmall.Failures[0] : "wrote a bigger file");

        // Text out of a PDF that has none is reported, not written as an empty file.
        var toText = FormatCatalog.For(FileCategory.Pdf).First(i => i.OutputExtension == ".txt");
        var text = await Run(toText, new[] { new SourceFile(pdf) }, FileCategory.Pdf);

        Check("a scanned PDF reports having no text",
            text.Outputs.Count == 0 && text.Failures.Count == 1,
            text.Failures.Count > 0 ? text.Failures[0] : "wrote a file anyway");

        await TestPdfText(workspace);

        // The formats that need real layout reconstruction stay off the wheel.
        var offered = Runner.BuildWheel(WheelKind.Formats, FileCategory.Pdf,
            new[] { new SourceFile(pdf) });

        Check("PDF wheel offers images and text",
            offered.Any(i => i.OutputExtension == ".png")
            && offered.Any(i => i.OutputExtension == ".txt"),
            string.Join(", ", offered.Select(i => i.Label)));

        Check("DOCX, PPTX and XLSX stay hidden",
            !offered.Any(i => i.OutputExtension is ".docx" or ".pptx" or ".xlsx"),
            "not offered until they can be done properly");
    }

    /// <summary>
    /// The ways of cutting one PDF into several, against a document long enough for them
    /// to differ — which is also where one-file-per-page stops being a sensible default.
    /// </summary>
    private static async Task TestSplitting(
        string workspace, string twoPager, IReadOnlyList<WheelItem> tools)
    {
        // Four copies of the two-pager merged into one eight-page document.
        var copies = new List<SourceFile>();
        for (var index = 1; index <= 4; index++)
        {
            var copy = Path.Combine(workspace, $"pdf-part-{index}.pdf");
            File.Copy(twoPager, copy, overwrite: true);
            copies.Add(new SourceFile(copy));
        }

        var built = await Run(Leaf(tools, "tool.pdf.merge"), copies, FileCategory.Pdf);
        if (built.Outputs.Count != 1) return;

        var long8 = built.Outputs[0];
        Check("an eight-page document to split", PdfRenderer.PageCount(long8) == 8,
            $"{PdfRenderer.PageCount(long8)} pages");

        var half = await Run(Leaf(tools, "tool.pdf.split.half"),
            new[] { new SourceFile(long8) }, FileCategory.Pdf);

        Check("split in half", half.Outputs.Count == 2, Describe(half));

        if (half.Outputs.Count == 2)
        {
            Check("the halves are four pages each",
                PdfRenderer.PageCount(half.Outputs[0]) == 4
                && PdfRenderer.PageCount(half.Outputs[1]) == 4,
                "4 + 4");

            Check("two pieces stay beside the original",
                Path.GetDirectoryName(half.Outputs[0]) == Path.GetDirectoryName(long8),
                "no folder for a pair");
        }

        // Chunks, which is the answer to a long document.
        var chunked = await Run(
            Leaf(tools, "tool.pdf.split.1") with
            {
                Options = new Dictionary<string, string> { ["chunk"] = "3" }
            },
            new[] { new SourceFile(long8) }, FileCategory.Pdf);

        Check("every three pages", chunked.Outputs.Count == 3, Describe(chunked));

        if (chunked.Outputs.Count == 3)
        {
            Check("the last chunk takes what is left",
                PdfRenderer.PageCount(chunked.Outputs[0]) == 3
                && PdfRenderer.PageCount(chunked.Outputs[1]) == 3
                && PdfRenderer.PageCount(chunked.Outputs[2]) == 2,
                "3 + 3 + 2");
        }

        // One file per page on a long document: the pieces go into their own folder
        // rather than over the folder the PDF was sitting in.
        var each = await Run(Leaf(tools, "tool.pdf.split.1"),
            new[] { new SourceFile(long8) }, FileCategory.Pdf);

        Check("every page", each.Outputs.Count == 8, Describe(each));

        if (each.Outputs.Count == 8)
        {
            var home = Path.GetDirectoryName(each.Outputs[0]);

            Check("many pieces go into their own folder",
                home != Path.GetDirectoryName(long8)
                && each.Outputs.All(p => Path.GetDirectoryName(p) == home),
                Path.GetFileName(home) ?? "nowhere");

            Check("the pieces sort in reading order",
                each.Outputs.OrderBy(p => p, StringComparer.Ordinal).SequenceEqual(each.Outputs),
                "zero-padded page numbers");
        }

        // Typed cut points.
        var cut = await Run(
            Leaf(tools, "tool.pdf.split.custom") with
            {
                Options = new Dictionary<string, string> { [WheelItem.CustomOption] = "3,6" }
            },
            new[] { new SourceFile(long8) }, FileCategory.Pdf);

        Check("cutting before chosen pages", cut.Outputs.Count == 3, Describe(cut));

        if (cut.Outputs.Count == 3)
        {
            Check("the cuts land where asked",
                PdfRenderer.PageCount(cut.Outputs[0]) == 2
                && PdfRenderer.PageCount(cut.Outputs[1]) == 3
                && PdfRenderer.PageCount(cut.Outputs[2]) == 3,
                "1-2, 3-5, 6-8");
        }

        // A chunk bigger than the document would hand back the document.
        var pointless = await Run(Leaf(tools, "tool.pdf.split.10"),
            new[] { new SourceFile(long8) }, FileCategory.Pdf);

        Check("a split that would not split is refused",
            pointless.Outputs.Count == 0 && pointless.Failures.Count == 1,
            pointless.Failures.Count > 0 ? pointless.Failures[0] : "wrote a copy anyway");
    }

    /// <summary>
    /// Text extraction, against a PDF that genuinely carries text. Built here with real
    /// text operators rather than rendered glyphs, which is the difference between a PDF
    /// that can be read and a picture of one.
    /// </summary>
    private static async Task TestPdfText(string workspace)
    {
        var path = Path.Combine(workspace, "pdf-with-text.pdf");

        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);

        var first = builder.AddPage(595, 842);
        first.AddText("Satsuma converts files.", 14,
            new UglyToad.PdfPig.Core.PdfPoint(50, 780), font);
        first.AddText("It does so without uploading them.", 14,
            new UglyToad.PdfPig.Core.PdfPoint(50, 750), font);

        var second = builder.AddPage(595, 842);
        second.AddText("Second page text.", 14,
            new UglyToad.PdfPig.Core.PdfPoint(50, 780), font);

        await File.WriteAllBytesAsync(path, builder.Build());

        var formats = FormatCatalog.For(FileCategory.Pdf);
        var source = new[] { new SourceFile(path) };

        var text = await Run(formats.First(i => i.OutputExtension == ".txt"), source, FileCategory.Pdf);
        Check("PDF -> txt", text.Outputs.Count == 1, Describe(text));

        if (text.Outputs.Count == 1)
        {
            var content = await File.ReadAllTextAsync(text.Outputs[0]);

            Check("the words come through", content.Contains("Satsuma converts files"),
                "first line recovered");
            Check("later pages come through too", content.Contains("Second page text"),
                "both pages read");
        }

        var markdown = await Run(formats.First(i => i.OutputExtension == ".md"), source, FileCategory.Pdf);
        Check("PDF -> markdown", markdown.Outputs.Count == 1, Describe(markdown));

        if (markdown.Outputs.Count == 1)
        {
            var content = await File.ReadAllTextAsync(markdown.Outputs[0]);

            Check("markdown is given structure",
                content.Contains("# pdf-with-text") && content.Contains("## Page 2"),
                "a title and a heading per page");
        }
    }

    // ---- Explorer titles --------------------------------------------------

    /// <summary>
    /// Which tab an Explorer window title names. Driven directly, because the alternative
    /// is shuffling real Explorer windows around on someone's desktop. The titles here
    /// were taken from an actual trace, German ones included.
    /// </summary>
    private static void TestExplorerTitles()
    {
        Section("Explorer window titles");

        Check("single tab, English",
            ExplorerTitle.NamesFolder("Pictures - File Explorer", "Pictures"),
            "'Pictures - File Explorer' names Pictures");

        Check("single tab, German",
            ExplorerTitle.NamesFolder("20261028-164631 – Datei-Explorer", "20261028-164631"),
            "en dash and a localised suffix");

        Check("several tabs names only the front one",
            ExplorerTitle.NamesFolder(
                "Bildschirmfotos und 3 weitere Registerkarten – Explorer", "Bildschirmfotos"),
            "the active tab is first");

        Check("a background tab is not matched",
            !ExplorerTitle.NamesFolder(
                "Bildschirmfotos und 3 weitere Registerkarten – Explorer", "Downloads"),
            "Downloads is one of the other three");

        // The reason the match is not a bare StartsWith.
        Check("a shorter name does not claim a longer one",
            !ExplorerTitle.NamesFolder("Documents - File Explorer", "Docs"),
            "Docs must not match Documents");

        Check("an exact title matches",
            ExplorerTitle.NamesFolder("papers", "papers"),
            "no decoration at all");

        Check("nothing matches nothing",
            !ExplorerTitle.NamesFolder("", "Pictures") && !ExplorerTitle.NamesFolder("Pictures", ""),
            "empty on either side is no match");
    }

    // ---- Hit testing ------------------------------------------------------

    /// <summary>
    /// The behaviour behind the hover flicker. A real pointer cannot test this, because
    /// the person's own mouse moves during the run, so it is driven as pure geometry.
    /// </summary>
    private static void TestHitTesting()
    {
        Section("Wheel hit testing");

        const double inner = 62, outer = 160;
        const int count = 6;      // 60 degrees per pane
        const int none = WheelHitTest.None;

        // A point at a given angle (degrees clockwise from up) and radius.
        static (double X, double Y) At(double degrees, double radius)
        {
            var radians = (degrees - 90) * Math.PI / 180.0;
            return (radius * Math.Cos(radians), radius * Math.Sin(radians));
        }

        static int Probe(double degrees, double radius, int current)
        {
            var (x, y) = At(degrees, radius);
            return WheelHitTest.IndexAt(x, y, inner, outer, count, current);
        }

        Check("hub selects nothing", Probe(30, 20, none) == none, "inside the inner radius");
        Check("beyond the rim selects nothing", Probe(30, 400, none) == none, "past the outer radius");

        Check("pane 0 is the first 60 degrees", Probe(30, 110, none) == 0, "30 degrees picks pane 0");
        Check("pane 1 follows it", Probe(90, 110, none) == 1, "90 degrees picks pane 1");
        Check("pane 5 wraps round", Probe(330, 110, none) == 5, "330 degrees picks pane 5");

        // The flicker: a hand resting on a boundary, crossing it by a degree or two.
        var flips = 0;
        var held = Probe(58, 110, none);
        for (var i = 0; i < 60; i++)
        {
            var next = Probe(i % 2 == 0 ? 58 : 62, 110, held);
            if (next != held) flips++;
            held = next;
        }
        Check("tremor across a pane boundary does not strobe", flips == 0,
            $"{flips} changes over 60 crossings of the 60 degree line");

        // The same at the outer rim, which is the other hard line.
        flips = 0;
        held = Probe(30, 155, none);
        for (var i = 0; i < 60; i++)
        {
            var next = Probe(30, i % 2 == 0 ? 158 : 168, held);
            if (next != held) flips++;
            held = next;
        }
        Check("tremor across the outer rim does not strobe", flips == 0,
            $"{flips} changes over 60 crossings of the rim");

        // Hysteresis must not become stickiness: a deliberate move still lands.
        Check("a deliberate move to the next pane lands",
            Probe(95, 110, 0) == 1, "35 degrees past the boundary switches");
        Check("a deliberate move to the hub lands",
            Probe(30, 20, 0) == none, "well inside the hub releases");
        Check("leaving the wheel entirely releases",
            Probe(30, 400, 0) == none, "well past the rim releases");

        // Two panes meet at 180 degrees on a two-item wheel; the wrap must not confuse it.
        Check("wrap across the top is handled",
            WheelHitTest.DegreesOutside(359, 0, 60) < WheelHitTest.DegreesOutside(200, 0, 60),
            "359 degrees is nearer pane 0 than 200 degrees is");
    }

    // ---- Wheel ------------------------------------------------------------

    private static Task TestWheelBuilding(string workspace)
    {
        Section("Wheel building");

        var image = new[] { new SourceFile(Path.Combine(workspace, "x.png")) };
        var formats = Runner.BuildWheel(WheelKind.Formats, FileCategory.Image, image);

        Check("image formats offered", formats.Count >= 5, $"{formats.Count} slices");
        Check("source format hidden",
            formats.All(f => f.OutputExtension != ".png"),
            "no PNG slice for a PNG");

        var tools = Runner.BuildWheel(WheelKind.Tools, FileCategory.Image, image);
        Check("image tools offered", tools.Count >= 5, $"{tools.Count} slices");
        Check("presets kept as children",
            tools.Any(t => t.HasChildren),
            "Compress and Resize drill in");

        var actions = Runner.BuildWheel(WheelKind.Actions, FileCategory.Image, image);
        Check("actions always offered", actions.Count == 6, $"{actions.Count} slices");

        // A file Satsuma knows nothing about offers nothing, rather than everything.
        var unknown = Runner.BuildWheel(WheelKind.Formats, FileCategory.Unknown,
            new[] { new SourceFile(Path.Combine(workspace, "x.wat")) });
        Check("unknown files offer nothing", unknown.Count == 0, "no slices");

        return Task.CompletedTask;
    }

    // ---- Images -----------------------------------------------------------

    private static async Task TestImages(string workspace)
    {
        Section("Image formats");

        var source = Path.Combine(workspace, "sample.png");
        WriteTestPng(source, 640, 480);

        foreach (var extension in new[] { ".jpg", ".bmp", ".tiff", ".gif", ".webp", ".avif" })
        {
            if (!ImageCodec.CanWrite(extension))
            {
                Skip($"png -> {extension}", "no writer available");
                continue;
            }

            var item = FormatCatalog.For(FileCategory.Image)
                .First(i => i.OutputExtension == extension);

            await RunAndCheck($"png -> {extension}", item, source, FileCategory.Image);
        }
    }

    private static async Task TestImageTools(string workspace)
    {
        Section("Image tools");

        var source = Path.Combine(workspace, "tool-sample.png");
        WriteTestPng(source, 800, 600);

        var tools = ToolCatalog.For(FileCategory.Image);

        var resize = Leaf(tools, "tool.img.resize.50");
        var outputs = await RunAndCheck("resize to 50%", resize, source, FileCategory.Image);
        if (outputs.Count == 1)
        {
            var decoded = Sta(() => ImageCodec.TryDecode(outputs[0], out _));
            Check("resize really halved it",
                decoded is { PixelWidth: 400, PixelHeight: 300 },
                decoded is null ? "could not read result" : $"{decoded.PixelWidth}x{decoded.PixelHeight}");
        }

        var longEdge = Leaf(tools, "tool.img.resize.720");
        outputs = await RunAndCheck("resize long edge to 720", longEdge, source, FileCategory.Image);
        if (outputs.Count == 1)
        {
            var decoded = Sta(() => ImageCodec.TryDecode(outputs[0], out _));
            Check("long edge is 720",
                decoded is { PixelWidth: 720 },
                decoded is null ? "could not read result" : $"{decoded.PixelWidth}x{decoded.PixelHeight}");
        }

        var rotate = Leaf(tools, "tool.img.rotate.90");
        outputs = await RunAndCheck("rotate 90", rotate, source, FileCategory.Image);
        if (outputs.Count == 1)
        {
            var decoded = Sta(() => ImageCodec.TryDecode(outputs[0], out _));
            Check("rotate swapped the axes",
                decoded is { PixelWidth: 600, PixelHeight: 800 },
                decoded is null ? "could not read result" : $"{decoded.PixelWidth}x{decoded.PixelHeight}");
        }

        // Flipping, checked by where the marked corner ends up rather than by reasoning
        // about which axis a negative scale negates. "Horizontal" means mirrored
        // left-to-right, as it does everywhere else.
        var marked = Path.Combine(workspace, "flip-source.png");
        WriteCornerMarkedPng(marked, 200, 120);

        var flipH = await RunAndCheck("flip horizontally",
            Leaf(tools, "tool.img.flip.h"), marked, FileCategory.Image);

        if (flipH.Count == 1)
        {
            Check("horizontal flip moves the mark left to right",
                IsMarked(flipH[0], right: true, bottom: false),
                DescribeMark(flipH[0]));
        }

        var flipV = await RunAndCheck("flip vertically",
            Leaf(tools, "tool.img.flip.v"), marked, FileCategory.Image);

        if (flipV.Count == 1)
        {
            Check("vertical flip moves the mark top to bottom",
                IsMarked(flipV[0], right: false, bottom: true),
                DescribeMark(flipV[0]));
        }

        // A typed size, the whole point of the Custom slice.
        var customResize = Leaf(tools, "tool.img.resize.custom") with
        {
            Options = new Dictionary<string, string> { [WheelItem.CustomOption] = "640x360" }
        };

        var exact = await RunAndCheck("resize to typed dimensions", customResize,
            source, FileCategory.Image);

        if (exact.Count == 1)
        {
            var image = Sta(() => ImageCodec.TryDecode(exact[0], out _));
            Check("typed dimensions are honoured exactly",
                image is { PixelWidth: 640, PixelHeight: 360 },
                image is null ? "unreadable" : $"{image.PixelWidth}x{image.PixelHeight}");
        }

        await RunAndCheck("grayscale", Leaf(tools, "tool.img.grayscale"), source, FileCategory.Image);
        await RunAndCheck("strip metadata", Leaf(tools, "tool.img.strip"), source, FileCategory.Image);

        var jpegSource = Path.Combine(workspace, "compress-sample.jpg");
        Sta(() =>
        {
            var decodedPng = ImageCodec.TryDecode(source, out _)!;
            ImageCodec.SaveWithWic(decodedPng, jpegSource, 100);
            return true;
        });

        var compress = Leaf(tools, "tool.img.compress.50");
        outputs = await RunAndCheck("compress a jpg", compress, jpegSource, FileCategory.Image);
        if (outputs.Count == 1)
        {
            var before = new FileInfo(jpegSource).Length;
            var after = new FileInfo(outputs[0]).Length;
            Check("compressing shrank it", after < before, $"{Kb(before)} -> {Kb(after)}");
        }

        // Rotating should not also mangle the aspect ratio of a non-square image, and the
        // output format should follow the source rather than silently becoming something else.
        Check("tools keep the source format",
            outputs.Count == 1 && Path.GetExtension(outputs[0]) == ".jpg",
            "jpg in, jpg out");
    }

    // ---- Subtitles --------------------------------------------------------

    private static async Task TestSubtitles(string workspace)
    {
        Section("Subtitles");

        var source = Path.Combine(workspace, "sample.srt");
        await File.WriteAllTextAsync(source,
            "1\r\n00:00:01,000 --> 00:00:03,500\r\n<i>First line</i>\r\n\r\n" +
            "2\r\n00:00:04,000 --> 00:00:06,000\r\nSecond line\r\n");

        var formats = FormatCatalog.For(FileCategory.Subtitle);

        var text = formats.First(f => f.OutputExtension == ".txt");
        var outputs = await RunAndCheck("srt -> txt", text, source, FileCategory.Subtitle);

        if (outputs.Count == 1)
        {
            var content = await File.ReadAllTextAsync(outputs[0]);
            Check("timings stripped", !content.Contains("-->"), "no arrows left");
            Check("tags stripped", !content.Contains("<i>"), "no markup left");
            Check("dialogue kept",
                content.Contains("First line") && content.Contains("Second line"),
                "both lines survived");
        }

        if (FfmpegLocator.IsAvailable)
        {
            var vtt = formats.First(f => f.OutputExtension == ".vtt");
            await RunAndCheck("srt -> vtt", vtt, source, FileCategory.Subtitle);
        }
        else
        {
            Skip("srt -> vtt", "ffmpeg not installed");
        }
    }

    // ---- Archives ---------------------------------------------------------

    private static async Task TestArchives(string workspace)
    {
        Section("Archives");

        var folder = Path.Combine(workspace, "to-archive");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        await File.WriteAllTextAsync(Path.Combine(folder, "one.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(folder, "nested", "two.txt"), "second");

        var zipItem = FormatCatalog.For(FileCategory.Folder).First(f => f.OutputExtension == ".zip");
        var zipped = await Run(zipItem, new[] { new SourceFile(folder) }, FileCategory.Folder);

        Check("folder -> zip", zipped.Outputs.Count == 1 && File.Exists(zipped.Outputs[0]),
            Describe(zipped));

        if (zipped.Outputs.Count == 1)
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(zipped.Outputs[0]);
            Check("zip holds both files", archive.Entries.Count == 2, $"{archive.Entries.Count} entries");
            Check("zip keeps the folder name",
                archive.Entries.All(entry => entry.FullName.StartsWith("to-archive")),
                "entries are prefixed, so it will not explode on extract");

            var extract = new WheelItem("fmt.extract", "Extract", WheelKind.Formats);
            var extracted = await Run(extract,
                new[] { new SourceFile(zipped.Outputs[0]) }, FileCategory.Archive);

            Check("zip -> extract",
                extracted.Outputs.Count == 1 && Directory.Exists(extracted.Outputs[0]),
                Describe(extracted));

            if (extracted.Outputs.Count == 1)
            {
                var files = Directory.GetFiles(extracted.Outputs[0], "*", SearchOption.AllDirectories);
                Check("both files came back out", files.Length == 2, $"{files.Length} files");
            }
        }

        var tarItem = FormatCatalog.For(FileCategory.Folder).First(f => f.OutputExtension == ".tar");
        var tarred = await Run(tarItem, new[] { new SourceFile(folder) }, FileCategory.Folder);
        Check("folder -> tar", tarred.Outputs.Count == 1 && File.Exists(tarred.Outputs[0]),
            Describe(tarred));

        var single = Path.Combine(workspace, "single.txt");
        await File.WriteAllTextAsync(single, new string('x', 5000));

        var gzItem = FormatCatalog.For(FileCategory.Archive).First(f => f.OutputExtension == ".gz");
        var gzipped = await Run(gzItem, new[] { new SourceFile(single) }, FileCategory.Archive);
        Check("file -> gz", gzipped.Outputs.Count == 1 && File.Exists(gzipped.Outputs[0]),
            Describe(gzipped));

        // A gzip member is one file, so a folder must not be offered the slice at all.
        var folderRequest = new JobRequest(gzItem, new[] { new SourceFile(folder) }, FileCategory.Folder);
        Check("gz refused for a folder", !Runner.CanHandle(folderRequest), "slice hidden");
    }

    // ---- Audio ------------------------------------------------------------

    private static async Task TestAudio(string workspace)
    {
        Section("Audio formats");

        var source = Path.Combine(workspace, "tone.wav");
        if (!await GenerateAsync(new[]
            {
                "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
                "-c:a", "pcm_s16le", source
            }))
        {
            Skip("audio", "could not generate a test tone");
            return;
        }

        foreach (var extension in new[] { ".mp3", ".m4a", ".flac", ".ogg", ".opus", ".aiff", ".wma" })
        {
            var item = FormatCatalog.For(FileCategory.Audio)
                .First(i => i.OutputExtension == extension);

            await RunAndCheck($"wav -> {extension}", item, source, FileCategory.Audio);
        }
    }

    private static async Task TestAudioTools(string workspace)
    {
        Section("Audio tools");

        var source = Path.Combine(workspace, "tools-tone.mp3");
        if (!await GenerateAsync(new[]
            {
                "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
                // Deliberately high bitrate, so "compress to 64k" has something to remove.
                "-c:a", "libmp3lame", "-b:a", "320k", source
            }))
        {
            Skip("audio tools", "could not generate a test tone");
            return;
        }

        var tools = ToolCatalog.For(FileCategory.Audio);

        await RunAndCheck("normalize", Leaf(tools, "tool.aud.normalize"), source, FileCategory.Audio);
        await RunAndCheck("to mono", Leaf(tools, "tool.aud.mono"), source, FileCategory.Audio);
        await RunAndCheck("strip tags", Leaf(tools, "tool.aud.strip"), source, FileCategory.Audio);

        var compress = Leaf(tools, "tool.aud.compress.64");
        var outputs = await RunAndCheck("compress to 64k", compress, source, FileCategory.Audio);
        if (outputs.Count == 1)
        {
            var before = new FileInfo(source).Length;
            var after = new FileInfo(outputs[0]).Length;
            Check("compressing shrank it", after < before, $"{Kb(before)} -> {Kb(after)}");
        }

        // Join folds the whole selection, so it needs at least two files.
        var second = Path.Combine(workspace, "tools-tone-2.mp3");
        await GenerateAsync(new[]
        {
            "-f", "lavfi", "-i", "sine=frequency=660:duration=2",
            "-c:a", "libmp3lame", second
        });

        var join = Leaf(tools, "tool.aud.join");
        var joined = await Run(join,
            new[] { new SourceFile(source), new SourceFile(second) }, FileCategory.Audio);

        Check("join two tracks", joined.Outputs.Count == 1 && File.Exists(joined.Outputs[0]),
            Describe(joined));

        if (joined.Outputs.Count == 1)
        {
            var duration = await Ffmpeg.ProbeDurationAsync(joined.Outputs[0]);
            Check("joined length adds up",
                duration is not null && duration.Value.TotalSeconds > 5,
                duration is null ? "unknown" : $"{duration.Value.TotalSeconds:0.0}s of an expected ~6s");
        }

        var singleRequest = new JobRequest(join, new[] { new SourceFile(source) }, FileCategory.Audio);
        Check("join hidden for one file", !Runner.CanHandle(singleRequest), "slice hidden");
    }

    // ---- Video ------------------------------------------------------------

    private static async Task TestVideo(string workspace)
    {
        Section("Video formats");

        var source = Path.Combine(workspace, "clip.mp4");
        if (!await GenerateAsync(new[]
            {
                "-f", "lavfi", "-i", "testsrc=size=320x240:rate=15:duration=2",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source
            }))
        {
            Skip("video", "could not generate a test clip");
            return;
        }

        foreach (var extension in new[] { ".mov", ".mkv", ".webm", ".avi", ".wmv", ".gif", ".mp3" })
        {
            var item = FormatCatalog.For(FileCategory.Video)
                .First(i => i.OutputExtension == extension);

            await RunAndCheck($"mp4 -> {extension}", item, source, FileCategory.Video);
        }
    }

    private static async Task TestVideoTools(string workspace)
    {
        Section("Video tools");

        var source = Path.Combine(workspace, "tools-clip.mp4");
        if (!await GenerateAsync(new[]
            {
                "-f", "lavfi", "-i", "testsrc=size=640x480:rate=15:duration=4",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source
            }))
        {
            Skip("video tools", "could not generate a test clip");
            return;
        }

        var tools = ToolCatalog.For(FileCategory.Video);

        var compress = Leaf(tools, "tool.vid.compress.34");
        var outputs = await RunAndCheck("compress", compress, source, FileCategory.Video);
        if (outputs.Count == 1)
        {
            var before = new FileInfo(source).Length;
            var after = new FileInfo(outputs[0]).Length;
            Check("compressing shrank it", after < before, $"{Kb(before)} -> {Kb(after)}");
        }

        var resize = Leaf(tools, "tool.vid.resize.50");
        outputs = await RunAndCheck("resize to 50%", resize, source, FileCategory.Video);
        if (outputs.Count == 1)
        {
            var size = await Ffmpeg.ProbeSizeAsync(outputs[0]);
            Check("resize really halved it", size is { Width: 320, Height: 240 },
                size is null ? "unknown" : $"{size.Value.Width}x{size.Value.Height}");
        }

        var longEdge = Leaf(tools, "tool.vid.resize.720");
        outputs = await RunAndCheck("resize long edge to 720", longEdge, source, FileCategory.Video);
        if (outputs.Count == 1)
        {
            var size = await Ffmpeg.ProbeSizeAsync(outputs[0]);
            Check("long edge is 640 and untouched, since it was already under 720",
                size is { Width: 640 },
                size is null ? "unknown" : $"{size.Value.Width}x{size.Value.Height}");
        }

        var speed = Leaf(tools, "tool.vid.speed.2");
        outputs = await RunAndCheck("double speed", speed, source, FileCategory.Video);
        if (outputs.Count == 1)
        {
            var duration = await Ffmpeg.ProbeDurationAsync(outputs[0]);
            Check("duration halved",
                duration is not null && duration.Value.TotalSeconds is > 1.5 and < 2.6,
                duration is null ? "unknown" : $"{duration.Value.TotalSeconds:0.0}s of an expected ~2s");
        }

        var rotate = Leaf(tools, "tool.vid.rotate.90");
        outputs = await RunAndCheck("rotate 90", rotate, source, FileCategory.Video);
        if (outputs.Count == 1)
        {
            var size = await Ffmpeg.ProbeSizeAsync(outputs[0]);
            Check("rotate swapped the axes", size is { Width: 480, Height: 640 },
                size is null ? "unknown" : $"{size.Value.Width}x{size.Value.Height}");
        }

        var mute = Leaf(tools, "tool.vid.mute");
        outputs = await RunAndCheck("remove audio", mute, source, FileCategory.Video);
        if (outputs.Count == 1)
        {
            Check("audio really gone", !await Ffmpeg.HasAudioAsync(outputs[0]), "no audio stream");
        }

        await RunAndCheck("snapshot", Leaf(tools, "tool.vid.snapshot"), source, FileCategory.Video);
        await RunAndCheck("extract audio", Leaf(tools, "tool.vid.audio"), source, FileCategory.Video);

        var second = Path.Combine(workspace, "tools-clip-2.mp4");
        await GenerateAsync(new[]
        {
            "-f", "lavfi", "-i", "testsrc=size=640x480:rate=15:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=660:duration=2",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", second
        });

        var join = Leaf(tools, "tool.vid.join");
        var joined = await Run(join,
            new[] { new SourceFile(source), new SourceFile(second) }, FileCategory.Video);

        Check("join two clips", joined.Outputs.Count == 1 && File.Exists(joined.Outputs[0]),
            Describe(joined));

        if (joined.Outputs.Count == 1)
        {
            var duration = await Ffmpeg.ProbeDurationAsync(joined.Outputs[0]);
            Check("joined length adds up",
                duration is not null && duration.Value.TotalSeconds > 5,
                duration is null ? "unknown" : $"{duration.Value.TotalSeconds:0.0}s of an expected ~6s");
        }
    }

    // ---- Harness ----------------------------------------------------------

    private static WheelItem Leaf(IReadOnlyList<WheelItem> items, string id)
    {
        foreach (var item in items)
        {
            if (item.Id == id) return item;

            foreach (var child in item.Children)
            {
                if (child.Id == id) return child;
            }
        }

        throw new InvalidOperationException($"No wheel item with id {id}.");
    }

    private static Task<BatchResult> Run(
        WheelItem item, IReadOnlyList<SourceFile> files, FileCategory category) =>
        Runner.RunAsync(new JobRequest(item, files, category));

    private static async Task<IReadOnlyList<string>> RunAndCheck(
        string label, WheelItem item, string sourcePath, FileCategory category)
    {
        var files = new[] { new SourceFile(sourcePath) };

        if (!Runner.CanHandle(new JobRequest(item, files, category)))
        {
            Skip(label, "no handler claims it");
            return Array.Empty<string>();
        }

        var result = await Run(item, files, category);

        var ok = result.AllSucceeded
                 && result.Outputs.Count > 0
                 && result.Outputs.All(p => File.Exists(p) || Directory.Exists(p))
                 && result.Outputs.All(p => Directory.Exists(p) || new FileInfo(p).Length > 0);

        Check(label, ok, Describe(result));
        return result.Outputs;
    }

    private static string Describe(BatchResult result)
    {
        if (result.Failures.Count > 0) return result.Failures[0];
        if (result.Outputs.Count == 0) return "no output";

        var first = result.Outputs[0];
        if (Directory.Exists(first)) return Path.GetFileName(first) + "/";

        return $"{Path.GetFileName(first)}, {Kb(new FileInfo(first).Length)}";
    }

    /// <summary>Makes a sample file with ffmpeg. Returns false when it could not.</summary>
    private static async Task<bool> GenerateAsync(IEnumerable<string> arguments)
    {
        try
        {
            var outcome = await Ffmpeg.RunAsync(arguments);
            return outcome.Succeeded;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes a PNG with some structure in it, so a resize or a rotate has something to
    /// actually change and a compression has something to lose.
    /// </summary>
    private static void WriteTestPng(string path, int width, int height, int dpi = 96)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                var checker = ((x / 32) + (y / 32)) % 2 == 0;

                pixels[offset + 0] = (byte)(checker ? x * 255 / width : 20);          // blue
                pixels[offset + 1] = (byte)(checker ? y * 255 / height : 40);         // green
                pixels[offset + 2] = (byte)(checker ? 255 - x * 255 / width : 180);   // red
                pixels[offset + 3] = 255;                                             // alpha
            }
        }

        Sta(() =>
        {
            var bitmap = BitmapSource.Create(width, height, dpi, dpi,
                PixelFormats.Bgra32, null, pixels, stride);

            ImageCodec.SaveWithWic(bitmap, path, 100);
            return true;
        });
    }

    /// <summary>WIC is apartment-threaded, so even the harness has to ask politely.</summary>
    private static T Sta<T>(Func<T> work) =>
        StaExecutor.RunAsync(work).GetAwaiter().GetResult();

    /// <summary>
    /// A dark image with one bright corner, so a flip can be checked by where the bright
    /// corner ends up. The mark starts in the top left.
    /// </summary>
    private static void WriteCornerMarkedPng(string path, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];

        var markWidth = width / 4;
        var markHeight = height / 4;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                var marked = x < markWidth && y < markHeight;

                pixels[offset + 0] = (byte)(marked ? 255 : 10);
                pixels[offset + 1] = (byte)(marked ? 255 : 10);
                pixels[offset + 2] = (byte)(marked ? 255 : 10);
                pixels[offset + 3] = 255;
            }
        }

        Sta(() =>
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96,
                PixelFormats.Bgra32, null, pixels, stride);

            ImageCodec.SaveWithWic(bitmap, path, 100);
            return true;
        });
    }

    /// <summary>Which corner is the bright one.</summary>
    private static (bool Right, bool Bottom)? FindMark(string path)
    {
        // Decoded and read on the same STA thread. A decoded bitmap stays bound to the
        // thread that read it, so it cannot be decoded here and inspected elsewhere.
        return Sta<(bool, bool)?>(() =>
        {
            var image = ImageCodec.TryDecode(path, out _);
            if (image is null) return null;

            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            converted.Freeze();

            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            var stride = width * 4;

            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            // Sample well inside each corner, away from any resampling at the edges.
            bool Bright(int x, int y) => pixels[y * stride + x * 4] > 128;

            var inset = Math.Min(width, height) / 10;

            if (Bright(inset, inset)) return (false, false);
            if (Bright(width - 1 - inset, inset)) return (true, false);
            if (Bright(inset, height - 1 - inset)) return (false, true);
            if (Bright(width - 1 - inset, height - 1 - inset)) return (true, true);

            return null;
        });
    }

    private static bool IsMarked(string path, bool right, bool bottom) =>
        FindMark(path) == (right, bottom);

    private static string DescribeMark(string path) => FindMark(path) switch
    {
        null => "no mark found",
        (false, false) => "mark is top left",
        (true, false) => "mark is top right",
        (false, true) => "mark is bottom left",
        (true, true) => "mark is bottom right"
    };

    private static string Kb(long bytes) => $"{bytes / 1024.0:0.#} KB";

    private static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine(name);
        Console.WriteLine(new string('-', name.Length));
    }

    private static void Check(string label, bool condition, string detail)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  pass  {label,-44}  {detail}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  FAIL  {label,-44}  {detail}");
        }
    }

    private static void Skip(string label, string reason)
    {
        _skipped++;
        Console.WriteLine($"  skip  {label,-44}  {reason}");
    }
}
