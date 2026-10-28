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
        var quality = ReadInt(options, "quality") ?? 92;

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
                var scale = ReadDouble(options, "scale");
                var longEdge = ReadInt(options, "longEdge");

                if (scale is not null)
                {
                    var factor = scale.Value;
                    transform = image => ImageCodec.Scale(image, factor);
                    suffix = $"{(int)Math.Round(factor * 100)}pct";
                }
                else if (longEdge is not null)
                {
                    var edge = longEdge.Value;
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
        return result;
    }

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
