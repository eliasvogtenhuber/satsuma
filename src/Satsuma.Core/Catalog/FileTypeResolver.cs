using Satsuma.Core.Model;

namespace Satsuma.Core.Catalog;

/// <summary>Maps file extensions onto <see cref="FileCategory"/>.</summary>
public static class FileTypeResolver
{
    private static readonly Dictionary<string, FileCategory> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images
        [".jpg"] = FileCategory.Image, [".jpeg"] = FileCategory.Image, [".jpe"] = FileCategory.Image,
        [".png"] = FileCategory.Image, [".webp"] = FileCategory.Image, [".heic"] = FileCategory.Image,
        [".heif"] = FileCategory.Image, [".tif"] = FileCategory.Image, [".tiff"] = FileCategory.Image,
        [".avif"] = FileCategory.Image, [".bmp"] = FileCategory.Image, [".gif"] = FileCategory.Image,
        [".svg"] = FileCategory.Image, [".ico"] = FileCategory.Image, [".jxl"] = FileCategory.Image,
        [".dng"] = FileCategory.Image, [".cr2"] = FileCategory.Image, [".nef"] = FileCategory.Image,
        [".arw"] = FileCategory.Image,

        // Video
        [".mp4"] = FileCategory.Video, [".m4v"] = FileCategory.Video, [".mov"] = FileCategory.Video,
        [".mkv"] = FileCategory.Video, [".webm"] = FileCategory.Video, [".avi"] = FileCategory.Video,
        [".wmv"] = FileCategory.Video, [".flv"] = FileCategory.Video, [".mpg"] = FileCategory.Video,
        [".mpeg"] = FileCategory.Video, [".ts"] = FileCategory.Video, [".3gp"] = FileCategory.Video,

        // Audio
        [".mp3"] = FileCategory.Audio, [".m4a"] = FileCategory.Audio, [".aac"] = FileCategory.Audio,
        [".wav"] = FileCategory.Audio, [".flac"] = FileCategory.Audio, [".ogg"] = FileCategory.Audio,
        [".opus"] = FileCategory.Audio, [".aiff"] = FileCategory.Audio, [".aif"] = FileCategory.Audio,
        [".wma"] = FileCategory.Audio, [".alac"] = FileCategory.Audio,

        // Documents and friends
        [".pdf"] = FileCategory.Pdf,
        [".docx"] = FileCategory.Document, [".doc"] = FileCategory.Document,
        [".odt"] = FileCategory.Document, [".rtf"] = FileCategory.Document,
        [".txt"] = FileCategory.Document, [".md"] = FileCategory.Document,
        [".markdown"] = FileCategory.Document, [".epub"] = FileCategory.Document,
        [".html"] = FileCategory.Document, [".htm"] = FileCategory.Document,

        [".pptx"] = FileCategory.Slides, [".ppt"] = FileCategory.Slides, [".odp"] = FileCategory.Slides,

        [".xlsx"] = FileCategory.Spreadsheet, [".xls"] = FileCategory.Spreadsheet,
        [".ods"] = FileCategory.Spreadsheet, [".csv"] = FileCategory.Spreadsheet,
        [".tsv"] = FileCategory.Spreadsheet,

        [".srt"] = FileCategory.Subtitle, [".vtt"] = FileCategory.Subtitle,
        [".ass"] = FileCategory.Subtitle, [".ssa"] = FileCategory.Subtitle,

        [".zip"] = FileCategory.Archive, [".tar"] = FileCategory.Archive, [".gz"] = FileCategory.Archive,
        [".tgz"] = FileCategory.Archive, [".rar"] = FileCategory.Archive, [".7z"] = FileCategory.Archive,
    };

    public static FileCategory Resolve(SourceFile file)
        => file.IsDirectory ? FileCategory.Folder : Resolve(file.Extension);

    public static FileCategory Resolve(string extension)
        => Map.TryGetValue(extension, out var category) ? category : FileCategory.Unknown;

    /// <summary>
    /// The category for a whole selection. A mixed selection resolves to the category
    /// they all share, or <see cref="FileCategory.Unknown"/> when they share none.
    /// </summary>
    public static FileCategory ResolveSelection(IReadOnlyList<SourceFile> files)
    {
        if (files.Count == 0) return FileCategory.Unknown;

        var first = Resolve(files[0]);
        foreach (var file in files)
        {
            if (Resolve(file) != first) return FileCategory.Unknown;
        }
        return first;
    }

    /// <summary>True when the extension can be read but never written by Satsuma.</summary>
    public static bool IsInputOnly(string extension) => extension.ToLowerInvariant() switch
    {
        ".svg" or ".rar" or ".7z" or ".doc" or ".xls" or ".ppt" or ".rtf"
            or ".dng" or ".cr2" or ".nef" or ".arw" => true,
        _ => false
    };
}
