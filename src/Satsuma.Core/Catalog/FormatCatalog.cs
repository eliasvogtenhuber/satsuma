using Satsuma.Core.Model;

namespace Satsuma.Core.Catalog;

/// <summary>
/// Every output format Satsuma knows about, per source category.
///
/// This catalog is deliberately broader than what the engine can currently execute.
/// The job runner filters it down before the wheel is drawn, so the wheel only ever shows
/// conversions that really work, and new handlers light up their formats automatically.
/// </summary>
public static class FormatCatalog
{
    private static WheelItem Fmt(string label, string ext, string? hint = null) =>
        new("fmt" + ext, label, WheelKind.Formats, Hint: hint) { OutputExtension = ext };

    public static IReadOnlyList<WheelItem> For(FileCategory category) => category switch
    {
        FileCategory.Image => new[]
        {
            Fmt("JPG", ".jpg"),
            Fmt("PNG", ".png"),
            Fmt("WebP", ".webp"),
            Fmt("HEIC", ".heic"),
            Fmt("TIFF", ".tiff"),
            Fmt("AVIF", ".avif"),
            Fmt("BMP", ".bmp"),
            Fmt("GIF", ".gif"),
            Fmt("PDF", ".pdf", "One page per image"),
        },

        FileCategory.Video => new[]
        {
            Fmt("MP4", ".mp4"),
            Fmt("MOV", ".mov"),
            Fmt("MKV", ".mkv"),
            Fmt("WebM", ".webm"),
            Fmt("AVI", ".avi"),
            Fmt("WMV", ".wmv"),
            Fmt("GIF", ".gif", "Animated, 12 fps"),
            Fmt("MP3", ".mp3", "Extract audio"),
        },

        FileCategory.Audio => new[]
        {
            Fmt("MP3", ".mp3"),
            Fmt("M4A", ".m4a"),
            Fmt("WAV", ".wav"),
            Fmt("FLAC", ".flac"),
            Fmt("OGG", ".ogg"),
            Fmt("Opus", ".opus"),
            Fmt("AIFF", ".aiff"),
            Fmt("WMA", ".wma"),
        },

        FileCategory.Pdf => new[]
        {
            Fmt("DOCX", ".docx"),
            Fmt("PPTX", ".pptx"),
            Fmt("XLSX", ".xlsx"),
            Fmt("JPG", ".jpg", "All pages, 300 DPI"),
            Fmt("PNG", ".png", "All pages, 300 DPI"),
            Fmt("EPUB", ".epub"),
            Fmt("TXT", ".txt"),
            Fmt("Markdown", ".md"),
        },

        FileCategory.Document => new[]
        {
            Fmt("DOCX", ".docx"),
            Fmt("PDF", ".pdf"),
            Fmt("Markdown", ".md"),
            Fmt("TXT", ".txt"),
            Fmt("EPUB", ".epub"),
            Fmt("HTML", ".html"),
        },

        FileCategory.Slides => new[]
        {
            Fmt("PDF", ".pdf"),
            Fmt("JPG", ".jpg", "One per slide"),
            Fmt("PNG", ".png", "One per slide"),
        },

        FileCategory.Spreadsheet => new[]
        {
            Fmt("XLSX", ".xlsx"),
            Fmt("CSV", ".csv"),
            Fmt("TSV", ".tsv"),
            Fmt("ODS", ".ods"),
            Fmt("PDF", ".pdf"),
        },

        FileCategory.Subtitle => new[]
        {
            Fmt("SRT", ".srt"),
            Fmt("VTT", ".vtt"),
            Fmt("TXT", ".txt", "Strip timings"),
        },

        FileCategory.Archive => new[]
        {
            Fmt("ZIP", ".zip"),
            Fmt("TAR", ".tar"),
            Fmt("GZIP", ".gz"),
            new WheelItem("fmt.extract", "Extract", WheelKind.Formats, Hint: "Into a new folder"),
        },

        FileCategory.Folder => new[]
        {
            Fmt("ZIP", ".zip"),
            Fmt("TAR", ".tar"),
        },

        _ => Array.Empty<WheelItem>()
    };

    /// <summary>
    /// Drops the format the files are already in, since offering PNG for a PNG is noise.
    /// Kept when the selection is mixed, because then it is a real conversion for some files.
    /// </summary>
    public static IReadOnlyList<WheelItem> For(FileCategory category, IReadOnlyList<SourceFile> files)
    {
        var all = For(category);
        if (files.Count == 0) return all;

        var extensions = files.Select(f => NormalizeExtension(f.Extension)).Distinct().ToList();
        if (extensions.Count != 1) return all;

        var sourceExt = extensions[0];
        return all.Where(i => i.OutputExtension is null
                              || NormalizeExtension(i.OutputExtension) != sourceExt)
                  .ToList();
    }

    /// <summary>Folds alias extensions onto the one Satsuma writes.</summary>
    public static string NormalizeExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpeg" or ".jpe" => ".jpg",
        ".tif" => ".tiff",
        ".heif" => ".heic",
        ".aif" => ".aiff",
        ".m4v" => ".mp4",
        ".markdown" => ".md",
        ".htm" => ".html",
        _ => ext.ToLowerInvariant()
    };
}
