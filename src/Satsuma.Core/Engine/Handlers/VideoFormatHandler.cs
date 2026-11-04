using Satsuma.Core.Model;
using Satsuma.Core.Tools;
using Satsuma.Core.Util;

namespace Satsuma.Core.Engine.Handlers;

/// <summary>Video to another container, to an animated GIF, or to an audio-only file.</summary>
public sealed class VideoFormatHandler : MediaHandlerBase, IJobHandler
{
    public bool CanHandle(JobRequest request)
    {
        if (request.Category != FileCategory.Video) return false;
        if (request.Item.Kind != WheelKind.Formats) return false;
        if (!FfmpegLocator.IsAvailable) return false;

        var extension = request.Item.OutputExtension;
        if (extension is null || !Known.Contains(extension)) return false;

        // MP4, MOV and MKV are H.264 here. A build without an H.264 encoder cannot
        // make them, and a slice that fails after the drop is worse than one that was
        // never drawn.
        return !NeedsH264.Contains(extension) || H264.IsAvailable;
    }

    private static readonly HashSet<string> NeedsH264 = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv"
    };

    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".gif", ".mp3" };

    public async Task<JobResult> ExecuteAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = request.Files[0];
        var extension = request.Item.OutputExtension!;
        var target = OutputNaming.Next(source.Path, extension);

        var args = new List<string> { "-i", source.Path };
        args.AddRange(await EncoderArgsAsync(extension, source.Path, cancellationToken)
            .ConfigureAwait(false));
        args.Add(target);

        return await RunAsync(source.Path, target, args, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IEnumerable<string>> EncoderArgsAsync(
        string extension, string sourcePath, CancellationToken cancellationToken)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".mp4":
            case ".mov":
                // faststart puts the index up front, so the file plays while it downloads.
                return H264.Args(23).Concat(new[]
                {
                    "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"
                });

            case ".mkv":
                return H264.Args(23).Concat(new[]
                {
                    "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-b:a", "192k"
                });

            case ".webm":
                // -b:v 0 is what puts libvpx-vp9 into constant-quality mode.
                return new[]
                {
                    "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1",
                    "-c:a", "libopus", "-b:a", "128k"
                };

            case ".avi":
                return new[]
                {
                    "-c:v", "mpeg4", "-vtag", "xvid", "-qscale:v", "4",
                    "-c:a", "libmp3lame", "-q:a", "3"
                };

            case ".wmv":
                return new[]
                {
                    "-c:v", "wmv2", "-b:v", "2500k",
                    "-c:a", "wmav2", "-b:a", "192k"
                };

            case ".gif":
            {
                // A GIF is limited to 256 colours, so a palette generated from this clip
                // beats the fixed web palette by a wide margin.
                const string filter =
                    "fps=12,scale=480:-1:flags=lanczos,split[s0][s1];" +
                    "[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=5";
                return new[] { "-an", "-filter_complex", filter, "-loop", "0" };
            }

            case ".mp3":
            {
                var hasAudio = await Ffmpeg.HasAudioAsync(sourcePath, cancellationToken)
                    .ConfigureAwait(false);
                if (!hasAudio) throw new InvalidOperationException("This video has no audio track.");

                return new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "2" };
            }

            default:
                throw new NotSupportedException($"No video recipe for {extension}.");
        }
    }
}
