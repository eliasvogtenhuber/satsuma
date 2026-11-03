using System.Windows;
using System.Windows.Interop;
using Satsuma.App.Glass;
using Satsuma.App.Interop;
using Satsuma.Core.Model;
using static Satsuma.App.Interop.NativeMethods;

namespace Satsuma.App.Hint;

/// <summary>
/// The mark that stays on screen while Satsuma is waiting to be told where, or which.
///
/// The wheel is gone by this point -- the drop ended it -- but the gesture is not over:
/// Space is still down, and what happens next depends on whether it is released or a
/// folder is clicked first. So the mark has to outlive the wheel, which means a window
/// of its own.
/// </summary>
public partial class ModeHintWindow : Window
{
    public ModeHintWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var exStyle = GetWindowLong(handle, GWL_EXSTYLE);

            // TRANSPARENT is the one that matters: the next thing the user does is click
            // a folder, and this must not be what they hit.
            SetWindowLong(handle, GWL_EXSTYLE,
                exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
        };
    }

    /// <summary>Puts the mark up beside the pointer, out from under it.</summary>
    public void ShowFor(Destination destination, int screenX, int screenY)
    {
        var glyph = ModeGlyphs.For(destination);
        if (glyph is null) return;

        ShowGlyph(glyph, screenX, screenY);
    }

    /// <summary>
    /// Shows how many files have been gathered so far, which replaces the mark once
    /// there is a number to show.
    /// </summary>
    public void ShowTally(int gathered, int screenX, int screenY)
    {
        if (gathered <= 0)
        {
            ShowGlyph(ModeGlyphs.Plus, screenX, screenY);
            return;
        }

        Count.Text = gathered.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Confirm.Data = ModeGlyphs.Return;

        Glyph.Visibility = Visibility.Collapsed;
        Tally.Visibility = Visibility.Visible;

        // Already up and only the number changed; moving it would chase the pointer
        // around while the user is trying to click things.
        if (IsVisible) return;

        Place(screenX, screenY);
    }

    private void ShowGlyph(System.Windows.Media.Geometry glyph, int screenX, int screenY)
    {
        Glyph.Data = glyph;
        Glyph.Visibility = Visibility.Visible;
        Tally.Visibility = Visibility.Collapsed;

        if (IsVisible) return;

        Place(screenX, screenY);
    }

    private void Place(int screenX, int screenY)
    {
        var cursor = new POINT(screenX, screenY);
        var scale = DpiAt(cursor) / 96.0;
        var size = (int)Math.Round(Width * scale);

        // Down and to the right of the pointer, where a cursor's own shadow would fall,
        // so it never covers the thing about to be clicked.
        var left = screenX + (int)Math.Round(18 * scale);
        var top = screenY + (int)Math.Round(18 * scale);

        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>()
        };

        if (GetMonitorInfo(monitor, ref info))
        {
            var work = info.rcWork;
            left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - size));
            top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - size));
        }

        var capture = ScreenCapture.Capture(left, top, size, size);
        Backdrop.Source = capture;
        Scrim.Background = GlassTint.For(capture);

        Show();

        WindowPlacement.Apply(
            new WindowInteropHelper(this).Handle,
            left, top, size, size, SWP_NOACTIVATE | SWP_SHOWWINDOW, "hint");
    }

    /// <summary>Takes the mark away without destroying the window, which is reused.</summary>
    public void Dismiss()
    {
        if (IsVisible) Hide();
    }
}
