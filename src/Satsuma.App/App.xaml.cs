using System.Windows;
using System.Windows.Threading;
using Satsuma.App.Actions;
using Satsuma.App.Diagnostics;
using Satsuma.App.Interop;
using Satsuma.App.Progress;
using Satsuma.App.Settings;
using Satsuma.App.Shelf;
using Satsuma.App.Tray;
using Satsuma.App.Wheel;
using Satsuma.Core.Engine;
using Satsuma.Core.Model;

namespace Satsuma.App;

/// <summary>
/// Wires the pieces together: the input watcher notices a drag, the wheel asks what to do
/// with it, and the job runner does it.
/// </summary>
public partial class App : Application
{
    private const string InstanceMutexName = "Satsuma.SingleInstance.v1";

    private Mutex? _instanceMutex;
    private AppSettings _settings = new();
    private JobRunner _runner = new();
    private TrayController? _tray;
    private DragWatcher? _watcher;
    private WheelWindow? _wheel;
    private FloatShelfWindow? _shelf;

    /// <summary>Set while a drop is being turned into a job, so the wheel is not reopened.</summary>
    private DispatcherTimer? _closeAfterDrag;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // A second copy would install a second set of hooks and show two wheels.
            MessageBox.Show("Satsuma is already running. Look for it in the tray.",
                "Satsuma", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Log.Enable(e.Args);
        Log.Write("starting");

        DispatcherUnhandledException += OnUnhandledException;

        _settings = AppSettings.Load();
        _runner = new JobRunner();

        // Finding ffmpeg walks PATH and the winget install tree, which is disk work that
        // happens once and would otherwise happen inside the first wheel build. Started
        // here so it overlaps the rest of startup instead of delaying a drag.
        Task.Run(() =>
        {
            var found = Core.Tools.FfmpegLocator.IsAvailable;
            Log.Write($"ffmpeg located: {found}");
        });

        // Reading an Explorer selection the first time sets up shell COM, which measured
        // a couple of hundred milliseconds. Done here, on the apartment it needs, so the
        // first wheel is not the one paying for it.
        Task.Run(async () =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Core.Tools.StaExecutor.RunAsync(() => ShellSelection.FromForeground());

            _shellWarm = true;
            Log.Write($"shell COM warmed in {clock.Elapsed.TotalMilliseconds:0}ms");
        });

        _tray = new TrayController(_settings);
        _tray.QuitRequested += (_, _) => Shutdown();
        _tray.ShelfRequested += (_, _) => ShowShelf();

        _wheel = new WheelWindow(_runner, _settings);
        _wheel.Chosen += OnWheelChoice;
        _wheel.Dismissed += (_, _) => SetWheelClosed();

        // Build and render the wheel once now, while nothing is waiting on it. Left to
        // the first real open, this costs about a second of visible lag. Any preview
        // follows it, so what a preview shows is what a real open looks like.
        Dispatcher.BeginInvoke(() =>
        {
            _wheel?.Prewarm();
            TryProbeSelection(e.Args);
            TryShowPreview(e.Args);
        }, DispatcherPriority.Normal);

        _closeAfterDrag = new DispatcherTimer
        {
            // Long enough for an OLE drop to be delivered and handled before the wheel is
            // torn down, since the button-up that ends the drag reaches the hook first.
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _closeAfterDrag.Tick += OnCloseAfterDrag;

        try
        {
            _watcher = new DragWatcher(_settings, Dispatcher);
            _watcher.WheelRequested += OnWheelRequested;
            _watcher.ClickWheelRequested += OnClickWheelRequested;

            // Clicking away from a sticky wheel puts it away, the way any transient
            // surface behaves. A click on the wheel itself is the wheel's own business.
            _watcher.ClickedWhileOpen += (_, click) =>
            {
                if (_wheel?.IsSticky != true) return;
                if (_wheel.HitTestScreen(click.ScreenX, click.ScreenY)) return;

                // A wheel that is open but still has nothing to work on is waiting to be
                // told which file. Clicking one away from the wheel answers that, rather
                // than meaning "go away".
                if (!_wheel.HasFiles)
                {
                    SeedFromSelectionAsync(_wheel, click.SourceWindow, delayMs: 130);
                    return;
                }

                CloseWheel();
            };
            _watcher.Cancelled += (_, _) => CloseWheel();
            _watcher.DragFinished += (_, _) =>
            {
                // A wheel opened by clicking is meant to outlive the button, so the
                // release that ends a drag must not take it down with it.
                if (_wheel?.IsSticky == true) return;
                _closeAfterDrag.Start();
            };
            _watcher.Start();
        }
        catch (Exception ex)
        {
            _tray.Notify("Satsuma could not start watching drags", ex.Message, isError: true);
        }

        if (!Core.Tools.FfmpegLocator.IsAvailable)
        {
            _tray.Notify("Satsuma is running",
                "Images work now. Install ffmpeg for video, audio, WebP and AVIF.");
        }

    }

