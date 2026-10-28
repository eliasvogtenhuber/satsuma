using System.Globalization;

namespace Satsuma.Core.Tools;

/// <summary>Thrown when a conversion needs ffmpeg and the machine has none.</summary>
public sealed class FfmpegMissingException : Exception
{
    public FfmpegMissingException()
        : base("ffmpeg was not found. Install it, or use \"Get ffmpeg\" from the Satsuma tray menu.")
    {
    }
}

/// <summary>Runs ffmpeg and ffprobe, and turns ffmpeg's progress stream into a fraction.</summary>
public static class Ffmpeg
{
    /// <summary>
    /// Runs ffmpeg. <paramref name="onProgress"/> gets 0..1 when the duration is known,
    /// which needs <paramref name="totalDuration"/> from a prior probe.
    /// </summary>
    public static async Task<ProcessOutcome> RunAsync(
        IEnumerable<string> arguments,
        TimeSpan? totalDuration = null,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var exe = FfmpegLocator.Ffmpeg ?? throw new FfmpegMissingException();

        // -progress writes machine-readable key=value pairs to stdout, which beats
        // scraping the human-readable stderr banner.
        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        args.AddRange(arguments);
        args.AddRange(new[] { "-nostats", "-progress", "pipe:1" });

        void HandleLine(string line)
        {
            if (onProgress is null || totalDuration is null) return;
            if (totalDuration.Value <= TimeSpan.Zero) return;

            var separator = line.IndexOf('=');
            if (separator <= 0) return;

            var key = line.AsSpan(0, separator).Trim();
            var value = line.AsSpan(separator + 1).Trim();

            if (key.SequenceEqual("out_time_us") || key.SequenceEqual("out_time_ms"))
            {
                // Despite the name, out_time_ms is also microseconds in current ffmpeg.
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
                    return;
                var fraction = micros / 1_000_000.0 / totalDuration.Value.TotalSeconds;
                onProgress(Math.Clamp(fraction, 0, 0.999));
            }
            else if (key.SequenceEqual("progress") && value.SequenceEqual("end"))
            {
                onProgress(1.0);
            }
        }

        return await ProcessRunner.RunAsync(exe, args, onStdOutLine: HandleLine,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Media duration, or null when ffprobe is missing or the file has none.</summary>
    public static async Task<TimeSpan?> ProbeDurationAsync(
        string path, CancellationToken cancellationToken = default)
    {
        var exe = FfmpegLocator.Ffprobe;
        if (exe is null) return null;

        var outcome = await ProcessRunner.RunAsync(exe, new[]
        {
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1",
            path
        }, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded) return null;

        var text = outcome.StdOut.Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
               && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    /// <summary>Pixel size of the first video or image stream.</summary>
    public static async Task<(int Width, int Height)?> ProbeSizeAsync(
        string path, CancellationToken cancellationToken = default)
    {
        var exe = FfmpegLocator.Ffprobe;
        if (exe is null) return null;

        var outcome = await ProcessRunner.RunAsync(exe, new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=width,height",
            "-of", "csv=s=x:p=0",
            path
        }, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded) return null;

        var parts = outcome.StdOut.Trim().Split('x');
        return parts.Length == 2
               && int.TryParse(parts[0], out var width)
               && int.TryParse(parts[1], out var height)
            ? (width, height)
            : null;
    }

    /// <summary>True when the file carries at least one audio stream.</summary>
    public static async Task<bool> HasAudioAsync(
        string path, CancellationToken cancellationToken = default)
    {
        var exe = FfmpegLocator.Ffprobe;
        if (exe is null) return true;   // assume yes; ffmpeg will tell us otherwise

        var outcome = await ProcessRunner.RunAsync(exe, new[]
        {
            "-v", "error",
            "-select_streams", "a",
            "-show_entries", "stream=index",
            "-of", "csv=p=0",
            path
        }, cancellationToken: cancellationToken).ConfigureAwait(false);

        return outcome.Succeeded && outcome.StdOut.Trim().Length > 0;
    }
}
