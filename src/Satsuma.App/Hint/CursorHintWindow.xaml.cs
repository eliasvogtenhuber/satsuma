using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Satsuma.App.Glass;
using Satsuma.App.Interop;
using static Satsuma.App.Interop.NativeMethods;

namespace Satsuma.App.Hint;

/// <summary>
/// Says what Satsuma is waiting for, at the pointer.
///
/// The wheel is gone by the time this appears -- the drop ended it -- but the gesture is
/// not over. Something has to carry the question, and a mark parked where the drop
/// happened turned out to read as a stray icon on the desktop rather than as part of
/// anything. At the pointer's elbow it is where the eye already is, and it moves with
/// the hand that is about to answer it.
///
/// Two modes, because there are two ways to be waiting. With a key still down there is
/// nothing to click and the label simply follows. With no key down the gesture has no
/// natural end, so the label stops at the pointer and grows a tick to click.
/// </summary>
public partial class CursorHintWindow : Window
{
    private readonly DispatcherTimer _follow;
    private bool _clickThrough = true;

    /// <summary>The tick was clicked.</summary>
    public event EventHandler? Accepted;

    public CursorHintWindow()
    {
        InitializeComponent();

        _follow = new DispatcherTimer(DispatcherPriority.Render)
        {
            // Fast enough to sit with the pointer rather than trail it.
            Interval = TimeSpan.FromMilliseconds(15)
        };
        _follow.Tick += (_, _) => PlaceAtCursor();

        Accept.MouseLeftButtonUp += (_, _) => Accepted?.Invoke(this, EventArgs.Empty);
        Accept.MouseEnter += (_, _) => Accept.Background = Brush(0x33);
        Accept.MouseLeave += (_, _) => Accept.Background = Brush(0x1A);

        SourceInitialized += (_, _) => ApplyStyle();
    }

    private static SolidColorBrush Brush(byte alpha) =>
        new(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));

    /// <summary>
    /// Follows the pointer while a key is held. Click-through, because the next thing
    /// the user does is click a file, and this must never be what they hit.
    /// </summary>
    public void Follow(string caption, Geometry? glyph)
    {
        Fill(caption, glyph);
        Accept.Visibility = Visibility.Collapsed;

        SetClickThrough(true);

        if (!IsVisible) Show();

        PlaceAtCursor();
        _follow.Start();
    }

    /// <summary>
    /// Stops at the pointer and offers a tick, for when no key is being held and there
    /// is otherwise no way to say "done".
    /// </summary>
    public void Anchor(string caption, Geometry? glyph)
    {
        Fill(caption, glyph);
        Accept.Visibility = Visibility.Visible;
        Tick.Data = ModeGlyphs.Tick;

        _follow.Stop();
        SetClickThrough(false);

        if (!IsVisible) Show();

        PlaceAtCursor();
    }

    /// <summary>Changes the words without moving the label or losing the mode.</summary>
    public void Say(string caption)
    {
        Caption.Text = caption;
        UpdateLayout();

        if (!_follow.IsEnabled) PlaceAtCursor();
    }

    public void Dismiss()
    {
        _follow.Stop();
        if (IsVisible) Hide();
    }

    private void Fill(string caption, Geometry? glyph)
    {
        Caption.Text = caption;

        Glyph.Data = glyph;
        Glyph.Visibility = glyph is null ? Visibility.Collapsed : Visibility.Visible;

        UpdateLayout();
    }

    /// <summary>
    /// Beside the pointer, on the right, flipping to the left near a screen edge rather
    /// than sliding along it and ending up somewhere the eye is not.
    /// </summary>
    private void PlaceAtCursor()
    {
        if (!GetCursorPos(out var cursor)) return;

        var scale = DpiAt(cursor) / 96.0;
        var width = (int)Math.Round(ActualWidth * scale);
        var height = (int)Math.Round(ActualHeight * scale);

        if (width <= 0 || height <= 0) return;

        var gap = (int)Math.Round(20 * scale);
        var left = cursor.X + gap;
        var top = cursor.Y - height / 2;

        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>()
        };

        if (GetMonitorInfo(monitor, ref info))
        {
            var work = info.rcWork;

            if (left + width > work.Right) left = cursor.X - gap - width;

            left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
            top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
        }

        WindowPlacement.Apply(
            new WindowInteropHelper(this).Handle,
            left, top, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW, "hint");
    }

    private void SetClickThrough(bool value)
    {
        if (_clickThrough == value) return;

        _clickThrough = value;
        if (IsVisible || new WindowInteropHelper(this).Handle != IntPtr.Zero) ApplyStyle();
    }

    private void ApplyStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var style = GetWindowLong(handle, GWL_EXSTYLE)
                    | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;

        style = _clickThrough
            ? style | WS_EX_TRANSPARENT
            : style & ~WS_EX_TRANSPARENT;

        SetWindowLong(handle, GWL_EXSTYLE, style);
    }
}
