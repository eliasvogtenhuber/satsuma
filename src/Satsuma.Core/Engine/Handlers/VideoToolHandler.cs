using Satsuma.Core.Model;
using Satsuma.Core.Tools;
using Satsuma.Core.Util;

namespace Satsuma.Core.Engine.Handlers;

/// <summary>Video tools: compress, resize, speed, rotate, mute, snapshot, extract audio, join.</summary>
public sealed class VideoToolHandler : MediaHandlerBase, IJobHandler
{
    private const string Prefix = "tool.vid.";

    private static readonly string[] Operations =
        { "compress", "resize", "speed", "rotate", "mute", "snapshot", "audio", "join" };

    /// <summary>Operations that leave the video stream alone.</summary>
    private static readonly HashSet<string> PassesVideoThrough = new(StringComparer.Ordinal)
    {
        "mute", "snapshot", "audio"
    };

    public bool CanHandle(JobRequest request)
    {
        if (request.Category != FileCategory.Video) return false;
        if (request.Item.Kind != WheelKind.Tools) return false;
        if (!FfmpegLocator.IsAvailable) return false;

        var operation = OperationFrom(request.Item.Id, Prefix, Operations);
        if (operation is null) return false;

        // Everything that re-encodes the picture needs an H.264 encoder; the ones that
        // only touch the container or the audio do not.
        if (!H264.IsAvailable && !PassesVideoThrough.Contains(operation)) return false;

        // Joining one clip to nothing is not a job.
        return operation != "join" || request.Files.Count > 1;
    }

    public bool IsAggregate(JobRequest request) =>
        OperationFrom(request.Item.Id, Prefix, Operations) == "join";

    public async Task<JobResult> ExecuteAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var operation = OperationFrom(request.Item.Id, Prefix, Operations)
                        ?? throw new NotSupportedException($"Unknown video tool {request.Item.Id}.");

        if (operation == "join")
        {
            return await JoinAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }

        var source = request.Files[0];
        var options = request.Item.Options;
        var sourceExtension = source.Extension is ".mkv" or ".webm" or ".mov" ? source.Extension : ".mp4";

        List<string> args;
        string target;

        switch (operation)
        {
            case "compress":
            {
                // A typed quality is 1..100 where higher is better; x264 counts the
                // other way, so it is mapped rather than passed straight through.
                var crf = options.TryGetValue(WheelItem.CustomOption, out var typedQuality)
                          && CustomInput.TryParseQuality(typedQuality, out var quality)
                    ? (int)Math.Round(51 - quality * 0.45)
                    : ReadInt(options, "crf") ?? 30;
                target = OutputNaming.Next(source.Path, sourceExtension, "compressed");
                args = new List<string> { "-i", source.Path };
                args.AddRange(H264.Args(crf));
                args.AddRange(new[]
                {
                    "-pix_fmt", "yuv420p",
                    // Audio is copied, not re-encoded: the ask was to compress the video,
                    // and re-encoding could raise the audio bitrate above the source.
                    "-c:a", "copy", target
                });
                break;
            }

            case "resize":
            {
                var typedSize = options.TryGetValue(WheelItem.CustomOption, out var sizeText)
                                && CustomInput.TryParseSize(sizeText, out var parsed)
                    ? parsed
                    : (SizeRequest?)null;

                var scale = typedSize?.Scale ?? ReadDouble(options, "scale");
                var longEdge = typedSize?.LongEdge ?? ReadInt(options, "longEdge");
                var exact = typedSize?.IsExact == true ? typedSize : null;

                // -2 keeps the other edge even, which h264's yuv420p requires.
                string filter;
                string suffix;

                if (exact is not null)
                {
                    // Rounded down to even, which is what yuv420p needs; an odd
                    // dimension is rejected by the encoder outright.
                    var width = exact.Value.Width!.Value / 2 * 2;
                    var height = exact.Value.Height!.Value / 2 * 2;

                    filter = $"scale={width}:{height}";
                    suffix = $"{width}x{height}";
                }
                else if (scale is not null)
                {
                    filter = $"scale=trunc(iw*{Num(scale.Value)}/2)*2:trunc(ih*{Num(scale.Value)}/2)*2";
                    suffix = $"{(int)Math.Round(scale.Value * 100)}pct";
                }
                else if (longEdge is not null)
                {
                    var edge = longEdge.Value;
                    // Scale the long edge whichever way round the video is.
                    filter = $"scale=if(gte(iw\\,ih)\\,min({edge}\\,iw)\\,-2):if(gte(iw\\,ih)\\,-2\\,min({edge}\\,ih))";
                    suffix = $"{edge}p";
                }
                else
                {
                    return JobResult.Fail("Resize was asked for without a size.");
                }

                target = OutputNaming.Next(source.Path, sourceExtension, suffix);
                args = new List<string> { "-i", source.Path, "-vf", filter };
                args.AddRange(H264.Args(23));
                args.AddRange(new[] { "-pix_fmt", "yuv420p", "-c:a", "copy", target });
                break;
            }

            case "speed":
            {
                var factor = options.TryGetValue(WheelItem.CustomOption, out var speedText)
                             && CustomInput.TryParseSpeed(speedText, out var typedSpeed)
                    ? typedSpeed
                    : ReadDouble(options, "factor") ?? 2.0;
                if (factor <= 0) return JobResult.Fail("Speed factor must be positive.");

                target = OutputNaming.Next(source.Path, sourceExtension, $"{Num(factor)}x");

                var hasAudio = await Ffmpeg.HasAudioAsync(source.Path, cancellationToken)
                    .ConfigureAwait(false);

                args = new List<string> { "-i", source.Path };

                if (hasAudio)
                {
                    // atempo only accepts 0.5..2.0 per pass, so larger changes are chained.
                    var chain = BuildAtempoChain(factor);
                    args.AddRange(new[]
                    {
                        "-filter_complex",
                        $"[0:v]setpts=PTS/{Num(factor)}[v];[0:a]{chain}[a]",
                        "-map", "[v]", "-map", "[a]"
                    });
                }
                else
                {
                    args.AddRange(new[] { "-filter:v", $"setpts=PTS/{Num(factor)}", "-an" });
                }

                args.AddRange(H264.Args(23));
                args.AddRange(new[] { "-pix_fmt", "yuv420p", target });
                break;
            }

            case "rotate":
            {
                var degrees = ReadInt(options, "degrees") ?? 90;
                var filter = degrees switch
                {
                    90 => "transpose=1",
                    270 => "transpose=2",
                    180 => "transpose=1,transpose=1",
                    _ => "transpose=1"
                };

                target = OutputNaming.Next(source.Path, sourceExtension, "rotated");
                args = new List<string> { "-i", source.Path, "-vf", filter };
                args.AddRange(H264.Args(23));
                args.AddRange(new[] { "-pix_fmt", "yuv420p", "-c:a", "copy", target });
                break;
            }

            case "mute":
                target = OutputNaming.Next(source.Path, source.Extension, "muted");
                args = new List<string> { "-i", source.Path, "-an", "-c:v", "copy", target };
                break;

            case "snapshot":
            {
                var duration = await Ffmpeg.ProbeDurationAsync(source.Path, cancellationToken)
                    .ConfigureAwait(false);
                var midpoint = (duration?.TotalSeconds ?? 0) / 2;

                target = OutputNaming.Next(source.Path, ".png", "frame");
                args = new List<string>
                {
                    "-ss", Num(midpoint), "-i", source.Path, "-frames:v", "1", target
                };
                break;
            }

            case "audio":
            {
                var hasAudio = await Ffmpeg.HasAudioAsync(source.Path, cancellationToken)
                    .ConfigureAwait(false);
                if (!hasAudio) return JobResult.Fail("This video has no audio track.");

                target = OutputNaming.Next(source.Path, ".mp3");
                args = new List<string>
                {
                    "-i", source.Path, "-vn", "-c:a", "libmp3lame", "-q:a", "2", target
                };
                break;
            }

            default:
                return JobResult.Fail($"Unknown video tool {request.Item.Id}.");
        }

