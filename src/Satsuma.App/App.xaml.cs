using System.Windows;
using System.Windows.Threading;
using Satsuma.App.Actions;
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

        DispatcherUnhandledException += OnUnhandledException;

        _settings = AppSettings.Load();
        _runner = new JobRunner();

        _tray = new TrayController(_settings);
        _tray.QuitRequested += (_, _) => Shutdown();
        _tray.ShelfRequested += (_, _) => ShowShelf();

        _wheel = new WheelWindow(_runner, _settings);
        _wheel.Chosen += OnWheelChoice;
        _wheel.Dismissed += (_, _) => SetWheelClosed();

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
            _watcher.Cancelled += (_, _) => CloseWheel();
            _watcher.DragFinished += (_, _) => _closeAfterDrag.Start();
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

        TryShowPreview(e.Args);
    }

    /// <summary>
    /// <c>--preview &lt;wheel&gt; &lt;path&gt;</c> opens a wheel for a file without a drag,
    /// centred on the primary monitor. It exists so the wheel can be looked at and
    /// screenshotted while working on it, since a drag cannot be scripted.
    /// </summary>
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
        var seed = ShellSelection.FromPoint(e.ScreenX, e.ScreenY);

        _watcher.WheelOpen = true;
        _wheel.ShowFor(e.Wheel, seed, e.ScreenX, e.ScreenY);
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

        card.Show();
        card.PositionNearCursor();
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
