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

    private static readonly Brush ProgressNormal = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Brush ProgressCancelled = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)));
    private static readonly Brush ProgressPartial = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xB4, 0x4E)));
    private static readonly Brush ProgressFailed = Freeze(new SolidColorBrush(Color.FromRgb(0xE2, 0x6D, 0x5A)));

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

    /// <summary>Switches the card from progress to outcome and starts the countdown.</summary>
    public void Complete(BatchResult result, string actionLabel)
    {
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
            BarFill.Background = ProgressPartial;
        }
        else
        {
            TitleText.Text = "Did not work";
            DetailText.Text = result.Failures.Count > 0 ? result.Failures[0] : "No output.";
            BarFill.Background = ProgressFailed;
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
        if (_results.Count == 0) return;

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

        var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(handle, HWND_TOPMOST, left, top, width, height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