    /// <summary>
    /// <c>--preview &lt;wheel&gt; &lt;path&gt;</c> opens a wheel for a file without a drag,
    /// centred on the primary monitor. It exists so the wheel can be looked at and
    /// screenshotted while working on it, since a drag cannot be scripted.
    /// </summary>
    /// <summary>
    /// <c>--probe-selection x y</c> reads the Explorer selection at a screen point and
    /// logs what it found. It exists so the shell lookup can be exercised against a real
    /// Explorer window without driving the pointer, since the lookup is the one piece a
    /// click-driven wheel would depend on completely.
    /// </summary>
    private static void TryProbeSelection(IReadOnlyList<string> args)
    {
        var flag = args.ToList().FindIndex(a =>
            a.Equals("--probe-selection", StringComparison.OrdinalIgnoreCase));

        if (flag < 0 || args.Count < flag + 3) return;
        if (!int.TryParse(args[flag + 1], out var x)) return;
        if (!int.TryParse(args[flag + 2], out var y)) return;

        var found = ShellSelection.FromPoint(x, y);

        Log.Write($"probe result: {found.Count} file(s)");
        foreach (var file in found) Log.Write($"  {file.Path}");
    }

    private void TryShowPreview(IReadOnlyList<string> args)
    {
        var flag = args.ToList().FindIndex(a =>
            a.Equals("--preview", StringComparison.OrdinalIgnoreCase));

        if (flag < 0 || _wheel is null) return;

        // --selection density|depth, so the two selection treatments can be compared.
        var styleFlag = args.ToList().FindIndex(a =>
            a.Equals("--selection", StringComparison.OrdinalIgnoreCase));

        if (styleFlag >= 0 && args.Count > styleFlag + 1
            && Enum.TryParse<SelectionStyle>(args[styleFlag + 1], true, out var style))
        {
            _settings.SelectionStyle = style;
        }

        var kind = WheelKind.Formats;
        var pathIndex = flag + 1;

        if (args.Count > flag + 1 && Enum.TryParse<WheelKind>(args[flag + 1], true, out var parsed))
        {
            kind = parsed;
            pathIndex = flag + 2;
        }

        var seed = args.Count > pathIndex && (File.Exists(args[pathIndex]) || Directory.Exists(args[pathIndex]))
            ? new[] { new SourceFile(args[pathIndex]) }
            : Array.Empty<SourceFile>();

        if (_watcher is not null) _watcher.WheelOpen = true;

        // Physical pixels, since that is what ShowFor positions in.
        _wheel.ShowFor(kind, seed,
            NativeMethods.GetSystemMetrics(0) / 2,
            NativeMethods.GetSystemMetrics(1) / 2);
    }

    private void OnWheelRequested(object? sender, DragModifierEvent e)
    {
        if (_wheel is null || _watcher is null) return;

        _closeAfterDrag?.Stop();

        // A guess at the selection, so the wheel opens with the right slices already on
        // it. The real paths replace this as soon as the drag reaches the wheel.
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // Seeded before showing where that is cheap, so the wheel opens with the right
        // slices rather than appearing empty and filling in a moment later. Cold, it is
        // cross-process COM into explorer.exe and far too slow to hold the wheel back.
        var upFront = _shellWarm ? ReadSelection(e.SourceWindow) : Array.Empty<SourceFile>();

        _watcher.WheelOpen = true;
        _wheel.ShowFor(e.Wheel, upFront, e.ScreenX, e.ScreenY);

        Log.Write(
            $"showing {e.Wheel} wheel in {clock.Elapsed.TotalMilliseconds:0}ms, " +
            $"seed={upFront.Count}");

        // The dropped paths are what finally decide, and they arrive on the way in.
        if (upFront.Count == 0) SeedFromSelectionAsync(_wheel, e.SourceWindow, delayMs: 0);
    }

    /// <summary>
    /// A bound key was held and a file clicked. The wheel goes up where the click landed
    /// and stays there, driven by clicking rather than by the drag.
    /// </summary>
    private void OnClickWheelRequested(object? sender, DragModifierEvent e)
    {
        if (_wheel is null || _watcher is null) return;

        _closeAfterDrag?.Stop();
        _watcher.WheelOpen = true;

        // There is no drag data here, so the Explorer selection is the only way to learn
        // which file this is about. Read from the window the event carried, not from a
        // screen point, since the wheel will be covering that point.
        //
        // Read before showing when it can be: once shell COM is warm this takes a few
        // milliseconds, and doing it first means the wheel opens already holding the
        // right slices. Showing an empty wheel and filling it a moment later is what
        // produced a visible flicker on every open.
        var upFront = _shellWarm ? ReadSelection(e.SourceWindow) : Array.Empty<SourceFile>();

        _wheel.ShowForClick(e.Wheel, upFront, e.ScreenX, e.ScreenY);

        // Cold, or nothing selected yet because a click is still being processed.
        if (upFront.Count == 0) SeedFromSelectionAsync(_wheel, e.SourceWindow, delayMs: 130);
    }

