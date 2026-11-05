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
        _anchor = null;

        Fill(caption, glyph);
        Accept.Visibility = Visibility.Collapsed;

        SetClickThrough(true);

        if (!IsVisible) Show();

        // After Show, not before: a window that has never been shown measures to
        // something other than what it settles at, and the first label came out a row
        // taller than every one after it.
        UpdateLayout();

        PlaceAtCursor();
        _follow.Start();
    }

    /// <summary>
    /// Stops at the pointer and offers a tick, for when no key is being held and there
    /// is otherwise no way to say "done".
    /// </summary>
    public void Anchor(string caption, Geometry? glyph)
    {
        _anchor = null;

        Fill(caption, glyph);
        Accept.Visibility = Visibility.Visible;
        Tick.Data = ModeGlyphs.Tick;

        _follow.Stop();
        SetClickThrough(false);

        if (!IsVisible) Show();

        // After Show, and after the tick was made visible: Fill's own pass saw neither,
        // and the first label came out a row taller than every one after it.
        UpdateLayout();

        PlaceAtCursor();
    }

    /// <summary>Changes the words without moving the label or losing the mode.</summary>
    /// <summary>
    /// Changes the words without moving an anchored label.
    ///
    /// It used to re-place itself at the pointer whenever the caption changed, which
    /// made the tick impossible to click: pressing it starts a selection read, the
    /// read comes back a moment later and changes the count, and the label jumped to
    /// sit beside the pointer -- so the button was no longer under the finger that was
    /// still on its way up. The anchored label stays exactly where it was put, and
    /// only grows rightward as the text gets longer.
    /// </summary>
    public void Say(string caption)
    {
        if (Caption.Text == caption) return;

        Caption.Text = caption;
        UpdateLayout();

        if (_follow.IsEnabled) return;
        if (_anchor is not { } spot) return;

        PlaceAt(spot.X, spot.Y);
    }

    /// <summary>
    /// Moves to the pointer and says something new, for when the pointer is where the
    /// user just acted.
    ///
    /// Picking files to merge is a run of clicks across a file list, and a tick parked
    /// where the first one happened is a long way from the fifth. So each pick brings
    /// the label along: it is beside the file just chosen, which is where the hand
    /// already is and where the next click is likeliest to be.
    ///
    /// Distinct from <see cref="Say"/>, which must not move -- a caption that arrives
    /// on its own, from a read finishing, would otherwise walk the tick out from under
    /// a finger that is pressing it.
    /// </summary>
    public void Reanchor(string caption)
    {
        Caption.Text = caption;
        UpdateLayout();

        if (_follow.IsEnabled) return;

        PlaceAtCursor();
    }

    /// <summary>
    /// Swaps the mark without moving or re-wording anything.
    ///
    /// Space and Alt can be taken up and let go in the middle of picking files, and the
    /// whole point of holding a key is to see what it is doing while you hold it. The
    /// mark is the whole indicator, in the same thin stroke as the ones on the wheel: a
    /// folder for somewhere else, two arrows passing for over the top. No wording,
    /// because the caption is already counting files and a line of prose about a key the
    /// user is holding down tells them something they just did.
    /// </summary>
    public void Mark(Geometry? glyph)
    {
        if (ReferenceEquals(Glyph.Data, glyph)) return;

        Glyph.Data = glyph;
        Glyph.Visibility = glyph is null ? Visibility.Collapsed : Visibility.Visible;

        UpdateLayout();

        // Same reason Say does not move: a mark changing under a finger that is pressing
        // the tick must not take the tick with it.
        if (_follow.IsEnabled) return;
        if (_anchor is not { } spot) return;

        PlaceAt(spot.X, spot.Y);
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
        var (width, height) = SizeAt(scale);

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

        if (!_follow.IsEnabled) _anchor = (left, top);

        PlaceAt(left, top);
    }

    /// <summary>Where an anchored label was put, so it can be left there.</summary>
    private (int X, int Y)? _anchor;

    /// <summary>
    /// The pill's size in device pixels, measured on the Border rather than on the
    /// Window.
    ///
    /// A window that has not been shown yet reports a height it does not keep -- 35.5
    /// against the 30 it settles at -- so the first label of a session came out a row
    /// taller than every one after it, and no amount of asking for the layout again
    /// before placing it helped. The Border is the whole of the content, has a fixed
    /// height, and measures the same every time; once the window has settled the two
    /// are identical, so nothing is clipped by going from one to the other.
    /// </summary>
    private (int Width, int Height) SizeAt(double scale)
    {
        var width = Pill.ActualWidth > 0 ? Pill.ActualWidth : Pill.DesiredSize.Width;
        var height = Pill.ActualHeight > 0 ? Pill.ActualHeight : Pill.DesiredSize.Height;

        if (width <= 0) width = ActualWidth;
        if (height <= 0) height = ActualHeight;

        return ((int)Math.Round(width * scale), (int)Math.Round(height * scale));
    }

    private void PlaceAt(int left, int top)
    {
        var scale = DpiAt(new POINT(left, top)) / 96.0;
        var (width, height) = SizeAt(scale);

        if (width <= 0 || height <= 0) return;

        Diagnostics.Log.Write(
            $"hint: '{Caption.Text}' at {left},{top} {width}x{height} " +
            (_follow.IsEnabled ? "following" : "anchored"));

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
