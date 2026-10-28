using Satsuma.Core.Model;

namespace Satsuma.Core.Engine;

/// <summary>
/// One unit of real work. A handler claims the wheel items it can carry out, and the wheel
/// is filtered through <see cref="CanHandle"/>, so an item nothing claims is never drawn.
/// </summary>
public interface IJobHandler
{
    /// <summary>
    /// True when this handler can carry out the request on this machine right now.
    /// Should account for missing external tools, not just the format pair.
    /// </summary>
    bool CanHandle(JobRequest request);

    /// <summary>
    /// True when this request folds the whole selection into one result, like Merge or Join.
    /// Otherwise the runner calls the handler once per file.
    ///
    /// It takes the request because one handler can own both kinds: the video tools are
    /// per-file except for Join.
    /// </summary>
    bool IsAggregate(JobRequest request) => false;

    Task<JobResult> ExecuteAsync(
        JobRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}