    /// <summary>
    /// Reads a window's selection away from the UI thread and gives it to the wheel if it
    /// still has nothing. The delay lets a click register as a selection first.
    /// </summary>
    /// <summary>Set once reading a selection no longer costs most of a second.</summary>
    private volatile bool _shellWarm;

    /// <summary>
    /// Reads a selection on this thread. Safe because the UI thread is an STA, which is
    /// what shell COM wants, and because it is only called once the first read has
    /// already paid for the setup.
    /// </summary>
    private static IReadOnlyList<SourceFile> ReadSelection(IntPtr sourceWindow)
    {
        try
        {
            return sourceWindow == IntPtr.Zero
                ? ShellSelection.FromForeground()
                : ShellSelection.FromWindow(sourceWindow);
        }
        catch (Exception ex)
        {
            Log.Write($"selection read failed: {ex.Message}");
            return Array.Empty<SourceFile>();
        }
    }

    private void SeedFromSelectionAsync(WheelWindow wheel, IntPtr sourceWindow, int delayMs)
    {
        Task.Run(async () =>
        {
            if (delayMs > 0) await Task.Delay(delayMs).ConfigureAwait(false);

            // Shell COM is apartment-threaded, and thread-pool threads are not.
            var files = await Core.Tools.StaExecutor
                .RunAsync(() => sourceWindow == IntPtr.Zero
                    ? ShellSelection.FromForeground()
                    : ShellSelection.FromWindow(sourceWindow))
                .ConfigureAwait(false);

            if (files.Count == 0) return;
            await Dispatcher.BeginInvoke(() => wheel.SeedLate(files));
        });
    }

    private void OnCloseAfterDrag(object? sender, EventArgs e)
    {
        _closeAfterDrag?.Stop();
        CloseWheel();
    }

    private void CloseWheel()
    {
        _closeAfterDrag?.Stop();
        _wheel?.Dismiss();
        SetWheelClosed();
    }

    private void SetWheelClosed()
    {
        if (_watcher is not null) _watcher.WheelOpen = false;
    }

    private async void OnWheelChoice(object? sender, WheelChoice choice)
    {
        SetWheelClosed();
        _closeAfterDrag?.Stop();

        if (choice.Item.Kind == WheelKind.Actions)
        {
            RunAction(choice);
            return;
        }

        await RunJobAsync(choice);
    }

    private void RunAction(WheelChoice choice)
    {
        var outcome = QuickActions.Run(choice.Item.Id, choice.Files, files => ShowShelf(files));

        if (!outcome.Succeeded && outcome.Message != "Cancelled")
        {
            _tray?.Notify(choice.Item.Label, outcome.Message, isError: true);
        }
    }

    private async Task RunJobAsync(WheelChoice choice)
    {
        var card = new ProgressWindow();
        using var cancellation = new CancellationTokenSource();
        card.Attach(cancellation);

        card.ShowNearCursor();
        card.Report(new JobProgress(choice.Item.Label, 0,
            choice.Files.Count == 1 ? choice.Files[0].Name : $"{choice.Files.Count} files"));

        var request = new JobRequest(choice.Item, choice.Files, choice.Category);
        var progress = new Progress<JobProgress>(card.Report);

        BatchResult result;
        try
        {
            result = await _runner.RunAsync(request, progress, cancellation.Token);
        }
        catch (Exception ex)
        {
            result = new BatchResult(Array.Empty<string>(), new[] { ex.Message });
        }

        card.Complete(result, choice.Item.Label);

        if (_settings.RevealResults && result.AnySucceeded)
        {
            foreach (var group in result.Outputs
                         .GroupBy(p => Path.GetDirectoryName(p) ?? string.Empty,
                             StringComparer.OrdinalIgnoreCase))
            {
                if (group.Key.Length > 0) QuickActions.SelectInExplorer(group.Key, group.ToList());
            }
        }
    }

    private void ShowShelf(IReadOnlyList<SourceFile>? files = null)
    {
        _shelf ??= new FloatShelfWindow();

        if (files is not null) _shelf.Add(files);
        else _shelf.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A failure in one conversion should never take the tray app down with it.
        Log.Write($"unhandled: {e.Exception}");
        _tray?.Notify("Something went wrong", e.Exception.Message, isError: true);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watcher?.Dispose();
        _tray?.Dispose();

        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch (ApplicationException) { }
            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}
