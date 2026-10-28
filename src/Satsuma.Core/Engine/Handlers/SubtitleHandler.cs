using System.Text;
using System.Text.RegularExpressions;
using Satsuma.Core.Model;
using Satsuma.Core.Tools;
using Satsuma.Core.Util;

namespace Satsuma.Core.Engine.Handlers;

/// <summary>
/// Subtitle conversion. SRT and VTT go through ffmpeg; plain text is done here, since
/// stripping timings is a few lines and should not need ffmpeg installed.
/// </summary>
public sealed partial class SubtitleHandler : MediaHandlerBase, IJobHandler
{
    public bool CanHandle(JobRequest request)
    {
        if (request.Category != FileCategory.Subtitle) return false;
        if (request.Item.Kind != WheelKind.Formats) return false;

        return request.Item.OutputExtension switch
        {
            ".txt" => true,                        // done in-process
            ".srt" or ".vtt" => FfmpegLocator.IsAvailable,
            _ => false
        };
    }

    public async Task<JobResult> ExecuteAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = request.Files[0];
        var extension = request.Item.OutputExtension!;
        var target = OutputNaming.Next(source.Path, extension);

        if (extension == ".txt")
        {
            await ToPlainTextAsync(source.Path, target, cancellationToken).ConfigureAwait(false);
            progress?.Report(1.0);
            return JobResult.Ok(target);
        }

        var args = new List<string> { "-i", source.Path, target };
        return await RunAsync(source.Path, target, args, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps the spoken lines and drops everything structural: cue numbers, timing lines,
    /// the WEBVTT header, and inline tags like &lt;i&gt; or {\an8}.
    /// </summary>
    private static async Task ToPlainTextAsync(
        string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false);

        var output = new List<string>();
        var previousWasBlank = true;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.Length == 0)
            {
                if (!previousWasBlank) output.Add(string.Empty);
                previousWasBlank = true;
                continue;
            }

            if (line.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("NOTE", StringComparison.Ordinal)) continue;
            if (TimingLine().IsMatch(line)) continue;
            if (CueNumberLine().IsMatch(line)) continue;

            var cleaned = InlineTag().Replace(line, string.Empty);
            cleaned = AssOverride().Replace(cleaned, string.Empty).Trim();

            if (cleaned.Length == 0) continue;

            output.Add(cleaned);
            previousWasBlank = false;
        }

        // Collapse any run of blank lines the filtering left behind.
        while (output.Count > 0 && output[^1].Length == 0) output.RemoveAt(output.Count - 1);

        await File.WriteAllLinesAsync(targetPath, output, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
    }

    [GeneratedRegex(@"^\d{1,2}:\d{2}(:\d{2})?[.,]\d{1,3}\s*-->", RegexOptions.Compiled)]
    private static partial Regex TimingLine();

    [GeneratedRegex(@"^\d+$", RegexOptions.Compiled)]
    private static partial Regex CueNumberLine();

    [GeneratedRegex(@"</?[a-zA-Z][^>]*>", RegexOptions.Compiled)]
    private static partial Regex InlineTag();

    [GeneratedRegex(@"\{\\[^}]*\}", RegexOptions.Compiled)]
    private static partial Regex AssOverride();
}
