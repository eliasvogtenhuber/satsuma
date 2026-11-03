namespace Satsuma.Core.Model;

/// <summary>Which wheel the user asked for, chosen by the modifier they held.</summary>
public enum WheelKind
{
    /// <summary>Output formats for the dragged files (Shift).</summary>
    Formats,

    /// <summary>Editing and compression tools (Alt).</summary>
    Tools,

    /// <summary>File actions that don't transform anything (Ctrl).</summary>
    Actions
}

/// <summary>Broad class of a file, used to decide what the wheel offers.</summary>
public enum FileCategory
{
    Unknown,
    Image,
    Video,
    Audio,
    Pdf,
    Document,
    Slides,
    Spreadsheet,
    Subtitle,
    Archive,
    Folder
}

/// <summary>
/// One slice of the wheel. <paramref name="Id"/> is the stable key a handler matches on;
/// everything else is presentation.
/// </summary>
public sealed record WheelItem(
    string Id,
    string Label,
    WheelKind Kind,
    string? Glyph = null,
    string? Hint = null)
{
    /// <summary>Extension (with dot) this item produces, when it produces a single file.</summary>
    public string? OutputExtension { get; init; }

    /// <summary>Preset arguments the handler interprets, e.g. a resize percentage.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Items that replace this one when it is opened, for presets like Compress.</summary>
    public IReadOnlyList<WheelItem> Children { get; init; } = Array.Empty<WheelItem>();

    /// <summary>
    /// A value this item needs before it can run. Presets cover the common answers; this
    /// is for the ones only the user knows.
    /// </summary>
    public InputRequest? Input { get; init; }

    /// <summary>
    /// True for a slice that needs more files than it was given, and asks for them by
    /// letting the user click them where they are.
    ///
    /// Merging one PDF is not a thing you can do, but it is exactly where someone starts:
    /// they are looking at the first file, not at a selection. So the slice is offered
    /// for a single file and collects the rest afterwards, with the one it started from
    /// at the front.
    /// </summary>
    public bool Gathers { get; init; }

    /// <summary>The option a typed answer fills in.</summary>
    public const string CustomOption = "custom";

    public bool HasChildren => Children.Count > 0;
}

/// <summary>
/// Where a job's results should end up.
///
/// Satsuma's default has always been "beside the original", which is the answer that
/// needs no thought and can't lose anything. The others are asked for by holding a key
/// through to the end of the gesture, so they cost a deliberate act: Space for somewhere
/// else, Alt for over the top.
/// </summary>
public enum Destination
{
    /// <summary>Beside the original, where every result has gone until now.</summary>
    Beside,

    /// <summary>In a new folder made for them.</summary>
    NewFolder,

    /// <summary>In a folder the user picked by clicking it.</summary>
    ChosenFolder,

    /// <summary>Over the original, which goes to the Recycle Bin first.</summary>
    InPlace
}

/// <summary>A single file the user dragged.</summary>
public sealed record SourceFile(string Path)
{
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
    public string Name => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
    public bool IsDirectory => System.IO.Directory.Exists(Path);
}

/// <summary>Everything a handler needs to do one unit of work.</summary>
public sealed record JobRequest(
    WheelItem Item,
    IReadOnlyList<SourceFile> Files,
    FileCategory Category);

public enum JobStatus { Succeeded, Failed, Skipped }

public sealed record JobResult(
    JobStatus Status,
    IReadOnlyList<string> OutputPaths,
    string? Message = null)
{
    public static JobResult Ok(params string[] outputs) => new(JobStatus.Succeeded, outputs);
    public static JobResult Ok(IReadOnlyList<string> outputs) => new(JobStatus.Succeeded, outputs);
    public static JobResult Fail(string message) => new(JobStatus.Failed, Array.Empty<string>(), message);
    public static JobResult Skip(string message) => new(JobStatus.Skipped, Array.Empty<string>(), message);
}

/// <summary>
/// One file a job wrote, and the file it was made from.
///
/// The source is what lets a result be delivered somewhere other than where the handler
/// put it. Replacing an original needs to know which original, and no handler reports
/// that, because until now nothing ever asked. It is null for results built from several
/// inputs at once, like a merge, where there is no single original to go back to.
/// </summary>
public sealed record ProducedFile(string Path, string? SourcePath = null);

/// <summary>Progress for the small window that appears while work runs.</summary>
public sealed record JobProgress(string Title, double Fraction, string? Detail = null);
