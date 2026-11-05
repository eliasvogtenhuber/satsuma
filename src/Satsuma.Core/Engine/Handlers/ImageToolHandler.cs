using System.Globalization;
using System.Windows.Media.Imaging;
using Satsuma.Core.Catalog;
using Satsuma.Core.Model;
using Satsuma.Core.Tools;
using Satsuma.Core.Util;

namespace Satsuma.Core.Engine.Handlers;

/// <summary>
/// The image tools: compress, resize, rotate, flip, grayscale, strip metadata.
///
/// Results keep the source format when Satsuma can write it, and fall back to JPG when it
/// cannot. That matters for HEIC, which Windows reads but nothing here writes, so
/// compressing a HEIC gives a JPG rather than an error.
/// </summary>
public sealed class ImageToolHandler : IJobHandler
{
    public bool CanHandle(JobRequest request)
    {
        if (request.Category != FileCategory.Image) return false;
        if (request.Item.Kind != WheelKind.Tools) return false;

        return Operation(request.Item.Id) is not null;
    }

    public async Task<JobResult> ExecuteAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = request.Files[0];
        var item = request.Item;
        var operation = Operation(item.Id)
                        ?? throw new NotSupportedException($"Unknown image tool {item.Id}.");

        var options = item.Options;
        var quality = ReadQuality(options) ?? 92;

        Func<BitmapSource, BitmapSource>? transform = null;
        var stripMetadata = false;
        string suffix;

        switch (operation)
        {
            case "compress":
                suffix = "compressed";
                break;

            case "resize":
            {
                var size = ReadSize(options);
                if (size is null) return JobResult.Fail("Resize was asked for without a size.");

                var wanted = size.Value;

                if (wanted.IsExact)
                {
                    var width = wanted.Width!.Value;
                    var height = wanted.Height!.Value;

                    transform = image => ImageCodec.ScaleTo(image, width, height);
                    suffix = $"{width}x{height}";
                }
                else if (wanted.Scale is not null)
                {
                    var factor = wanted.Scale.Value;
                    transform = image => ImageCodec.Scale(image, factor);
                    suffix = $"{(int)Math.Round(factor * 100)}pct";
                }
                else if (wanted.LongEdge is not null)
                {
                    var edge = wanted.LongEdge.Value;
                    transform = image => ImageCodec.ScaleToLongEdge(image, edge);
                    suffix = $"{edge}p";
                }
                else
                {
                    return JobResult.Fail("Resize was asked for without a size.");
                }
                break;
            }

            case "rotate":
            {
                var degrees = ReadInt(options, "degrees") ?? 90;
                transform = image => ImageCodec.Rotate(image, degrees);
                suffix = "rotated";
                break;
            }

            case "flip":
            {
                var horizontal = !string.Equals(
                    options.TryGetValue("axis", out var axis) ? axis : "h", "v",
                    StringComparison.OrdinalIgnoreCase);
                transform = image => ImageCodec.Flip(image, horizontal);
                suffix = "flipped";
                break;
            }

            case "grayscale":
                transform = ImageCodec.Grayscale;
                suffix = "gray";
                break;

            case "strip":
                stripMetadata = true;
                suffix = "clean";
                break;

            default:
                return JobResult.Fail($"Unknown image tool {item.Id}.");
        }

        var extension = TargetExtension(source.Extension);
        var target = OutputNaming.Next(source.Path, extension, suffix);

        progress?.Report(0.1);

        var result = await ImagePipeline
            .ConvertAsync(source.Path, target, extension, quality, transform, stripMetadata,
                cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(1.0);

        // Compress keeps the source format, and the lossless ones have no quality dial
        // for WIC to turn: a "compressed" PNG is the same pixels re-encoded, which is
        // usually the same size and sometimes bigger. Saying so beats handing back a
        // copy named "compressed" and letting someone discover it later.
        if (operation == "compress" && result.Status == JobStatus.Succeeded)
        {
            return KeptOnlyIfSmaller(source.Path, result, extension);
        }

        return result;
    }

    /// <summary>
    /// Throws away a compression that did not compress, and says what would.
    /// </summary>
    private static JobResult KeptOnlyIfSmaller(
        string sourcePath, JobResult result, string extension)
    {
        if (result.OutputPaths.Count != 1) return result;

        var written = result.OutputPaths[0];

        try
        {
            var before = new FileInfo(sourcePath).Length;
            var after = new FileInfo(written).Length;

            if (after < before) return result;

            ImagePipeline.TryDelete(written);

            return Lossless.Contains(extension)
                ? JobResult.Fail(
                    $"{extension.TrimStart('.').ToUpperInvariant()} has no quality to trade " +
                    "away. Convert it to JPG or WebP to make it smaller.")
                : JobResult.Fail("Already as small as this can usefully make it.");
        }
        catch (Exception)
        {
            return result;
        }
    }

    /// <summary>Formats whose encoders ignore a quality setting entirely.</summary>
    private static readonly HashSet<string> Lossless = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".gif", ".bmp", ".tif", ".tiff"
    };

    /// <summary>
    /// Keeps the source format when it is writable, otherwise JPG. Grayscale of a PNG
    /// stays a PNG; compressing a HEIC becomes a JPG.
    /// </summary>
    private static string TargetExtension(string sourceExtension)
    {
        var normalized = FormatCatalog.NormalizeExtension(sourceExtension);
        return ImageCodec.CanWrite(normalized) ? normalized : ".jpg";
    }

    /// <summary>Pulls the operation out of a leaf id like <c>tool.img.resize.50</c>.</summary>
    private static string? Operation(string id)
    {
        const string prefix = "tool.img.";
        if (!id.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var rest = id[prefix.Length..];
        var operation = rest.Split('.')[0];

        return operation switch
        {
            // "Make PDF" is catalogued but has no writer yet, so it stays off the wheel.
            "compress" or "resize" or "rotate" or "flip" or "grayscale" or "strip" => operation,
            _ => null
        };
    }

    /// <summary>
    /// The size to use: what the user typed, where a Custom slice asked, and otherwise
    /// whatever the preset carries.
    /// </summary>
    /// <summary>A typed quality, or the preset the slice carries.</summary>
    private static int? ReadQuality(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue(WheelItem.CustomOption, out var typed)
            && CustomInput.TryParseQuality(typed, out var custom))
        {
            return custom;
        }

        return ReadInt(options, "quality");
    }

    private static SizeRequest? ReadSize(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue(WheelItem.CustomOption, out var typed)
            && CustomInput.TryParseSize(typed, out var custom))
        {
            return custom;
        }

        var scale = ReadDouble(options, "scale");
        if (scale is not null) return new SizeRequest(scale, null, null, null);

        var longEdge = ReadInt(options, "longEdge");
        if (longEdge is not null) return new SizeRequest(null, longEdge, null, null);

        return null;
    }

    private static int? ReadInt(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var raw)
        && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static double? ReadDouble(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var raw)
        && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
