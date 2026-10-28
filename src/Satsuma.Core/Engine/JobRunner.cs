using Satsuma.Core.Catalog;
using Satsuma.Core.Engine.Handlers;
using Satsuma.Core.Model;

namespace Satsuma.Core.Engine;

/// <summary>Outcome of running a whole wheel pick over a whole selection.</summary>
public sealed record BatchResult(
    IReadOnlyList<string> Outputs,
    IReadOnlyList<string> Failures,
    bool Cancelled = false)
{
    public bool AnySucceeded => Outputs.Count > 0;
    public bool AllSucceeded => Failures.Count == 0 && !Cancelled;
}

/// <summary>
/// Picks the handler for a wheel item and runs it over the selection, turning per-file
/// progress into one fraction for the progress window.
/// </summary>
public sealed class JobRunner
{
    private readonly List<IJobHandler> _handlers;

    public JobRunner(IEnumerable<IJobHandler>? handlers = null)
    {
        _handlers = (handlers ?? DefaultHandlers()).ToList();
    }

    private static IEnumerable<IJobHandler> DefaultHandlers() => new IJobHandler[]
    {
        new ImageFormatHandler(),
        new ImageToolHandler(),
        new VideoFormatHandler(),
        new VideoToolHandler(),
        new AudioFormatHandler(),
        new AudioToolHandler(),
        new SubtitleHandler(),
        new ArchiveHandler(),
    };

    public bool CanHandle(JobRequest request) => Find(request) is not null;

    private IJobHandler? Find(JobRequest request) =>
        _handlers.FirstOrDefault(h => h.CanHandle(request));

    /// <summary>
    /// The wheel for this selection, with everything no handler claims removed.
    /// Parent items survive only if at least one of their children survives.
    /// </summary>
    public IReadOnlyList<WheelItem> BuildWheel(
        WheelKind kind, FileCategory category, IReadOnlyList<SourceFile> files)
    {
        var candidates = kind switch
        {
            WheelKind.Formats => FormatCatalog.For(category, files),
            WheelKind.Tools => ToolCatalog.For(category),
            WheelKind.Actions => ActionCatalog.For(category),
            _ => Array.Empty<WheelItem>()
        };

        // Actions never transform anything, so they always apply.
        if (kind == WheelKind.Actions) return candidates;

        var kept = new List<WheelItem>();
        foreach (var item in candidates)
        {
            if (item.HasChildren)
            {
                var children = item.Children
                    .Where(c => CanHandle(new JobRequest(c, files, category)))
                    .ToList();

                if (children.Count > 0)
                {
                    kept.Add(item with { Children = children });
                }
            }
            else if (CanHandle(new JobRequest(item, files, category)))
            {
                kept.Add(item);
            }
        }

        return kept;
    }

    public async Task<BatchResult> RunAsync(
        JobRequest request,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var handler = Find(request);
        if (handler is null)
        {
            return new BatchResult(
                Array.Empty<string>(),
                new[] { $"Nothing here can do {request.Item.Label}." });
        }

        var title = request.Item.Label;
        var outputs = new List<string>();
        var failures = new List<string>();

        try
        {
            if (handler.IsAggregate(request))
            {
                var inner = new Progress<double>(f =>
                    progress?.Report(new JobProgress(title, f, Detail(request.Files))));

                var result = await handler
                    .ExecuteAsync(request, inner, cancellationToken)
                    .ConfigureAwait(false);

                Collect(result, outputs, failures, request.Files.Count == 1 ? request.Files[0].Name : null);
            }
            else
            {
                var total = request.Files.Count;
                for (var index = 0; index < total; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var file = request.Files[index];
                    var slotStart = (double)index / total;
                    var slotSize = 1.0 / total;

                    var inner = new Progress<double>(f =>
                        progress?.Report(new JobProgress(
                            title,
                            slotStart + Math.Clamp(f, 0, 1) * slotSize,
                            total == 1 ? file.Name : $"{file.Name}  ({index + 1} of {total})")));

                    progress?.Report(new JobProgress(
                        title,
                        slotStart,
                        total == 1 ? file.Name : $"{file.Name}  ({index + 1} of {total})"));

                    var single = request with { Files = new[] { file } };

                    try
                    {
                        var result = await handler
                            .ExecuteAsync(single, inner, cancellationToken)
                            .ConfigureAwait(false);

                        Collect(result, outputs, failures, file.Name);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{file.Name}: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new BatchResult(outputs, failures, Cancelled: true);
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
        }

        progress?.Report(new JobProgress(title, 1.0));
        return new BatchResult(outputs, failures);
    }

    private static void Collect(
        JobResult result, List<string> outputs, List<string> failures, string? label)
    {
        switch (result.Status)
        {
            case JobStatus.Succeeded:
                outputs.AddRange(result.OutputPaths);
                break;
            case JobStatus.Failed:
            case JobStatus.Skipped:
                var message = result.Message ?? "Failed.";
                failures.Add(label is null ? message : $"{label}: {message}");
                break;
        }
    }

    private static string Detail(IReadOnlyList<SourceFile> files) =>
        files.Count == 1 ? files[0].Name : $"{files.Count} files";
}
