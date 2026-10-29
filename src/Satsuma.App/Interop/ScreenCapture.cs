using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Satsuma.App.Interop;

/// <summary>
/// Grabs a rectangle of the screen.
///
/// This is what makes the wheel frosted rather than merely see-through. Windows will not
/// blur what is behind an arbitrary shape: the documented blur APIs went away after
/// Windows 7, and the accent ones apply to a rectangle and fight with per-pixel
/// transparency. Capturing the pixels the wheel is about to cover and blurring them in
/// WPF gives real refraction of the actual desktop, costs a couple of milliseconds, and
/// uses nothing undocumented.
///
/// The capture has to happen before the wheel is shown, or it photographs itself.
/// </summary>
internal static class ScreenCapture
{
    /// <summary>
    /// Captures a rectangle, then redraws the window under its centre on top of it from
    /// that window's own content.
    ///
    /// This is for capturing during a drag. The system paints the dragged file's ghost as
    /// a layered window riding the cursor, and the cursor is exactly where the wheel
    /// appears, so the ghost lands in the middle of the backdrop and blurs into a bright
    /// smear. Asking the window underneath to draw itself gets its real content with no
    /// overlay of any kind on it.
    /// </summary>
    public static BitmapSource? CaptureWithoutOverlays(int x, int y, int width, int height) =>
        Capture(x, y, width, height, redrawWindowUnderCentre: true);

    public static BitmapSource? Capture(int x, int y, int width, int height) =>
        Capture(x, y, width, height, redrawWindowUnderCentre: false);

    private static BitmapSource? Capture(
        int x, int y, int width, int height, bool redrawWindowUnderCentre)
    {
        if (width <= 0 || height <= 0) return null;

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;

        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero) return null;

            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == IntPtr.Zero) return null;

            var previous = SelectObject(memoryDc, bitmap);

            // CAPTUREBLT is needed, not avoided. Without it layered windows are left out
            // of the copy, and the pixels they covered come back undefined rather than
            // transparent -- which showed up as white patches in the frosted backdrop
            // during a drag, where the system paints the dragged file's ghost as exactly
            // such a window. Including them costs only that ghost appearing in the blur,
            // where at this radius it is not visible.
            var copied = BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y,
                SRCCOPY | CAPTUREBLT);

            if (copied && redrawWindowUnderCentre)
            {
                RedrawWindowUnder(memoryDc, x, y, width, height);
            }

            SelectObject(memoryDc, previous);
            if (!copied) return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();   // handed to the UI thread, so it must be frozen
            return source;
        }
        catch (Exception)
        {
            // A capture failure just means a plainer wheel, never a broken one.
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Draws the window under the centre of the captured area into the same bitmap, in
    /// the right place, from the window's own content rather than from the screen.
    /// </summary>
    private static void RedrawWindowUnder(IntPtr targetDc, int x, int y, int width, int height)
    {
        try
        {
            var centre = new NativeMethods.POINT(x + width / 2, y + height / 2);
            var window = NativeMethods.GetAncestor(
                NativeMethods.WindowFromPoint(centre), NativeMethods.GA_ROOT);

            if (window == IntPtr.Zero) return;
            if (!NativeMethods.GetWindowRect(window, out var bounds)) return;

            // PrintWindow always draws at the origin of the target, so the origin is
            // moved to where the window actually sits within the captured rectangle.
            if (!SetViewportOrgEx(targetDc, bounds.Left - x, bounds.Top - y, out var previous))
            {
                return;
            }

            // RENDERFULLCONTENT is what makes this work for a modern composited window;
            // without it the result is commonly blank.
            PrintWindow(window, targetDc, PW_RENDERFULLCONTENT);

            SetViewportOrgEx(targetDc, previous.X, previous.Y, out _);
        }
        catch (Exception)
        {
            // Worst case the backdrop keeps whatever the screen copy gave it.
        }
    }

    private const int SRCCOPY = 0x00CC0020;

    /// <summary>Render the whole window, including parts drawn by the compositor.</summary>
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    [DllImport("gdi32.dll")]
    private static extern bool SetViewportOrgEx(IntPtr hdc, int x, int y, out NativeMethods.POINT previous);

    /// <summary>Include layered windows in the copy.</summary>
    private const int CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int w, int h,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);
}
