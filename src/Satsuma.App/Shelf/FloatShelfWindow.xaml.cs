using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Satsuma.App.Glass;
using Satsuma.App.Interop;
using Satsuma.Core.Model;
using static Satsuma.App.Interop.NativeMethods;

namespace Satsuma.App.Shelf;

/// <summary>One parked file, as the shelf shows it.</summary>
public sealed record ShelfEntry(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetFileName(
        System.IO.Path.GetDirectoryName(Path)?.TrimEnd('\\') ?? string.Empty);
}

/// <summary>
/// A small floating tray of files to drag out again later.
///
/// It is the answer to the awkward case where the source and destination are never on
/// screen together: park the files here, go and find the destination, then drag them off.
///
/// Unlike every other surface in Satsuma this one persists and is moved by hand, which
/// is why it carries its own glass rather than capturing once on the way up: the
/// photograph behind it is taken again each time it is shown, resized or set down
/// somewhere new.
/// </summary>
public partial class FloatShelfWindow : Window
{
    private readonly ObservableCollection<ShelfEntry> _entries = new();
    private Point _pressOrigin;
    private bool _pressed;
    private bool _dragged;

    public FloatShelfWindow()
    {
        InitializeComponent();

        Items.ItemsSource = _entries;

        TitleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;

            var from = WhereItIs();

            try
            {
                // Blocks until the move is over, so the glass can be retaken afterwards
                // rather than smearing the place it came from across the new one.
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // The button was already up by the time this ran.
                return;
            }

            // Only when it actually went somewhere. A plain click on the strip should
            // not cost a capture, and a capture means hiding the window for a frame.
            if (WhereItIs() != from) Reglaze();
        };

        // The cross lives inside the strip you drag the shelf by, so its press bubbles
        // up to the strip and starts DragMove -- which blocks until the button comes up
        // and swallows the release, so the click never became a close. Stopping the
        // press here is what keeps the two apart. It is also why the cross seemed
        // unclickable before it was a proper target: the press that missed its strokes
        // went to the strip and dragged the window a pixel instead.
        CloseButton.MouseLeftButtonDown += (_, e) => e.Handled = true;

        CloseButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Hide();
        };

        CloseButton.MouseEnter += (_, _) => CloseButton.Background = Tint(0x26);
        CloseButton.MouseLeave += (_, _) => CloseButton.Background = Tint(0x00);

        Items.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _pressOrigin = e.GetPosition(this);
            _pressed = true;
            _dragged = false;
        };

        Items.PreviewMouseMove += OnItemsMouseMove;
        Items.PreviewMouseLeftButtonUp += OnItemsMouseUp;

        // Taking one off the shelf without opening it or dragging it anywhere.
        Items.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (EntryAt(e) is not { } entry) return;

            e.Handled = true;
            Remove(entry);
        };
    }

    /// <summary>
    /// The window's own corner, in real pixels. Asked of Windows rather than of WPF,
    /// because the shelf is placed with SetWindowPos and WPF's Left and Top do not
    /// always know about it.
    /// </summary>
    private (int X, int Y) WhereItIs()
    {
        var handle = new WindowInteropHelper(this).Handle;

        return handle != IntPtr.Zero && GetWindowRect(handle, out var rect)
            ? (rect.Left, rect.Top)
            : (0, 0);
    }

    private static System.Windows.Media.Brush Tint(byte alpha) =>
        new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));

    public void Add(IReadOnlyList<SourceFile> files)
    {
        foreach (var file in files)
        {
            if (_entries.Any(e => string.Equals(e.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                continue;

            _entries.Add(new ShelfEntry(file.Path));
        }

        UpdateFooter();
        Reveal();
    }

    /// <summary>
    /// Brings the shelf up, where it was the last time it was up.
    ///
    /// The off-screen park is how the height is settled before anything is photographed,
    /// and it is also how the shelf went missing: parking happened on every reveal while
    /// placing happened only on the first, so the second time it was shown it sat at
    /// -20000 and stayed there. Where it sits is remembered here rather than read back
    /// from the window, because the window is moved with SetWindowPos behind WPF's back
    /// and Hide/Show is free to put it wherever WPF last thought it was.
    /// </summary>
    public void Reveal()
    {
        if (!IsVisible)
        {
            Left = -20000;
            Top = -20000;
            Show();
        }

        if (_placed) Restore();
        else
        {
            PlaceBottomRight();
            _placed = true;
        }

        Reglaze();
        Activate();
    }

    private bool _placed;
    private int _spotLeft;
    private int _spotTop;

    /// <summary>Back to the corner it was left in, at whatever size it is now.</summary>
    private void Restore()
    {
        var scale = DpiAt(new POINT(_spotLeft, _spotTop)) / 96.0;
        var size = Measure(scale);

        SetWindowPos(
            new WindowInteropHelper(this).Handle, HWND_TOPMOST,
            _spotLeft, _spotTop, size.Width, size.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Out of the way to begin with: bottom right of the monitor the pointer is on,
    /// above the taskbar, which is where a parking spot belongs.
    /// </summary>
    private void PlaceBottomRight()
    {
        if (!GetCursorPos(out var cursor)) return;

        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>()
        };

        if (!GetMonitorInfo(monitor, ref info)) return;

        var scale = DpiAt(cursor) / 96.0;
        var size = Measure(scale);
        var margin = (int)Math.Round(16 * scale);

        _spotLeft = info.rcWork.Right - size.Width - margin;
        _spotTop = info.rcWork.Bottom - size.Height - margin;

        SetWindowPos(
            new WindowInteropHelper(this).Handle, HWND_TOPMOST,
            _spotLeft, _spotTop, size.Width, size.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Settles the height on the content, then freezes it.
    ///
    /// SizeToContent has to be off before the backdrop goes on: an Image with a source
    /// contributes its own natural size, so a window still sizing itself would grow to
    /// fit the photograph of itself.
    /// </summary>
    private (int Width, int Height) Measure(double scale)
    {
        SizeToContent = SizeToContent.Height;
        UpdateLayout();

        Height = ActualHeight;
        SizeToContent = SizeToContent.Manual;

        return ((int)Math.Round(ActualWidth * scale), (int)Math.Round(ActualHeight * scale));
    }

    /// <summary>
    /// Takes the photograph behind the shelf again and puts it back where it was.
    ///
    /// Hidden for the capture, because the capture is of the screen and the shelf is on
    /// it: left visible it photographs itself, and every retake blurs the last one a
    /// little further until the glass is mud.
    /// </summary>
    private void Reglaze()
    {
        if (!IsVisible) return;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        if (!GetWindowRect(handle, out var rect)) return;

        // Wherever it has ended up is where it lives from now on, including after a
        // drag, so closing and reopening brings it back to the same corner.
        _spotLeft = rect.Left;
        _spotTop = rect.Top;

        var scale = DpiAt(new POINT(rect.Left, rect.Top)) / 96.0;
        var size = Measure(scale);

        ShowWindow(handle, SW_HIDE);
        var capture = ScreenCapture.Capture(rect.Left, rect.Top, size.Width, size.Height);

        Backdrop.Source = capture;
        Scrim.Background = GlassTint.For(capture);
        UpdateLayout();

        WindowPlacement.Apply(handle, rect.Left, rect.Top, size.Width, size.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW, "shelf");
    }

    /// <summary>
    /// The footer says what to do with what is there, and the shelf can be empty: the
    /// tray menu opens it whether or not anything has been parked on it yet.
    /// </summary>
    private void UpdateFooter() =>
        FooterText.Text = _entries.Count == 0
            ? "Nothing parked yet"
            : "Click to open, or drag it off";

    private ShelfEntry? EntryAt(MouseEventArgs e) =>
        (e.OriginalSource as FrameworkElement)?.DataContext as ShelfEntry;

    private void Remove(ShelfEntry entry)
    {
        _entries.Remove(entry);
        UpdateFooter();

        // Emptied by hand rather than closed, so it stays up and says so. Closing is
        // the cross, and the two should not be the same gesture.
        Reglaze();
    }

    /// <summary>
    /// A click with no drag in it opens the file, the way a click on anything in a file
    /// list does. The drag threshold is what tells the two apart, so dragging one off
    /// still works and never opens anything.
    /// </summary>
    private void OnItemsMouseUp(object sender, MouseButtonEventArgs e)
    {
        var wasDrag = _dragged;
        _pressed = false;
        _dragged = false;

        if (wasDrag) return;
        if (EntryAt(e) is not { } entry) return;

        e.Handled = true;
        Open(entry);
    }

    private void Open(ShelfEntry entry)
    {
        try
        {
            Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Write($"shelf could not open {entry.Name}: {ex.Message}");
        }
    }

    private void OnItemsMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _pressOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _pressOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressed = false;
        _dragged = true;

        if (EntryAt(e) is not { } entry) return;

        var paths = new StringCollection { entry.Path };
        var data = new DataObject();
        data.SetFileDropList(paths);

        // Copy only: the shelf is a staging area, not a move queue, and the file should
        // still be where it was when this is over.
        DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
    }
}
