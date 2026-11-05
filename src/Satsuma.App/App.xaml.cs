using System.Windows;
using System.Windows.Threading;
using Satsuma.App.Actions;
using Satsuma.App.Diagnostics;
using Satsuma.App.Glass;
using Satsuma.App.Hint;
using Satsuma.App.Interop;
using Satsuma.App.Progress;
using Satsuma.App.Prompt;
using Satsuma.App.Settings;
using Satsuma.App.Shelf;
using Satsuma.App.Tray;
using Satsuma.App.Wheel;
using Satsuma.Core.Engine;
using Satsuma.Core.Model;
using Satsuma.Core.Util;

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
    private CursorHintWindow? _hint;

    /// <summary>
    /// The Explorer window the current gesture came from.
    ///
    /// Kept because the wheel is topmost: once anything of Satsuma's is on screen,
    /// asking what lies under a screen point answers "Satsuma".
    /// </summary>
    private IntPtr _lastSourceWindow;

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

        // Satsuma is idle nearly all the time, and Windows trims an idle process's
        // working set, so the pages holding the wheel and its compiled code go out to
        // disk. The first drag after a quiet spell then faults them all back in, which is
        // exactly the reported symptom: slow once, fine every time after.
        var pinned = NativeMethods.KeepWorkingSetResident(
            minimumBytes: 96L * 1024 * 1024,
            maximumBytes: 512L * 1024 * 1024);

        // Keeps the collector from handing pages back between uses for the same reason.
        System.Runtime.GCSettings.LatencyMode =
            System.Runtime.GCLatencyMode.SustainedLowLatency;

        Log.Write($"working set floor set: {pinned}");

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
            TryShelf(e.Args);
            TryHint(e.Args);
            TryHighlight(e.Args);
            TryDuplicate(e.Args);
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
                // told which file, and a click on a file answers that rather than
                // meaning "go away". Only a click on a file, though: this used to
                // swallow every outside click, so an empty wheel over a window with no
                // selection to read could not be dismissed by clicking at all, and Esc
                // or a right-click were the only ways out of it.
                if (!_wheel.HasFiles && DragWatcher.IsShellWindow(click.SourceWindow))
                {
                    SeedFromSelectionAsync(_wheel, click.SourceWindow, delayMs: 130);
                    return;
                }

                CloseWheel();
            };
            // Space and Alt say where the results go. Shown live, because the whole
            // point of holding a key is to know what it is doing while you hold it.
            _watcher.ModifiersChanged += (_, _) =>
                _wheel?.ShowDestination(AskedForDestination());

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

    /// <summary>
    /// <c>--shelf &lt;path&gt;</c> parks a file on the shelf, closes it and opens it again.
    ///
    /// It exists because the shelf cannot be driven from a test and the thing that kept
    /// going wrong with it was the difference between the first time it is shown and
    /// every time after. Two reveals in a row put both in the log side by side.
    /// </summary>
    private async void TryShelf(IReadOnlyList<string> args)
    {
        var flag = args.ToList().FindIndex(a =>
            a.Equals("--shelf", StringComparison.OrdinalIgnoreCase));

        if (flag < 0 || args.Count <= flag + 1) return;

        var path = args[flag + 1];
        if (!File.Exists(path) && !Directory.Exists(path)) return;

        Log.Write("--shelf: first reveal");
        ShowShelf(new[] { new SourceFile(path) });

        await Task.Delay(1000);

        // Adding to a shelf that is already open, which is the case that used to send
        // the whole window off the screen and back.
        var second = args.Count > flag + 2 ? args[flag + 2] : null;

        if (second is not null && (File.Exists(second) || Directory.Exists(second)))
        {
            Log.Write("--shelf: adding while open");
            ShowShelf(new[] { new SourceFile(second) });

            await Task.Delay(1000);
        }

        Log.Write("--shelf: closing");
        _shelf?.Hide();

        await Task.Delay(400);

        Log.Write("--shelf: second reveal");
        _shelf?.Reveal();

        await Task.Delay(1000);

        // The sequence that broke it: emptied by hand, closed, then filled again. The
        // window kept the height it had while it was empty and clipped the chip.
        Log.Write("--shelf: emptied");
        _shelf?.Clear();

        await Task.Delay(600);

        Log.Write("--shelf: closing while empty");
        _shelf?.Hide();

        await Task.Delay(400);

        Log.Write("--shelf: filled again");
        ShowShelf(new[] { new SourceFile(path) });
    }

    /// <summary>
    /// <c>--hint</c> anchors the label and changes its caption a few times.
    ///
    /// The label cannot be clicked from a test, and the bug worth catching is that it
    /// moves when it should not: an anchored label that re-places itself on every
    /// caption change slides out from under the finger pressing its tick. The log says
    /// where it went each time.
    /// </summary>
    private async void TryHint(IReadOnlyList<string> args)
    {
        if (!args.Any(a => a.Equals("--hint", StringComparison.OrdinalIgnoreCase))) return;

        _hint ??= new CursorHintWindow();

        Log.Write("--hint: anchoring");
        _hint.Anchor("Select files", ModeGlyphs.Page);

        foreach (var caption in new[] { "2 files", "3 files", "Select files", "4 files" })
        {
            await Task.Delay(500);

            Log.Write($"--hint: saying '{caption}'");
            _hint.Say(caption);
        }

        // And the marks, which change while the label is up because Space and Alt can
        // be taken up and let go in the middle of picking. Same rule as the captions:
        // the label must not move, or the tick walks out from under the finger.
        foreach (var (name, glyph) in new (string, System.Windows.Media.Geometry)[]
                 {
                     ("folder", ModeGlyphs.Folder),
                     ("replace", ModeGlyphs.Replace),
                     ("page", ModeGlyphs.Page)
                 })
        {
            await Task.Delay(500);

            Log.Write($"--hint: marking '{name}'");
            _hint.Mark(glyph);
        }

        await Task.Delay(800);

        Log.Write("--hint: done");
        _hint.Dismiss();
    }

    /// <summary>
    /// --highlight &lt;folder&gt;: opens that folder, selects the first two files in it from
    /// outside, then reads the selection back.
    ///
    /// Writing a selection is shell COM asking Explorer to do something rather than
    /// asking it a question, and the failure mode is silence: the call returns, nothing
    /// is selected, and the only sign is that merging quietly picks up fewer files than
    /// were clicked. Reading it back afterwards is the one answer that cannot be
    /// mistaken -- if the files come back, Explorer really did select them.
    /// </summary>
    private async void TryHighlight(IReadOnlyList<string> args)
    {
        var flag = args.ToList().FindIndex(a =>
            a.Equals("--highlight", StringComparison.OrdinalIgnoreCase));

        if (flag < 0 || args.Count <= flag + 1) return;

        var folder = args[flag + 1];
        if (!Directory.Exists(folder)) { Log.Write($"--highlight: no {folder}"); return; }

        var files = Directory.GetFiles(folder).Take(2).ToList();
        if (files.Count == 0) { Log.Write("--highlight: folder is empty"); return; }

        System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\"");
        await Task.Delay(2500);

        var window = IntPtr.Zero;
        var leaf = Path.GetFileName(folder.TrimEnd('\\'));

        NativeMethods.EnumWindows((handle, _) =>
        {
            var text = new System.Text.StringBuilder(512);
            NativeMethods.GetWindowText(handle, text, text.Capacity);
            var title = text.ToString();

            if (NativeMethods.ClassNameOf(handle) == "CabinetWClass"
                && title.Contains(leaf, StringComparison.OrdinalIgnoreCase))
            {
                window = handle;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        if (window == IntPtr.Zero) { Log.Write("--highlight: no window found"); return; }

        Log.Write($"--highlight: window 0x{window.ToInt64():X}, selecting {files.Count}");
        foreach (var file in files) Log.Write($"  asked: {file}");

        var wrote = await Core.Tools.StaExecutor.RunAsync(
            () => ShellSelection.Highlight(window, files));

        Log.Write($"--highlight: wrote={wrote}");

        await Task.Delay(600);

        var back = await Core.Tools.StaExecutor.RunAsync(
            () => ShellSelection.FromWindow(window));

        Log.Write($"--highlight: read back {back.Count} file(s)");
        foreach (var file in back) Log.Write($"  got: {file.Path}");

        // Now a different set, to prove the old one goes away rather than being added
        // to. This is the half that matters when a file is clicked back out of a merge:
        // the set shrinks, and the view has to shrink with it.
        var replacement = Directory.GetFiles(folder).Skip(2).Take(1).ToList();
        foreach (var file in replacement) Log.Write($"  asked instead: {file}");

        await Core.Tools.StaExecutor.RunAsync(
            () => ShellSelection.Highlight(window, replacement));

        await Task.Delay(600);

        var after = await Core.Tools.StaExecutor.RunAsync(
            () => ShellSelection.FromWindow(window));

        Log.Write($"--highlight: after replacing, {after.Count} file(s)");
        foreach (var file in after) Log.Write($"  got: {file.Path}");
        Log.Write("--highlight: done");
    }

    /// <summary>
    /// --dupe &lt;path&gt;...: duplicates each path and says what landed.
    ///
    /// The naming is covered by the self-test, which cannot reach this project; what is
    /// left to check is that a real file and a real folder both come out the other side
    /// whole, and that a second run steps aside rather than writing over the first.
    /// </summary>
    private void TryDuplicate(IReadOnlyList<string> args)
    {
        var flag = args.ToList().FindIndex(a =>
            a.Equals("--dupe", StringComparison.OrdinalIgnoreCase));

        if (flag < 0 || args.Count <= flag + 1) return;

        var paths = args.Skip(flag + 1)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Select(path => new SourceFile(path))
            .ToList();

        if (paths.Count == 0) { Log.Write("--dupe: nothing there"); return; }

        foreach (var file in paths)
        {
            Log.Write($"--dupe: asked for {file.Name} (directory={file.IsDirectory})");
        }

        var outcome = Actions.QuickActions.Run("act.dupe", paths, _ => { });

        Log.Write($"--dupe: {outcome.Succeeded} '{outcome.Message}'");

        // What is actually on disk now, which is the only answer that counts.
        foreach (var directory in paths
                     .Select(file => Path.GetDirectoryName(
                         file.Path.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty)
                     .Where(path => path.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var entry in Directory.GetFileSystemEntries(directory))
            {
                var what = Directory.Exists(entry) ? "dir " : "file";
                var size = Directory.Exists(entry)
                    ? Directory.GetFileSystemEntries(entry, "*", SearchOption.AllDirectories).Length
                    : new FileInfo(entry).Length;

                Log.Write($"--dupe:   {what} {Path.GetFileName(entry)} ({size})");
            }
        }

        Log.Write("--dupe: done");
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

        _lastSourceWindow = e.SourceWindow;

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
        _lastSourceWindow = e.SourceWindow;

        // There is no drag data here, so the Explorer selection is the only way to learn
        // which file this is about. Read from the window the event carried, not from a
        // screen point, since the wheel will be covering that point.
        //
        // Read before showing when it can be: once shell COM is warm this takes a few
        // milliseconds, and doing it first means the wheel opens already holding the
        // right slices. Showing an empty wheel and filling it a moment later is what
        // produced a visible flicker on every open.
        var upFront = _shellWarm ? ReadSelection(e.SourceWindow) : Array.Empty<SourceFile>();

        _wheel.ShowForClick(e.Wheel, upFront, e.ScreenX, e.ScreenY, e.ButtonDown);

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

    /// <summary>
    /// Where the user is asking for the results, judged by what is still held down.
    ///
    /// Read at the moment it matters rather than remembered, which is what makes a tap
    /// useless: a key let go before the end of the gesture is simply not down when this
    /// is asked. Alt wins a tie, since it is the more specific of the two.
    /// </summary>
    private Destination AskedForDestination()
    {
        if (_watcher is null) return Destination.Beside;
        if (_watcher.AltHeld) return Destination.InPlace;
        return _watcher.SpaceHeld ? Destination.NewFolder : Destination.Beside;
    }

    /// <summary>What ended a wait, and so what happens next.</summary>
    private enum WaitEnd
    {
        /// <summary>Answered.</summary>
        Done,

        /// <summary>Escape, or nothing was chosen. Nothing happens.</summary>
        Cancelled,

        /// <summary>The back key: undo the choice and put the wheel up again.</summary>
        Back
    }

    private async void OnWheelChoice(object? sender, WheelChoice choice)
    {
        // Sampled first, before anything can await and let the keys come up.
        var destination = AskedForDestination();
        var holding = _watcher?.WheelKeyHeld == true;
        var where = NativeMethods.GetCursorPos(out var cursor)
            ? cursor
            : new NativeMethods.POINT(0, 0);

        // Kept for the back key, which has to put the wheel back the way it was.
        var startedWith = choice.Files;
        var source = _lastSourceWindow;

        SetWheelClosed();
        _closeAfterDrag?.Stop();

        if (choice.Item.Kind == WheelKind.Actions)
        {
            RunAction(choice);
            return;
        }

        // A slice that works on several files but was given one asks for the rest by
        // letting them be selected. Before the typed value, because which files there
        // are can change what a typed value would mean.
        if (choice.Item.Gathers && choice.Files.Count < 2)
        {
            var (end, gathered, going) =
                await GatherMoreAsync(choice.Files, holding, source);

            if (end == WaitEnd.Back)
            {
                ReopenWheel(choice.Item.Kind, startedWith, where);
                return;
            }

            if (end != WaitEnd.Done) return;

            // Picking the files is the gesture here, so what is being held when that
            // ends is what says where the result goes. The reading taken at the drop was
            // of a hand that was still holding a mouse button and a wheel key and had
            // not chosen anything yet.
            destination = going;

            choice = choice with { Files = gathered };
        }

        // A slice that needs a value asks for it now. The wheel is gone by this point,
        // so a window can take focus and be typed into.
        if (choice.Item.Input is { } request)
        {
            var answer = await AskForAsync(request, choice);
            if (answer is null) return;   // changed their mind

            var options = new Dictionary<string, string>(choice.Item.Options)
            {
                [WheelItem.CustomOption] = answer
            };

            choice = choice with { Item = choice.Item with { Options = options } };
        }

        string? folder = null;

        // Space asked for somewhere else. Which somewhere else is still open: select a
        // folder before letting go, or let go having selected none and have one made.
        if (destination == Destination.NewFolder)
        {
            var (end, picked) = await WaitForFolderAsync(startedWith, source);

            if (end == WaitEnd.Back)
            {
                ReopenWheel(choice.Item.Kind, startedWith, where);
                return;
            }

            if (end != WaitEnd.Done) return;

            if (picked is not null)
            {
                destination = Destination.ChosenFolder;
                folder = picked;
            }
        }

        Log.Write($"destination: {destination}{(folder is null ? "" : " -> " + folder)}");

        await RunJobAsync(choice, destination, folder);
    }

    /// <summary>
    /// Puts the wheel back up after the back key, holding what it held before.
    ///
    /// Shown the click-driven way rather than the drag way, because by this point the
    /// drag is long over: the drop is what got us here. So it stays up and is clicked.
    /// </summary>
    private void ReopenWheel(
        WheelKind kind, IReadOnlyList<SourceFile> files, NativeMethods.POINT at)
    {
        if (_wheel is null || _watcher is null) return;

        Log.Write($"back to the {kind} wheel with {files.Count} file(s)");

        _watcher.WheelOpen = true;
        _wheel.ShowForClick(kind, files, at.X, at.Y);
    }

    /// <summary>
    /// Collects the other files by letting them be selected where they already are.
    ///
    /// Explorer does the selecting. A click picks one, Ctrl and Shift behave the way
    /// they do everywhere else, and whatever is selected when the gesture ends is the
    /// answer. Satsuma used to keep its own tally of clicked files, which meant a plain
    /// click added instead of replacing and the selection on screen stopped agreeing
    /// with the one being used -- the worst kind of disagreement, because the screen is
    /// the only one of the two you can see.
    ///
    /// What ends it depends on how it began. With the wheel key still down there is
    /// nothing to press: letting go ends this gesture the way letting go of the mouse
    /// ended the one before it. With no key down -- a wheel opened by clicking -- there
    /// is no such moment, so the label grows a tick and Enter works too.
    /// </summary>
    private Task<(WaitEnd End, IReadOnlyList<SourceFile> Files, Destination Where)>
        GatherMoreAsync(IReadOnlyList<SourceFile> starting, bool keyHeld, IntPtr source)
    {
        var watcher = _watcher;

        if (watcher is null)
        {
            return Task.FromResult((WaitEnd.Cancelled, starting, Destination.Beside));
        }

        var answered =
            new TaskCompletionSource<(WaitEnd, IReadOnlyList<SourceFile>, Destination)>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        // Only more of the same kind. Merging a PDF with a spreadsheet is not a thing,
        // and a stray click on the wrong file should cost nothing.
        var wanted = starting.Count > 0 ? starting[0].Extension : string.Empty;
        var window = source;

        EventHandler? onReleased = null;
        EventHandler? onConfirmed = null;
        EventHandler? onBack = null;
        EventHandler? onCancelled = null;
        EventHandler? onAccepted = null;
        EventHandler? onModifiers = null;
        EventHandler<DragModifierEvent>? onClicked = null;
        DispatcherTimer? giveUp = null;

        // The files picked so far, in the order they were picked -- which Explorer does
        // not report, since a selection comes back in whatever order the view is sorted
        // in. Merging is one of the few jobs where the order is the whole point.
        //
        // This list is what gets merged, and after every click it is pushed back into
        // the view, so the highlight on screen says the same thing. That is what makes
        // a plain click add a file instead of replacing the lot: Explorer still replaces
        // its selection, and Satsuma puts the rest of the set straight back. The file
        // the gesture started on stays lit the whole time, because it is in the set.
        var order = new List<string>();

        // Rises with every click, so a read that comes back after a newer one has
        // already been applied can tell that it is out of date and keep quiet.
        var latest = 0;
        var subjects = new HashSet<string>(
            starting.Select(file => file.Path), StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<SourceFile> Picked() =>
            starting.Concat(order.Select(path => new SourceFile(path))).ToList();

        // How many files are going in, counting the ones the gesture started with.
        string Caption(int extra) =>
            extra > 0 ? $"{starting.Count + extra} files" : "Select files";

        // The mark the label carries: whichever destination is being held, or the sheet
        // of paper that just means "which files" when nothing is.
        System.Windows.Media.Geometry Mark() =>
            ModeGlyphs.For(AskedForDestination()) ?? ModeGlyphs.Page;

        // What in a selection could join the merge: the right kind of file, and not one
        // of the files the gesture began with -- those are in already and cannot be
        // dropped, so a click on one is not a click on anything.
        List<string> Mergeable(IReadOnlyList<SourceFile> selected) => selected
            .Where(file => !file.IsDirectory
                           && !subjects.Contains(file.Path)
                           && string.Equals(file.Extension, wanted,
                               StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Path)
            .ToList();

        // Folds one click's worth of selection into the set.
        void Absorb(IReadOnlyList<SourceFile> selected, bool extending)
        {
            var now = Mergeable(selected);

            // Ctrl, Shift, or a band drawn round several files: the user asked for
            // Explorer's own behaviour and what it arrived at is the answer.
            if (extending || now.Count > 1)
            {
                order.RemoveAll(path => !now.Contains(path, StringComparer.OrdinalIgnoreCase));

                foreach (var path in now)
                {
                    if (!order.Contains(path, StringComparer.OrdinalIgnoreCase)) order.Add(path);
                }

                return;
            }

            // One file, plainly clicked: in if it was out, out if it was in. Explorer
            // replaced its selection with it; keeping the rest of the set is what turns
            // that into adding one, and clicking a chosen file again is how it comes
            // back out.
            if (now.Count == 1)
            {
                var path = now[0];
                var held = order.FindIndex(
                    picked => string.Equals(picked, path, StringComparison.OrdinalIgnoreCase));

                if (held >= 0) order.RemoveAt(held);
                else order.Add(path);

                return;
            }

            // Nothing mergeable. An empty selection is the background being clicked,
            // which is how to start the set over. Something selected that cannot join --
            // a folder, the wrong kind of file, the file it all started from -- leaves
            // the set alone, because a stray click should cost nothing.
            if (selected.Count == 0) order.Clear();
        }

        // Puts the set back on screen, so the highlight and the merge agree. Kept off
        // the UI thread's critical path: this is shell COM and can be slow.
        void Highlight()
        {
            var view = window;
            var paths = starting.Select(file => file.Path).Concat(order).ToList();

            _ = Core.Tools.StaExecutor.RunAsync(
                () => ShellSelection.Highlight(view, paths));
        }

        void Stop()
        {
            watcher.Waiting = false;
            watcher.WheelKeyReleased -= onReleased;
            watcher.ConfirmRequested -= onConfirmed;
            watcher.BackRequested -= onBack;
            watcher.Cancelled -= onCancelled;
            watcher.ClickedWhileWaiting -= onClicked;
            watcher.ModifiersChanged -= onModifiers;

            giveUp?.Stop();

            if (_hint is not null) _hint.Accepted -= onAccepted;
            _hint?.Dismiss();
        }

        void Leave(WaitEnd end)
        {
            Stop();
            answered.TrySetResult((end, starting, Destination.Beside));
        }

        void Take()
        {
            // Where the results go, read here because here is the moment it means
            // something. For everything else that moment is the drop; for a merge the
            // drop is only where the picking starts, and Space or Alt held through the
            // picking is what the user let go of last.
            var going = AskedForDestination();

            // Unwired first, so a second trigger arriving during the wait cannot start
            // another one.
            Stop();

            Task.Run(async () =>
            {
                // Longer than a click's own read, so a file clicked just as the key came
                // up is in the set before the set is used. The set is the answer by now;
                // reading Explorer here instead would see one plain click's worth of
                // selection and mistake that for the whole pick.
                await Task.Delay(260).ConfigureAwait(false);

                await Dispatcher.BeginInvoke(() =>
                {
                    var files = Picked();

                    Log.Write(
                        $"gathered {files.Count} file(s) for {wanted}, going {going}");

                    // Nothing picked: there is no merge to do, and one file is not
                    // something that can be merged anyway.
                    answered.TrySetResult(files.Count > starting.Count
                        ? (WaitEnd.Done, files, going)
                        : (WaitEnd.Cancelled, starting, going));
                });
            });
        }

        onReleased = (_, _) =>
        {
            // Letting go of the wheel key is what finishes a merge that was begun by
            // dragging -- once there is something to finish.
            //
            // Nothing picked and a pointer that has moved since the picking began means
            // the user could have clicked a file and did not: a change of mind, and
            // letting go means what it says.
            if (order.Count > 0 || !watcher.PointerCouldNotMove)
            {
                Take();
                return;
            }

            // Nothing picked, and the pointer has not moved a pixel since the picking
            // began -- so it was never free to reach a second file. Windows suppresses
            // precision-touchpad movement for as long as any key is held, and letting
            // go is the only way to get the pointer back. Ending here would make merging
            // by touchpad impossible, so it hands over to the clickable form instead --
            // the same handover that picking a folder already does.
            watcher.WheelKeyReleased -= onReleased;

            Log.Write("wheel key let go, pointer never free: handing over to the tick");

            _hint?.Anchor(Caption(0), Mark());
        };
        onConfirmed = (_, _) => Take();
        onAccepted = (_, _) => Take();
        onBack = (_, _) => Leave(WaitEnd.Back);
        onCancelled = (_, _) => Leave(WaitEnd.Cancelled);

        onClicked = (_, click) =>
        {
            if (click.SourceWindow != IntPtr.Zero) window = click.SourceWindow;

            // The press. Bring the label along before anything is read, so it is beside
            // the file being clicked while the finger is still on it rather than a beat
            // later. Nothing is decided here -- a press can be the start of a band.
            if (click.ButtonDown)
            {
                _hint?.Reanchor(Caption(order.Count));
                return;
            }

            var reading = window;
            var ticket = ++latest;

            Task.Run(async () =>
            {
                // Long enough for the click to have become a selection.
                await Task.Delay(140).ConfigureAwait(false);

                var selected = await Core.Tools.StaExecutor
                    .RunAsync(() => ShellSelection.FromWindow(reading))
                    .ConfigureAwait(false);

                await Dispatcher.BeginInvoke(() =>
                {
                    // Each click starts its own delayed read, so a quick second click
                    // can have its answer overtaken by the first one's -- which then
                    // rewrites the order from a selection that is already history.
                    if (ticket != latest) return;

                    Absorb(selected, click.Extending);

                    // The set, back on screen. Without this the user is picking files
                    // against a highlight that disagrees with what will be merged, and
                    // the highlight is the only one of the two they can see.
                    Highlight();

                    _hint?.Say(Caption(order.Count));
                });
            });
        };

        // Space and Alt can be taken up and let go in the middle of picking, so the mark
        // on the label follows them rather than being decided once at the start.
        onModifiers = (_, _) => _hint?.Mark(Mark());

        watcher.Waiting = true;
        watcher.ModifiersChanged += onModifiers;
        watcher.WheelKeyReleased += onReleased;
        watcher.ConfirmRequested += onConfirmed;
        watcher.BackRequested += onBack;
        watcher.Cancelled += onCancelled;
        watcher.ClickedWhileWaiting += onClicked;

        // Escape and the back key are both available here, so this is not the only way
        // out. It is for the key-up that never arrives, which would otherwise leave
        // every later click in Explorer answering a question nobody is still asking.
        giveUp = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
        giveUp.Tick += (_, _) => Leave(WaitEnd.Cancelled);
        giveUp.Start();

        _hint ??= new CursorHintWindow();
        _hint.Accepted += onAccepted;

        if (keyHeld) _hint.Follow(Caption(0), Mark());
        else _hint.Anchor(Caption(0), Mark());

        // Light up the file it all started from. The drag that got here left Explorer's
        // selection wherever the drop put it, and the first thing the user needs to see
        // is that the file they dragged is already in.
        Highlight();

        return answered.Task;
    }
    /// <summary>
    /// Waits for the answer to "which folder".
    ///
    /// Space being down at the drop is what starts this, and used to be what finished
    /// it too: let go and Satsuma read whatever was selected. That cannot work on a
    /// laptop. Windows suppresses precision-touchpad movement while any key is held --
    /// accidental activation prevention, on by default -- so the pointer is frozen for
    /// exactly as long as Space is down, and the one thing the user is being asked to
    /// do is move it to a folder. Swallowing the key in the hook does not help; the
    /// suppression is keyed off the keyboard, not off who ends up handling it.
    ///
    /// So letting go hands over rather than finishing. If a folder is already selected
    /// -- which is the mouse user, who could point at one -- that is the answer and
    /// nothing changes for them. If not, the label stops following the pointer, grows a
    /// tick, and waits: now the pointer moves, a folder can be clicked, and Enter or
    /// the tick confirms. Esc cancels and the back key returns to the wheel.
    /// </summary>
    private Task<(WaitEnd End, string? Folder)> WaitForFolderAsync(
        IReadOnlyList<SourceFile> starting, IntPtr source)
    {
        var watcher = _watcher;

        if (watcher is null || !watcher.SpaceHeld)
        {
            // Let go between the drop and here. That is the answer: make one.
            return Task.FromResult<(WaitEnd, string?)>((WaitEnd.Done, null));
        }

        var answered = new TaskCompletionSource<(WaitEnd, string?)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // The files being worked on are usually still selected, and one of them can be a
        // folder -- zipping one, say. That is not a destination, it is the subject.
        var subjects = new HashSet<string>(
            starting.Select(file => file.Path), StringComparer.OrdinalIgnoreCase);

        var window = source;
        var handedOver = false;

        EventHandler? onReleased = null;
        EventHandler? onConfirmed = null;
        EventHandler? onAccepted = null;
        EventHandler? onBack = null;
        EventHandler? onCancelled = null;
        EventHandler<DragModifierEvent>? onClicked = null;
        DispatcherTimer? giveUp = null;

        string? FolderIn(IReadOnlyList<SourceFile> selected) =>
            selected.FirstOrDefault(
                file => file.IsDirectory && !subjects.Contains(file.Path))?.Path;

        void Stop()
        {
            watcher.Waiting = false;
            watcher.SpaceReleased -= onReleased;
            watcher.ConfirmRequested -= onConfirmed;
            watcher.BackRequested -= onBack;
            watcher.Cancelled -= onCancelled;
            watcher.ClickedWhileWaiting -= onClicked;

            if (_hint is not null) _hint.Accepted -= onAccepted;

            giveUp?.Stop();
            _hint?.Dismiss();
        }

        // Reads what is selected now and finishes with it, folder or not.
        void Finish()
        {
            Stop();

            Task.Run(async () =>
            {
                // Long enough for a last click to have become a selection.
                await Task.Delay(140).ConfigureAwait(false);

                var selected = await Core.Tools.StaExecutor
                    .RunAsync(() => ShellSelection.FromWindow(window))
                    .ConfigureAwait(false);

                await Dispatcher.BeginInvoke(() =>
                    answered.TrySetResult((WaitEnd.Done, FolderIn(selected))));
            });
        }

        onReleased = (_, _) =>
        {
            if (handedOver) return;

            Task.Run(async () =>
            {
                await Task.Delay(140).ConfigureAwait(false);

                var selected = await Core.Tools.StaExecutor
                    .RunAsync(() => ShellSelection.FromWindow(window))
                    .ConfigureAwait(false);

                await Dispatcher.BeginInvoke(() =>
                {
                    if (handedOver || !watcher.Waiting) return;

                    var folder = FolderIn(selected);

                    // Already pointing at one: that was the whole gesture.
                    if (folder is not null)
                    {
                        Stop();
                        answered.TrySetResult((WaitEnd.Done, folder));
                        return;
                    }

                    // Nothing chosen, and the pointer has been free the whole time: they
                    // could have pointed at a folder and did not, which is the plainest
                    // way of asking for a new one. Making it here rather than handing
                    // over is what keeps that a single gesture for a mouse.
                    if (!watcher.PointerCouldNotMove)
                    {
                        Log.Write("space let go with the pointer free: making a folder");

                        Stop();
                        answered.TrySetResult((WaitEnd.Done, null));
                        return;
                    }

                    // Nothing chosen yet, and now the pointer is free to choose. It was
                    // not before: a held key freezes a precision touchpad, so this is
                    // the first moment a folder can be reached at all.
                    handedOver = true;

                    Log.Write("space let go, pointer never free: handing over to the tick");

                    giveUp?.Stop();
                    _hint?.Anchor("Pick a folder", ModeGlyphs.Folder);
                });
            });
        };

        onConfirmed = (_, _) => Finish();
        onAccepted = (_, _) => Finish();

        onBack = (_, _) =>
        {
            Stop();
            answered.TrySetResult((WaitEnd.Back, null));
        };

        onCancelled = (_, _) =>
        {
            Stop();
            answered.TrySetResult((WaitEnd.Cancelled, null));
        };

        onClicked = (_, click) =>
        {
            if (click.SourceWindow != IntPtr.Zero) window = click.SourceWindow;
            var reading = window;

            Task.Run(async () =>
            {
                await Task.Delay(140).ConfigureAwait(false);

                var selected = await Core.Tools.StaExecutor
                    .RunAsync(() => ShellSelection.FromWindow(reading))
                    .ConfigureAwait(false);

                await Dispatcher.BeginInvoke(() =>
                {
                    if (!watcher.Waiting) return;

                    var folder = FolderIn(selected);
                    _hint?.Say(folder is null ? "Pick a folder" : Path.GetFileName(folder));
                });
            });
        };

        watcher.Waiting = true;
        watcher.SpaceReleased += onReleased;
        watcher.ConfirmRequested += onConfirmed;
        watcher.BackRequested += onBack;
        watcher.Cancelled += onCancelled;
        watcher.ClickedWhileWaiting += onClicked;

        _hint ??= new CursorHintWindow();
        _hint.Accepted += onAccepted;
        _hint.Follow("Pick a folder", ModeGlyphs.Folder);

        // Only while the key is still down. A key-up that never arrives -- an elevated
        // window taking focus mid-gesture is enough -- would otherwise leave this
        // waiting for good. Once the label is anchored there is no stuck key to guard
        // against, and the user is allowed to take as long as they like.
        giveUp = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        giveUp.Tick += (_, _) =>
        {
            Stop();

            // Cancel rather than quietly inventing a folder nobody asked for.
            answered.TrySetResult((WaitEnd.Cancelled, null));
        };
        giveUp.Start();

        return answered.Task;
    }

    /// <summary>
    /// Puts the question up and waits for the answer, or null if it was cancelled.
    ///
    /// Awaited rather than pumped. A nested message loop would block this one while the
    /// input hooks are still posting into it, and anything that stopped the window
    /// closing would take the whole app with it. Awaiting keeps the dispatcher running
    /// and has no way to hang.
    /// </summary>
    private static Task<string?> AskForAsync(InputRequest request, WheelChoice choice)
    {
        // Page numbers are checked against the document as they are typed, which needs to
        // know how long it is.
        var needsPages = request.Kind is InputKind.PageRange or InputKind.SplitPoints;
        var pageCount = needsPages && choice.Files.Count > 0
            ? SafePageCount(choice.Files[0].Path)
            : 1;

        var answered = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var prompt = new PromptWindow(request, pageCount);
            prompt.Closed += (_, _) => answered.TrySetResult(prompt.Answer);

            prompt.ShowAtCursor();
        }
        catch (Exception ex)
        {
            Log.Write($"could not ask for a value: {ex.Message}");
            answered.TrySetResult(null);
        }

        return answered.Task;
    }

    private static int SafePageCount(string path)
    {
        try
        {
            return Core.Tools.PdfRenderer.PageCount(path);
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private void RunAction(WheelChoice choice)
    {
        var outcome = QuickActions.Run(choice.Item.Id, choice.Files, files => ShowShelf(files));

        if (!outcome.Succeeded && outcome.Message != "Cancelled")
        {
            _tray?.Notify(choice.Item.Label, outcome.Message, isError: true);
        }
    }

    private async Task RunJobAsync(
        WheelChoice choice,
        Destination destination = Destination.Beside,
        string? folder = null)
    {
        var card = new ProgressWindow();
        using var cancellation = new CancellationTokenSource();
        card.Attach(cancellation);

        card.ShowNearCursor();
        card.Report(new JobProgress(choice.Item.Label, 0,
            choice.Files.Count == 1 ? choice.Files[0].Name : $"{choice.Files.Count} files"));

        var request = new JobRequest(choice.Item, choice.Files, choice.Category);
        var progress = new Progress<JobProgress>(card.Report);

        // Everything between showing the card and completing it is inside this, because
        // the card only ever closes once Complete has started its timer. Anything that
        // threw in between -- the delivery step used to be outside the try -- left a
        // card on screen for good, with no way to dismiss it and nothing but a tray
        // notification to say why.
        var result = BatchResult.Nothing();

        try
        {
            try
            {
                result = await _runner.RunAsync(request, progress, cancellation.Token);
            }
            catch (Exception ex)
            {
                result = BatchResult.Nothing(ex.Message);
            }

            // Handlers write beside the original; moving them is a separate, later
            // step, so a cancelled or failed run never half-moves anything.
            if (destination != Destination.Beside && result.AnySucceeded && !result.Cancelled)
            {
                result = await DeliverAsync(result, destination, choice.Item.Label, folder);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"job {choice.Item.Id} threw after the card was up: {ex}");
            result = BatchResult.Nothing(ex.Message);
        }

        // Until this was here a failed job left nothing behind at all, and the only
        // record of what went wrong was a card that dismissed itself after six seconds.
        Log.Write(
            $"job {choice.Item.Id} on {choice.Files.Count} file(s): " +
            $"{result.Outputs.Count} out, {result.Failures.Count} failed" +
            (result.Cancelled ? ", cancelled" : string.Empty));

        foreach (var failure in result.Failures) Log.Write($"  failed: {failure}");

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

    /// <summary>
    /// Moves finished results to wherever was asked for, off the UI thread.
    ///
    /// Anything that could not be moved is reported as a failure rather than swallowed:
    /// a result that is not where the user asked for it is not a result they have found.
    /// </summary>
    private static async Task<BatchResult> DeliverAsync(
        BatchResult result, Destination destination, string label, string? folder)
    {
        var produced = result.Produced;

        var delivered = await Task.Run(
            () => Delivery.Deliver(destination, produced, label, folder));

        foreach (var problem in delivered.Problems) Log.Write($"delivery: {problem}");

        return result with
        {
            Produced = delivered.Produced,
            Failures = result.Failures.Concat(delivered.Problems).ToList()
        };
    }

    private void ShowShelf(IReadOnlyList<SourceFile>? files = null)
    {
        _shelf ??= new FloatShelfWindow();

        // Reveal rather than Show: the shelf places and reglazes itself, and a bare
        // Show leaves it wherever WPF last thought it was, which is off screen.
        if (files is not null) _shelf.Add(files);
        else _shelf.Reveal();
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