        return await RunAsync(source.Path, target, args, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// atempo is clamped to 0.5..2.0, so a 4x change becomes two 2x passes.
    /// </summary>
    private static string BuildAtempoChain(double factor)
    {
        var stages = new List<string>();
        var remaining = factor;

        while (remaining > 2.0)
        {
            stages.Add("atempo=2.0");
            remaining /= 2.0;
        }
        while (remaining < 0.5)
        {
            stages.Add("atempo=0.5");
            remaining /= 0.5;
        }

        stages.Add($"atempo={Num(remaining)}");
        return string.Join(",", stages);
    }

    /// <summary>
    /// Concatenates clips. The concat filter re-encodes, which is slower than the concat
    /// demuxer but survives clips that differ in codec, resolution or frame rate.
    /// </summary>
    private async Task<JobResult> JoinAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var files = request.Files;
        var paths = files.Select(f => f.Path).ToList();

        var audioFlags = new List<bool>();
        foreach (var path in paths)
        {
            audioFlags.Add(await Ffmpeg.HasAudioAsync(path, cancellationToken).ConfigureAwait(false));
        }

        // Audio is only carried through when every clip has some; otherwise the
        // concat filter would need silence generated for the gaps.
        var withAudio = audioFlags.All(x => x);

        var target = OutputNaming.ForGroup(paths, ".mp4", "joined");

        var args = new List<string>();
        foreach (var path in paths)
        {
            args.AddRange(new[] { "-i", path });
        }

        var inputs = string.Concat(Enumerable.Range(0, paths.Count)
            .Select(i => withAudio ? $"[{i}:v:0][{i}:a:0]" : $"[{i}:v:0]"));

        var filter = withAudio
            ? $"{inputs}concat=n={paths.Count}:v=1:a=1[v][a]"
            : $"{inputs}concat=n={paths.Count}:v=1:a=0[v]";

        args.AddRange(new[] { "-filter_complex", filter, "-map", "[v]" });
        if (withAudio) args.AddRange(new[] { "-map", "[a]", "-c:a", "aac", "-b:a", "192k" });

        args.AddRange(H264.Args(23));
        args.AddRange(new[] { "-pix_fmt", "yuv420p", target });

        // Progress is measured against the first clip only, so report against the total.
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
