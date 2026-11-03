using static Satsuma.App.Interop.NativeMethods;

namespace Satsuma.App.Interop;

/// <summary>
/// Puts a window exactly where it was asked to go, across monitors of different DPI.
///
/// Satsuma's windows are sized in physical pixels for the monitor they are about to
/// appear on, because the backdrop behind them is a photograph of that monitor and has
/// to line up pixel for pixel. Windows does not leave that size alone. A window that
/// crosses a DPI boundary gets a WM_DPICHANGED with a suggested rectangle scaled by the
/// ratio between the two monitors, WPF accepts it, and the window arrives at the wrong
/// size -- 1.6x too large going from a 125% monitor to a 200% one, and the same fraction
/// too small coming back. That happens inside the SetWindowPos call, so by the time it
/// returns the damage is already done and a second ask settles it.
/// </summary>
internal static class WindowPlacement
{
    /// <summary>
    /// Moves and sizes a window, and insists on the size if Windows rescaled it on the
    /// way. <paramref name="what"/> names the window in the log when that happens.
    /// </summary>
    internal static void Apply(
        IntPtr handle, int left, int top, int width, int height, uint flags, string what)
    {
        SetWindowPos(handle, HWND_TOPMOST, left, top, width, height, flags);

        if (!GetWindowRect(handle, out var landed)) return;

        var landedWidth = landed.Right - landed.Left;
        var landedHeight = landed.Bottom - landed.Top;

        if (landedWidth == width && landedHeight == height) return;

        Diagnostics.Log.Write(
            $"{what} rescaled on landing: {landedWidth}x{landedHeight}, " +
            $"asking again for {width}x{height}");

        SetWindowPos(handle, HWND_TOPMOST, left, top, width, height, flags);
    }
}
