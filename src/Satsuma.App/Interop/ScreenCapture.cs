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
                MaskOverlays(memoryDc, x, y, width, height);
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
    /// Paints over any floating overlay that happens to sit within the captured area.
    ///
    /// During a drag the system paints the dragged file's ghost as a layered window that
    /// rides the cursor, and the wheel opens on the cursor, so the ghost always lands in
    /// the middle of the backdrop. Blurred, it reads as a bright smear across the glass.
    ///
    /// An overlay is not part of what is behind the wheel, so its area is filled with a
    /// tone sampled from the capture instead. The blur then has nothing to spread. This
    /// matches on the window being layered and topmost rather than on any particular
    /// class, so tooltips and similar transients are handled the same way.
    /// </summary>
    private static void MaskOverlays(IntPtr targetDc, int x, int y, int width, int height)
    {
        try
        {
            var captured = new NativeMethods.RECT
            {
                Left = x, Top = y, Right = x + width, Bottom = y + height
            };

            var fill = SampleBackdropTone(targetDc, width, height);
            var brush = CreateSolidBrush(fill);
            if (brush == IntPtr.Zero) return;

            var overlays = OverlaysIntersecting(captured);
            if (overlays.Count > 0)
            {
                Diagnostics.Log.Write(
                    $"masked {overlays.Count} overlay(s) out of the backdrop");
            }

            try
            {
                foreach (var overlay in overlays)
                {
                    // Into capture-relative coordinates, clipped to the capture.
                    var area = new NativeMethods.RECT
                    {
                        Left = Math.Max(overlay.Left, captured.Left) - x,
                        Top = Math.Max(overlay.Top, captured.Top) - y,
                        Right = Math.Min(overlay.Right, captured.Right) - x,
                        Bottom = Math.Min(overlay.Bottom, captured.Bottom) - y
                    };

                    FillRect(targetDc, ref area, brush);
                }
            }
            finally
            {
                DeleteObject(brush);
            }
        }
        catch (Exception)
        {
            // Worst case the backdrop keeps whatever the screen copy gave it.
        }
    }

    /// <summary>
    /// Visible, layered, topmost windows overlapping the captured area, excluding this
    /// process's own. Those are overlays rather than content.
    /// </summary>
    private static List<NativeMethods.RECT> OverlaysIntersecting(NativeMethods.RECT captured)
    {
        var found = new List<NativeMethods.RECT>();
        var self = (uint)Environment.ProcessId;

        NativeMethods.EnumWindows((window, _) =>
        {
            if (!NativeMethods.IsWindowVisible(window)) return true;

            var exStyle = NativeMethods.GetWindowLong(window, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0) return true;
            if ((exStyle & NativeMethods.WS_EX_TOPMOST) == 0) return true;

            NativeMethods.GetWindowThreadProcessId(window, out var owner);
            if (owner == self) return true;

            if (!NativeMethods.GetWindowRect(window, out var bounds)) return true;

            var overlaps = bounds.Left < captured.Right && bounds.Right > captured.Left
                           && bounds.Top < captured.Bottom && bounds.Bottom > captured.Top;

            if (overlaps) found.Add(bounds);
            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// A colour to paint an overlay out with, taken from the corners of the capture,
    /// which are the parts least likely to be covered by something riding the cursor.
    /// </summary>
    private static int SampleBackdropTone(IntPtr dc, int width, int height)
    {
        var inset = Math.Max(2, Math.Min(width, height) / 8);
        var points = new[]
        {
            (inset, inset),
            (width - inset, inset),
            (inset, height - inset),
            (width - inset, height - inset)
        };

        long red = 0, green = 0, blue = 0;
        var counted = 0;

        foreach (var (px, py) in points)
        {
            var colour = GetPixel(dc, px, py);
            if (colour == -1) continue;   // CLR_INVALID

            red += colour & 0xFF;
            green += (colour >> 8) & 0xFF;
            blue += (colour >> 16) & 0xFF;
            counted++;
        }

        if (counted == 0) return 0x202020;

        return (int)((red / counted) | ((green / counted) << 8) | ((blue / counted) << 16));
    }

    private const int SRCCOPY = 0x00CC0020;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int colour);

    [DllImport("gdi32.dll")]
    private static extern int GetPixel(IntPtr hdc, int x, int y);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hdc, ref NativeMethods.RECT rect, IntPtr brush);

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
