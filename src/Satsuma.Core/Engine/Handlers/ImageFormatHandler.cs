using Satsuma.Core.Model;
using Satsuma.Core.Tools;
using Satsuma.Core.Util;

namespace Satsuma.Core.Engine.Handlers;

/// <summary>Image to image, one output per input.</summary>
public sealed class ImageFormatHandler : IJobHandler
{
    public bool CanHandle(JobRequest request)
    {
        if (request.Category != FileCategory.Image) return false;
        if (request.Item.Kind != WheelKind.Formats) return false;

        var extension = request.Item.OutputExtension;
        return extension is not null && ImageCodec.CanWrite(extension);
    }

    public async Task<JobResult> ExecuteAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = request.Files[0];
        var extension = request.Item.OutputExtension!;
        var target = OutputNaming.Next(source.Path, extension);

        progress?.Report(0.1);

        var result = await ImagePipeline
            .ConvertAsync(source.Path, target, extension, quality: 92,
                transform: null, stripMetadata: false, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(1.0);
        return result;
    }
}
