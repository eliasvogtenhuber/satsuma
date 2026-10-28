using System.Windows.Media.Imaging;
using Satsuma.Core.Model;
using Satsuma.Core.Tools;

namespace Satsuma.Core.Engine.Handlers;

/// <summary>
/// The one place image conversion actually happens, shared by the format wheel and the
/// tools wheel.
///
/// Two writers exist and neither covers everything: WIC reads and writes the formats
/// Windows knows, ffmpeg covers WebP and AVIF. The pipeline prefers a single ffmpeg pass
/// when ffmpeg owns the output anyway, then a single WIC pass, and only bounces through a
/// lossless PNG when the pair of formats has no direct route.
///
/// Every WIC call is wrapped in <see cref="StaExecutor"/>, and a whole decode-transform-
/// encode runs inside one such call: a <see cref="BitmapSource"/> belongs to the thread
/// that made it, so the pixels must never cross back out mid-conversion.
/// </summary>
internal static class ImagePipeline
{
    private enum WicStatus
    {
        /// <summary>WIC wrote the final file.</summary>
        Done,

        /// <summary>WIC wrote a lossless PNG for ffmpeg to encode.</summary>
        Staged,

        /// <summary>No installed codec could read the source.</summary>
        CannotDecode,

        /// <summary>It could be read, but something later went wrong.</summary>
        Failed
    }

    private sealed record WicResult(WicStatus Status, string? Error = null, string? StagePath = null);

    public static async Task<JobResult> ConvertAsync(
        string sourcePath,
        string targetPath,
        string targetExtension,
        int quality,
        Func<BitmapSource, BitmapSource>? transform,
        bool stripMetadata,
        CancellationToken cancellationToken)
    {
        // Fast path: nothing to transform and ffmpeg owns the output format, so let
        // ffmpeg read the original directly and skip WIC entirely.
        if (transform is null && ImageCodec.NeedsFfmpegToWrite(targetExtension))
        {
            var direct = await TryFfmpegAsync(sourcePath, targetPath, targetExtension, quality,
                cancellationToken).ConfigureAwait(false);
            if (direct is not null) return direct;
        }

        var wic = await StaExecutor.RunAsync(
            () => RunWic(sourcePath, targetPath, targetExtension, quality, transform, stripMetadata),
            cancellationToken).ConfigureAwait(false);

        // Nothing installed reads this format. Let ffmpeg turn it into a PNG and retry.
        if (wic.Status == WicStatus.CannotDecode)
        {
            var bouncePath = TempPath(".png");
            try
            {
                var bounced = await TryFfmpegAsync(sourcePath, bouncePath, ".png", 100,
                    cancellationToken).ConfigureAwait(false);

                if (bounced is null)
                {
                    return JobResult.Fail(wic.Error ?? (FfmpegLocator.IsAvailable
                        ? "Nothing installed can read this image."
                        : "Nothing installed can read this image, and ffmpeg is missing."));
                }

                wic = await StaExecutor.RunAsync(
                    () => RunWic(bouncePath, targetPath, targetExtension, quality, transform,
                        stripMetadata),
                    cancellationToken).ConfigureAwait(false);

                if (wic.Status == WicStatus.CannotDecode)
                {
                    return JobResult.Fail(wic.Error ?? "Could not read this image.");
                }
            }
            finally
            {
                TryDelete(bouncePath);
            }
        }

        switch (wic.Status)
        {
            case WicStatus.Done:
                return JobResult.Ok(targetPath);

            case WicStatus.Failed:
                TryDelete(targetPath);
                return JobResult.Fail(wic.Error ?? "Could not convert this image.");

            case WicStatus.Staged:
            {
                var stagePath = wic.StagePath!;
                try
                {
                    var encoded = await TryFfmpegAsync(stagePath, targetPath, targetExtension,
                        quality, cancellationToken).ConfigureAwait(false);

                    return encoded ?? JobResult.Fail(FfmpegLocator.IsAvailable
                        ? $"ffmpeg could not write {targetExtension}."
                        : $"Writing {targetExtension} needs ffmpeg.");
                }
                finally
                {
                    TryDelete(stagePath);
                }
            }

            default:
                return JobResult.Fail("Could not convert this image.");
        }
    }

