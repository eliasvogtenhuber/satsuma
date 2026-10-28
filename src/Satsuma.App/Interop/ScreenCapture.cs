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
    public static BitmapSource? Capture(int x, int y, int width, int height)
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

            // CAPTUREBLT would include layered windows above us, which is the opposite of
            // what the backdrop should show.
            var copied = BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, SRCCOPY);

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

    private const int SRCCOPY = 0x00CC0020;

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
