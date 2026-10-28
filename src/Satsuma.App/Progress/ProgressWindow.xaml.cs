using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Satsuma.App.Actions;
using Satsuma.Core.Engine;
using Satsuma.Core.Model;
using static Satsuma.App.Interop.NativeMethods;

namespace Satsuma.App.Progress;

/// <summary>
/// The small window that appears while a job runs and then reports what happened.
///
/// It never takes focus, parks itself bottom-right of whichever monitor the pointer is on,
/// and goes away on its own, so a conversion never interrupts what the user was doing.
/// </summary>
public partial class ProgressWindow : Window
{
    private readonly DispatcherTimer _dismissTimer;
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<string> _results = Array.Empty<string>();

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
        Bar.Value = Math.Clamp(progress.Fraction, 0, 1);
    }

    /// <summary>Switches the card from progress to outcome and starts the countdown.</summary>
    public void Complete(BatchResult result, string actionLabel)
    {
        _results = result.Outputs;
        _dismissTimer.Stop();

        CancelButton.Visibility = Visibility.Collapsed;
        Bar.Value = 1;

        if (result.Cancelled)
        {
            TitleText.Text = "Cancelled";
            DetailText.Text = result.Outputs.Count > 0
                ? $"{result.Outputs.Count} finished first"
                : string.Empty;
            Bar.Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A));
        }
        else if (result.AllSucceeded)
        {
            TitleText.Text = Describe(actionLabel, result.Outputs.Count);
            DetailText.Text = result.Outputs.Count > 0
                ? Path.GetFileName(result.Outputs[0]) + (result.Outputs.Count > 1
                    ? $"  +{result.Outputs.Count - 1} more"
                    : string.Empty)
                : string.Empty;
        }
        else if (result.AnySucceeded)
        {
            TitleText.Text = $"{result.Outputs.Count} done, {result.Failures.Count} failed";
            DetailText.Text = result.Failures[0];
            Bar.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        }
        else
        {
            TitleText.Text = "Did not work";
            DetailText.Text = result.Failures.Count > 0 ? result.Failures[0] : "No output.";
            Bar.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x5A, 0x4A));
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

    /// <summary>Parks the card bottom-right of the monitor the pointer is on.</summary>
    public void PositionNearCursor()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var dpi = GetDpiForWindow(handle);
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96.0;

        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Height * scale);

        GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);

        var info = new MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>()
        };

        var left = cursor.X;
        var top = cursor.Y;

        if (GetMonitorInfo(monitor, ref info))
        {
            const int margin = 12;
            left = info.rcWork.Right - width - margin;
            top = info.rcWork.Bottom - height - margin;
        }

        SetWindowPos(handle, HWND_TOPMOST, left, top, width, height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }
}
