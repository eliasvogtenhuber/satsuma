using Satsuma.Core.Model;

namespace Satsuma.Core.Catalog;

/// <summary>
/// The tools wheel: edits and compressions that keep the file's kind the same.
///
/// Tools lean on presets rather than dialogs. The whole point of Satsuma is not opening
/// another window, so anything that would need a form either gets preset slices
/// (Compress, Resize) or waits for a proper editor surface (Crop, Annotate, Redact).
/// </summary>
public static class ToolCatalog
{
    private static WheelItem Tool(
        string id,
        string label,
        string? hint = null,
        Dictionary<string, string>? options = null,
        WheelItem[]? children = null) =>
        new(id, label, WheelKind.Tools, Hint: hint)
        {
            Options = options ?? new Dictionary<string, string>(),
            Children = children ?? Array.Empty<WheelItem>()
        };

    private static WheelItem[] QualityPresets(string prefix) => new[]
    {
        Tool(prefix + ".90", "Light", "Barely visible", new() { ["quality"] = "90" }),
        Tool(prefix + ".75", "Balanced", "Good default", new() { ["quality"] = "75" }),
        Tool(prefix + ".50", "Strong", "Visibly softer", new() { ["quality"] = "50" }),
        Tool(prefix + ".35", "Maximum", "Smallest file", new() { ["quality"] = "35" }),
    };

    private static WheelItem[] ScalePresets(string prefix) => new[]
    {
        Tool(prefix + ".75", "75%", options: new() { ["scale"] = "0.75" }),
        Tool(prefix + ".50", "50%", options: new() { ["scale"] = "0.5" }),
        Tool(prefix + ".25", "25%", options: new() { ["scale"] = "0.25" }),
        Tool(prefix + ".1080", "1080p", "Long edge", new() { ["longEdge"] = "1080" }),
        Tool(prefix + ".720", "720p", "Long edge", new() { ["longEdge"] = "720" }),
    };

    private static WheelItem[] RotatePresets(string prefix) => new[]
    {
        Tool(prefix + ".90", "90 right", options: new() { ["degrees"] = "90" }),
        Tool(prefix + ".270", "90 left", options: new() { ["degrees"] = "270" }),
        Tool(prefix + ".180", "180", options: new() { ["degrees"] = "180" }),
    };

    public static IReadOnlyList<WheelItem> For(FileCategory category) => category switch
    {
        FileCategory.Image => new[]
        {
            Tool("tool.img.compress", "Compress", "Pick a strength",
                children: QualityPresets("tool.img.compress")),
            Tool("tool.img.resize", "Resize", "Pick a size",
                children: ScalePresets("tool.img.resize")),
            Tool("tool.img.rotate", "Rotate", children: RotatePresets("tool.img.rotate")),
            Tool("tool.img.flip", "Flip", children: new[]
            {
                Tool("tool.img.flip.h", "Horizontal", options: new() { ["axis"] = "h" }),
                Tool("tool.img.flip.v", "Vertical", options: new() { ["axis"] = "v" }),
            }),
            Tool("tool.img.grayscale", "Grayscale"),
            Tool("tool.img.strip", "Strip metadata", "Drop EXIF, GPS, author"),
            Tool("tool.img.pdf", "Make PDF", "All images in one PDF"),
        },

        FileCategory.Video => new[]
        {
            Tool("tool.vid.compress", "Compress", "Pick a strength", children: new[]
            {
                Tool("tool.vid.compress.26", "Light", "Roughly half the size", new() { ["crf"] = "26" }),
                Tool("tool.vid.compress.30", "Balanced", "Good default", new() { ["crf"] = "30" }),
                Tool("tool.vid.compress.34", "Strong", "Visibly softer", new() { ["crf"] = "34" }),
            }),
            Tool("tool.vid.resize", "Resize", "Pick a size",
                children: ScalePresets("tool.vid.resize")),
            Tool("tool.vid.speed", "Speed", children: new[]
            {
                Tool("tool.vid.speed.2", "2x faster", options: new() { ["factor"] = "2" }),
                Tool("tool.vid.speed.15", "1.5x faster", options: new() { ["factor"] = "1.5" }),
                Tool("tool.vid.speed.05", "Half speed", options: new() { ["factor"] = "0.5" }),
            }),
            Tool("tool.vid.rotate", "Rotate", children: RotatePresets("tool.vid.rotate")),
            Tool("tool.vid.mute", "Remove audio"),
            Tool("tool.vid.snapshot", "Snapshot", "Frame at the midpoint"),
            Tool("tool.vid.audio", "Extract audio", "As MP3"),
            Tool("tool.vid.join", "Join", "All clips end to end"),
        },

        FileCategory.Audio => new[]
        {
            Tool("tool.aud.compress", "Compress", "Pick a bitrate", children: new[]
            {
                Tool("tool.aud.compress.192", "192 kbps", options: new() { ["bitrate"] = "192k" }),
                Tool("tool.aud.compress.128", "128 kbps", options: new() { ["bitrate"] = "128k" }),
                Tool("tool.aud.compress.96", "96 kbps", options: new() { ["bitrate"] = "96k" }),
                Tool("tool.aud.compress.64", "64 kbps", "Speech", new() { ["bitrate"] = "64k" }),
            }),
            Tool("tool.aud.normalize", "Normalize", "Even out loudness"),
            Tool("tool.aud.mono", "To mono"),
            Tool("tool.aud.strip", "Strip metadata", "Drop tags and cover art"),
            Tool("tool.aud.join", "Join", "All tracks end to end"),
        },

        FileCategory.Pdf => new[]
        {
            Tool("tool.pdf.compress", "Compress", children: QualityPresets("tool.pdf.compress")),
            Tool("tool.pdf.merge", "Merge", "All PDFs into one"),
            Tool("tool.pdf.split", "Split", "One file per page"),
        },

        FileCategory.Folder => new[]
        {
            Tool("tool.folder.zip", "Zip"),
        },

        _ => Array.Empty<WheelItem>()
    };
}
