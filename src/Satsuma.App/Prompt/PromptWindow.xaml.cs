using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Satsuma.App.Glass;
using Satsuma.App.Interop;
using Satsuma.Core.Model;
using static Satsuma.App.Interop.NativeMethods;

namespace Satsuma.App.Prompt;

/// <summary>
/// Asks for the one value a slice could not guess.
///
/// Presets answer what most people want most of the time; this is for the rest. The wheel
/// itself cannot ask, because it is never focused — taking focus would cancel the drag it
/// lives inside — so the question comes after the drop, when the drag is over.
///
/// It validates as you type rather than on submit, so a value that will not be accepted
/// says so before Enter is pressed rather than after.
/// </summary>
public partial class PromptWindow : Window
{
    private readonly InputRequest _request;
    private readonly int _pageCount;

    private static readonly Brush FieldNormal =
        Freeze(new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)));

    private static readonly Brush FieldRejected =
        Freeze(new SolidColorBrush(Color.FromArgb(0xB3, 0xE2, 0x6D, 0x5A)));

    /// <summary>What was typed, or null if it was cancelled.</summary>
    public string? Answer { get; private set; }

    public PromptWindow(InputRequest request, int pageCount = 1)
    {
        _request = request;
        _pageCount = pageCount;

        InitializeComponent();

        TitleText.Text = request.Title;
        HintText.Text = request.Hint;
        Example.Text = request.Placeholder ?? string.Empty;

        Field.TextChanged += (_, _) => ShowWhetherAccepted();
        PreviewKeyDown += OnKey;

        SourceInitialized += (_, _) =>
        {
            // Out of Alt-Tab, but focusable, which is the difference from the wheel.
            var handle = new WindowInteropHelper(this).Handle;
            var exStyle = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
        };

        Loaded += (_, _) =>
        {
            Activate();
            Field.Focus();
        };

        // Clicking away is the same as changing your mind.
        Deactivated += (_, _) => { if (Answer is null) Close(); };
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Answer = null;
                e.Handled = true;
                Close();
                break;

            case Key.Enter:
                e.Handled = true;

                if (!CustomInput.IsValid(_request.Kind, Field.Text, _pageCount))
                {
                    ShowWhetherAccepted();
                    return;
                }

                Answer = Field.Text.Trim();
                Close();
                break;
        }
    }

    private void ShowWhetherAccepted()
    {
        Example.Visibility = Field.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // An empty field is not wrong yet, just empty.
        var acceptable = Field.Text.Trim().Length == 0
                         || CustomInput.IsValid(_request.Kind, Field.Text, _pageCount);

        FieldBorder.BorderBrush = acceptable ? FieldNormal : FieldRejected;
    }

    /// <summary>
    /// Centred on the pointer, frosted over whatever it covers, the way the wheel is.
    /// </summary>
    public void ShowAtCursor()
    {
        GetCursorPos(out var cursor);

        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>()
        };

        var known = GetMonitorInfo(monitor, ref info);

        // Realised off-screen first, so WPF can lay the card out and settle on a height
        // for this particular question. Measuring it by hand was close enough to look
        // right and wrong by a line, which is the worst of both: the card centres its
        // content, so a few pixels short takes the title off the top and the footer off
        // the bottom at the same time, and what is left reads like the whole card.
        Left = -20000;
        Top = -20000;
        Show();

        // Then parked directly below the monitor it is about to appear on, which is
        // still off every screen but keeps it nearest to that one monitor. It is laid
        // out at that monitor's DPI and does not change scale on the way into place.
        if (known)
        {
            SetWindowPos(
                new WindowInteropHelper(this).Handle, HWND_TOPMOST,
                cursor.X, info.rcMonitor.Bottom + 8, 0, 0, SWP_NOSIZE | SWP_SHOWWINDOW);
        }

        UpdateLayout();

        var scale = DpiAt(cursor) / 96.0;
        var width = (int)Math.Round(ActualWidth * scale);
        var height = (int)Math.Round(ActualHeight * scale);

        var left = cursor.X - width / 2;
        var top = cursor.Y - height / 2;

        if (known)
        {
            var work = info.rcWork;
            left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
            top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
        }

        var capture = ScreenCapture.Capture(left, top, width, height);
        Backdrop.Source = capture;
        Scrim.Background = GlassTint.For(capture);

        // Glass on before the move, so the first frame anyone sees is the finished card.
        UpdateLayout();

        Diagnostics.Log.Write(
            $"prompt: {_request.Title} at {left},{top} size={width}x{height} " +
            $"({ActualWidth:0}x{ActualHeight:0} at {scale:0.##}x)");

        WindowPlacement.Apply(
            new WindowInteropHelper(this).Handle,
            left, top, width, height, SWP_SHOWWINDOW, "prompt");
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
