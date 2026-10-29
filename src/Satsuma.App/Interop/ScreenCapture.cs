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
        Capture(x, y, width, height, maskOverlays: true);

    public static BitmapSource? Capture(int x, int y, int width, int height) =>
        Capture(x, y, width, height, maskOverlays: false);

    private static BitmapSource? Capture(
        int x, int y, int width, int height, bool maskOverlays)
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

            if (copied && maskOverlays)
            {
                CloneOverGhost(memoryDc, width, height);
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
    /// Covers the area the dragged file's ghost occupies by cloning neighbouring pixels
    /// over it.
    ///
    /// The ghost rides the cursor and the wheel opens on the cursor, so it always lands
    /// in the middle of the backdrop, where the blur turns it into a bright smear. It
    /// cannot be masked by finding its window: it is not a layered topmost window, and
    /// widening the search to every layered window would start painting over ordinary
    /// application windows, which are real backdrop. Windows does not publish the drag
    /// image's bounds either.
    ///
    /// So the centre is covered by size rather than by identity, and filled by copying
    /// the band of capture directly above it rather than with a flat colour, so the
    /// texture carries on through and nothing shows once blurred.
    /// </summary>
    private static void CloneOverGhost(IntPtr targetDc, int width, int height)
    {
        // Generous enough for a thumbnail and its filename label, and the hub is drawn
        // over most of it in any case.
        var maskWidth = Math.Min(width, (int)(width * 0.46));
        var maskHeight = Math.Min(height, (int)(height * 0.46));

        var left = (width - maskWidth) / 2;
        var top = (height - maskHeight) / 2;

        // Take the replacement from above the masked band, or from below when the
        // capture is too close to the top edge for that.
        var sourceTop = top - maskHeight;
        if (sourceTop < 0) sourceTop = top + maskHeight;
        if (sourceTop < 0 || sourceTop + maskHeight > height) return;

        BitBlt(targetDc, left, top, maskWidth, maskHeight,
            targetDc, left, sourceTop, SRCCOPY);
    }

    private const int SRCCOPY = 0x00CC0020;



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