    /// <summary>
    /// The whole WIC half of a conversion, start to finish, on one STA thread.
    /// </summary>
    private static WicResult RunWic(
        string sourcePath,
        string targetPath,
        string targetExtension,
        int quality,
        Func<BitmapSource, BitmapSource>? transform,
        bool stripMetadata)
    {
        var decoded = ImageCodec.TryDecode(sourcePath, out var metadata, out var decodeError);
        if (decoded is null) return new WicResult(WicStatus.CannotDecode, decodeError);

        try
        {
            if (transform is not null) decoded = transform(decoded);
            if (stripMetadata) metadata = null;

            if (ImageCodec.CanWriteWithWic(targetExtension))
            {
                ImageCodec.SaveWithWic(decoded, targetPath, quality, metadata);
                return new WicResult(WicStatus.Done);
            }

            // WebP or AVIF: hand ffmpeg a lossless PNG of the finished pixels.
            var stagePath = TempPath(".png");
            ImageCodec.SaveWithWic(decoded, stagePath, 100, metadata: null);
            return new WicResult(WicStatus.Staged, null, stagePath);
        }
        catch (Exception ex)
        {
            return new WicResult(WicStatus.Failed, ex.Message);
        }
    }

    /// <summary>
    /// One ffmpeg still-image pass. Returns null when ffmpeg is missing or refused the
    /// job, which tells the caller to try another route rather than to give up.
    /// </summary>
    private static async Task<JobResult?> TryFfmpegAsync(
        string sourcePath,
        string targetPath,
        string targetExtension,
        int quality,
        CancellationToken cancellationToken)
    {
        if (!FfmpegLocator.IsAvailable) return null;

        var args = new List<string> { "-i", sourcePath, "-frames:v", "1" };
        args.AddRange(EncoderArgs(targetExtension, quality));
        args.Add(targetPath);

        try
        {
            var outcome = await Ffmpeg.RunAsync(args, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (outcome.Succeeded && File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
            {
                return JobResult.Ok(targetPath);
            }

            TryDelete(targetPath);
            return null;
        }
        catch (FfmpegMissingException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EncoderArgs(string extension, int quality) =>
        extension.ToLowerInvariant() switch
        {
            ".webp" => new[]
            {
                "-c:v", "libwebp", "-quality", Math.Clamp(quality, 1, 100).ToString(),
                "-preset", "picture"
            },
            ".avif" => new[]
            {
                "-c:v", "libaom-av1", "-still-picture", "1", "-cpu-used", "6",
                "-crf", AvifCrf(quality).ToString()
            },
            ".png" => new[] { "-c:v", "png" },
            ".jpg" or ".jpeg" => new[] { "-q:v", JpegQScale(quality).ToString() },
            _ => Array.Empty<string>()
        };

    /// <summary>Maps a 1..100 quality onto an AV1 CRF, where lower means better.</summary>
    private static int AvifCrf(int quality) =>
        Math.Clamp((int)Math.Round((100 - Math.Clamp(quality, 1, 100)) * 0.55) + 12, 12, 58);

    /// <summary>Maps a 1..100 quality onto ffmpeg's mjpeg qscale, where lower means better.</summary>
    private static int JpegQScale(int quality) =>
        Math.Clamp((int)Math.Round((100 - Math.Clamp(quality, 1, 100)) * 0.3) + 2, 2, 31);

    internal static string TempPath(string extension)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Satsuma");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Guid.NewGuid().ToString("N") + extension);
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { /* a temp file left behind is not worth failing over */ }
        catch (UnauthorizedAccessException) { }
    }
}
