namespace Satsuma.Core.Tools;

/// <summary>
/// Runs a piece of work on a single-threaded-apartment thread.
///
/// Windows Imaging Component is an apartment-threaded COM surface, and WPF's imaging
/// classes inherit that: decoding or encoding from a multi-threaded apartment ranges from
/// silently failing to taking the process down. Thread-pool threads are MTA, which is
/// exactly where conversions land after any <c>await</c>, so every WIC call goes through
/// here rather than relying on which thread happened to resume.
///
/// The work is always handed to a fresh STA thread rather than run inline on an STA caller,
/// so a large image never blocks the UI thread that asked for it.
/// </summary>
public static class StaExecutor
{
    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (cancellationToken.IsCancellationRequested)
        {
            completion.SetCanceled(cancellationToken);
            return completion.Task;
        }

        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Satsuma WIC"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task;
    }

    public static Task RunAsync(Action work, CancellationToken cancellationToken = default) =>
        RunAsync<object?>(() => { work(); return null; }, cancellationToken);
}
