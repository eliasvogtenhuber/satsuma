using System.Globalization;
using Satsuma.Core.Model;
using Satsuma.Core.Tools;

namespace Satsuma.Core.Engine.Handlers;

/// <summary>Shared plumbing for the handlers that drive ffmpeg over a timeline.</summary>
public abstract class MediaHandlerBase
{
    /// <summary>
    /// Probes the duration so progress is a real fraction rather than a spinner, then runs
    /// ffmpeg and checks that something actually landed on disk.
    /// </summary>
    protected static async Task<JobResult> RunAsync(
        string sourcePath,
        string targetPath,
        IEnumerable<string> arguments,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var duration = await Ffmpeg.ProbeDurationAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false);

        var reporter = progress is null ? null : new Action<double>(progress.Report);

        var outcome = await Ffmpeg
            .RunAsync(arguments, duration, reporter, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            ImagePipeline.TryDelete(targetPath);
            return JobResult.Fail(outcome.Tail());
        }

        if (!File.Exists(targetPath) || new FileInfo(targetPath).Length == 0)
        {
            ImagePipeline.TryDelete(targetPath);
            return JobResult.Fail("ffmpeg reported success but wrote nothing.");
        }

        return JobResult.Ok(targetPath);
    }

    protected static int? ReadInt(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var raw)
        && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    protected static double? ReadDouble(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var raw)
        && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>Formats a number the way ffmpeg expects it, regardless of system locale.</summary>
    protected static string Num(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Pulls the operation out of a leaf id like <c>tool.vid.speed.2</c>.</summary>
    protected static string? OperationFrom(string id, string prefix, params string[] known)
    {
        if (!id.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var operation = id[prefix.Length..].Split('.')[0];
        return known.Contains(operation, StringComparer.Ordinal) ? operation : null;
    }
}
