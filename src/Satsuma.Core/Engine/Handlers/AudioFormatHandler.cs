using Satsuma.Core.Model;
using Satsuma.Core.Tools;
using Satsuma.Core.Util;

namespace Satsuma.Core.Engine.Handlers;

/// <summary>Audio to audio.</summary>
public sealed class AudioFormatHandler : MediaHandlerBase, IJobHandler
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".m4a", ".wav", ".flac", ".ogg", ".opus", ".aiff", ".wma" };

    public bool CanHandle(JobRequest request)
    {
        if (request.Category != FileCategory.Audio) return false;
        if (request.Item.Kind != WheelKind.Formats) return false;
        if (!FfmpegLocator.IsAvailable) return false;

        var extension = request.Item.OutputExtension;
        return extension is not null && Known.Contains(extension);
    }

    public async Task<JobResult> ExecuteAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = request.Files[0];
        var extension = request.Item.OutputExtension!;
        var target = OutputNaming.Next(source.Path, extension);

        var args = new List<string> { "-i", source.Path, "-vn" };
        args.AddRange(EncoderArgs(extension));
        args.Add(target);

        return await RunAsync(source.Path, target, args, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static IEnumerable<string> EncoderArgs(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".mp3" => new[] { "-c:a", "libmp3lame", "-q:a", "2" },
            ".m4a" => new[] { "-c:a", "aac", "-b:a", "192k" },
            ".wav" => new[] { "-c:a", "pcm_s16le" },
            ".flac" => new[] { "-c:a", "flac" },
            ".ogg" => new[] { "-c:a", "libvorbis", "-q:a", "5" },
            ".opus" => new[] { "-c:a", "libopus", "-b:a", "128k" },
            ".aiff" => new[] { "-c:a", "pcm_s16be" },
            ".wma" => new[] { "-c:a", "wmav2", "-b:a", "192k" },
            _ => throw new NotSupportedException($"No audio recipe for {extension}.")
        };
}
