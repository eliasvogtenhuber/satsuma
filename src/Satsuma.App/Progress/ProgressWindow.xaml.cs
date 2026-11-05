using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Satsuma.App.Actions;
using Satsuma.App.Glass;
using Satsuma.App.Interop;
using Satsuma.Core.Engine;
using Satsuma.Core.Model;
using static Satsuma.App.Interop.NativeMethods;

namespace Satsuma.App.Progress;

/// <summary>
/// The small card that appears while a job runs and then reports what happened.
///
/// It never takes focus, parks itself bottom-right of whichever monitor the pointer is on,
/// and goes away on its own, so a conversion never interrupts what the user was doing.
/// Made of the same glass as the wheel, because it is the other half of the same gesture.
/// </summary>
public partial class ProgressWindow : Window
{
    private readonly DispatcherTimer _dismissTimer;
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<string> _results = Array.Empty<string>();
    private double _fraction;

    /// <summary>
    /// The bar has no colours, only a length.
    ///
    /// It filled amber for a partial run and red for a failed one, and both read as
    /// decoration: nothing else in Satsuma is coloured, so a coloured bar is the only
    /// hue on screen and draws the eye to the least informative part of the card. The
    /// length already says everything -- full for done, part-way for partly done,
    /// nothing for a run that produced nothing -- and the line above it says the rest
    /// in words.
    /// </summary>
    private static readonly Brush ProgressNormal = Freeze(new SolidColorBrush(Colors.White));

    private static readonly Brush ProgressCancelled =
        Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)));

    public ProgressWindow()
    {
        InitializeComponent();

        _dismissTimer = new DispatcherTimer();
        _dismissTimer.Tick += (_, _) => { _dismissTimer.Stop(); Close(); };

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var exStyle = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };

        // The bar is drawn, so it has to be re-laid out when the card is sized.
        BarTrack.SizeChanged += (_, _) => ApplyFraction();

        CancelButton.MouseLeftButtonUp += (_, _) => _cancellation?.Cancel();

        // Clicking the card once it has finished shows what came out of it.
        MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource == CancelButton) return;
            RevealResults();
        };
    }

    public void Attach(CancellationTokenSource cancellation) => _cancellation = cancellation;

    public void Report(JobProgress progress)
    {
        // A report that arrives after the outcome is a report about work that is over.
        if (_completed) return;

        TitleText.Text = progress.Title;
        DetailText.Text = progress.Detail ?? string.Empty;

        _fraction = Math.Clamp(progress.Fraction, 0, 1);
        ApplyFraction();
    }

    private void ApplyFraction()
    {
        var track = BarTrack.ActualWidth;
        if (track > 0) BarFill.Width = track * _fraction;
    }

    /// <summary>
    /// True once the card is showing an outcome, after which progress is history.
    ///
    /// A handler's last progress report crosses the dispatcher on its way here, so it
    /// can arrive after the job has finished and overwrite "Did not work" with the
    /// action's name and a filename -- a card that says a failed job succeeded.
    /// </summary>
    private bool _completed;

    /// <summary>Switches the card from progress to outcome and starts the countdown.</summary>
    public void Complete(BatchResult result, string actionLabel)
    {
        _completed = true;
        _results = result.Outputs;
        _dismissTimer.Stop();

        CancelButton.Visibility = Visibility.Collapsed;
        _fraction = 1;
        ApplyFraction();

        if (result.Cancelled)
        {
            TitleText.Text = "Cancelled";
            DetailText.Text = result.Outputs.Count > 0
                ? $"{result.Outputs.Count} finished first"
                : string.Empty;
            BarFill.Background = ProgressCancelled;
        }
        else if (result.AllSucceeded)
        {
            TitleText.Text = Describe(actionLabel, result.Outputs.Count);
            DetailText.Text = result.Outputs.Count > 0
                ? Path.GetFileName(result.Outputs[0]) + (result.Outputs.Count > 1
                    ? $"  +{result.Outputs.Count - 1} more"
                    : string.Empty)
                : string.Empty;
            BarFill.Background = ProgressNormal;
        }
        else if (result.AnySucceeded)
        {
            TitleText.Text = $"{result.Outputs.Count} done, {result.Failures.Count} failed";
            DetailText.Text = result.Failures[0];
            BarFill.Background = ProgressNormal;

            // The bar stops where the work did, against the faint track it always has.
            var total = result.Outputs.Count + result.Failures.Count;
            _fraction = total > 0 ? result.Outputs.Count / (double)total : 1;
            ApplyFraction();
        }
        else
        {
            TitleText.Text = "Did not work";
            DetailText.Text = result.Failures.Count > 0 ? result.Failures[0] : "No output.";

            // Nothing was produced, so the bar shows nothing. An empty track under
            // "Did not work" needs no colour to be understood.
            BarFill.Background = ProgressNormal;
            _fraction = 0;
            ApplyFraction();
        }

        // A failure stays up long enough to actually read.
        _dismissTimer.Interval = TimeSpan.FromSeconds(result.AllSucceeded ? 2.6 : 6.5);
        _dismissTimer.Start();
    }

    private static string Describe(string actionLabel, int count) => count switch
    {
        0 => actionLabel,
        1 => $"{actionLabel} ✓",
        _ => $"{actionLabel} ✓  {count} files"
    };

    private void RevealResults()
    {
        // A card with nothing to reveal is a card reporting a failure, and clicking it
        // did nothing at all: no reveal, and no close either, so the only way to be rid
        // of it was to wait out the timer. Clicking anything that will not open should
        // at least put it away.
        if (_results.Count == 0)
        {
            Close();
            return;
        }

        foreach (var group in _results
                     .Where(p => File.Exists(p) || Directory.Exists(p))
                     .GroupBy(p => Path.GetDirectoryName(p) ?? string.Empty,
                         StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length == 0) continue;
            QuickActions.SelectInExplorer(group.Key, group.ToList());
        }

        Close();
    }

    /// <summary>
    /// Parks the card bottom-right of the monitor the pointer is on, frosting whatever it
    /// lands on. The backdrop is captured before the window is shown there, or the card
    /// photographs itself.
    /// </summary>
    public void ShowNearCursor()
    {
        GetCursorPos(out var cursor);

        var scale = DpiAt(cursor) / 96.0;
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Height * scale);

        var left = cursor.X;
        var top = cursor.Y;

        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>()
        };

        if (GetMonitorInfo(monitor, ref info))
        {
            const int margin = 10;
            left = info.rcWork.Right - width - margin;
            top = info.rcWork.Bottom - height - margin;
        }

        var capture = ScreenCapture.Capture(left, top, width, height);
        Backdrop.Source = capture;
        Scrim.Background = GlassTint.For(capture);

        Show();

        WindowPlacement.Apply(
            new WindowInteropHelper(this).Handle,
            left, top, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW, "progress");
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
