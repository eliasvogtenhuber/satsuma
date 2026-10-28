using Satsuma.Core.Model;
using Satsuma.Core.Tools;
using Satsuma.Core.Util;

namespace Satsuma.Core.Engine.Handlers;

/// <summary>Audio tools: compress, normalize, to mono, strip metadata, join.</summary>
public sealed class AudioToolHandler : MediaHandlerBase, IJobHandler
{
    private const string Prefix = "tool.aud.";

    private static readonly string[] Operations =
        { "compress", "normalize", "mono", "strip", "join" };

    public bool CanHandle(JobRequest request)
    {
        if (request.Category != FileCategory.Audio) return false;
        if (request.Item.Kind != WheelKind.Tools) return false;
        if (!FfmpegLocator.IsAvailable) return false;

        var operation = OperationFrom(request.Item.Id, Prefix, Operations);
        if (operation is null) return false;

        return operation != "join" || request.Files.Count > 1;
    }

    public bool IsAggregate(JobRequest request) =>
        OperationFrom(request.Item.Id, Prefix, Operations) == "join";

    public async Task<JobResult> ExecuteAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var operation = OperationFrom(request.Item.Id, Prefix, Operations)
                        ?? throw new NotSupportedException($"Unknown audio tool {request.Item.Id}.");

        if (operation == "join")
        {
            return await JoinAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }

        var source = request.Files[0];
        var options = request.Item.Options;

        // Lossy sources stay in their own format; lossless ones become M4A, since
        // re-encoding a FLAC as a FLAC to "compress" it would do nothing useful.
        var extension = source.Extension;
        List<string> args;
        string target;

        switch (operation)
        {
            case "compress":
            {
                var bitrate = options.TryGetValue("bitrate", out var value) ? value : "128k";
                extension = extension is ".wav" or ".flac" or ".aiff" or ".aif" ? ".m4a" : extension;

                target = OutputNaming.Next(source.Path, extension, "compressed");
                args = new List<string> { "-i", source.Path, "-vn" };
                args.AddRange(BitrateArgs(extension, bitrate));
                args.Add(target);
                break;
            }

            case "normalize":
            {
                // EBU R128 loudness normalisation, the same target broadcasters use.
                target = OutputNaming.Next(source.Path, extension, "normalized");
                args = new List<string>
                {
                    "-i", source.Path, "-vn",
                    "-af", "loudnorm=I=-16:TP=-1.5:LRA=11"
                };
                args.AddRange(AudioFormatHandler.EncoderArgs(Normalize(extension)));
                args.Add(target);
                break;
            }

            case "mono":
                target = OutputNaming.Next(source.Path, extension, "mono");
                args = new List<string> { "-i", source.Path, "-vn", "-ac", "1" };
                args.AddRange(AudioFormatHandler.EncoderArgs(Normalize(extension)));
                args.Add(target);
                break;

            case "strip":
                // Copying the stream keeps it bit-identical; only the tags go.
                target = OutputNaming.Next(source.Path, extension, "clean");
                args = new List<string>
                {
                    "-i", source.Path, "-map", "0:a", "-c:a", "copy",
                    "-map_metadata", "-1", "-map_chapters", "-1", target
                };
                break;

            default:
                return JobResult.Fail($"Unknown audio tool {request.Item.Id}.");
        }

        return await RunAsync(source.Path, target, args, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string Normalize(string extension) => extension.ToLowerInvariant() switch
    {
        ".aif" => ".aiff",
        ".aac" => ".m4a",
        ".alac" => ".m4a",
        _ => extension.ToLowerInvariant()
    };

    private static IEnumerable<string> BitrateArgs(string extension, string bitrate) =>
        extension.ToLowerInvariant() switch
        {
            ".mp3" => new[] { "-c:a", "libmp3lame", "-b:a", bitrate },
            ".m4a" or ".aac" => new[] { "-c:a", "aac", "-b:a", bitrate },
            ".ogg" => new[] { "-c:a", "libvorbis", "-b:a", bitrate },
            ".opus" => new[] { "-c:a", "libopus", "-b:a", bitrate },
            ".wma" => new[] { "-c:a", "wmav2", "-b:a", bitrate },
            _ => new[] { "-c:a", "aac", "-b:a", bitrate }
        };

    private async Task<JobResult> JoinAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var paths = request.Files.Select(f => f.Path).ToList();
        var target = OutputNaming.ForGroup(paths, ".m4a", "joined");

        var args = new List<string>();
        foreach (var path in paths)
        {
            args.AddRange(new[] { "-i", path });
        }

        var inputs = string.Concat(Enumerable.Range(0, paths.Count).Select(i => $"[{i}:a:0]"));
        args.AddRange(new[]
        {
            "-filter_complex", $"{inputs}concat=n={paths.Count}:v=0:a=1[a]",
            "-map", "[a]", "-c:a", "aac", "-b:a", "192k", target
        });

        var total = TimeSpan.Zero;
        foreach (var path in paths)
        {
            total += await Ffmpeg.ProbeDurationAsync(path, cancellationToken).ConfigureAwait(false)
                     ?? TimeSpan.Zero;
        }

        var reporter = progress is null ? null : new Action<double>(progress.Report);
        var outcome = await Ffmpeg
            .RunAsync(args, total > TimeSpan.Zero ? total : null, reporter, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            ImagePipeline.TryDelete(target);
            return JobResult.Fail(outcome.Tail());
        }

        return JobResult.Ok(target);
    }
}
