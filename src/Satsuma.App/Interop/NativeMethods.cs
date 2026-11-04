using System.Runtime.InteropServices;

namespace Satsuma.App.Interop;

/// <summary>P/Invoke surface. Nothing in here holds state.</summary>
internal static class NativeMethods
{
    // ---- Hooks -------------------------------------------------------------

    internal const int WH_KEYBOARD_LL = 13;
    internal const int WH_MOUSE_LL = 14;

    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_KEYUP = 0x0101;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const int WM_SYSKEYUP = 0x0105;

    internal const int WM_MOUSEMOVE = 0x0200;
    internal const int WM_LBUTTONDOWN = 0x0201;
    internal const int WM_LBUTTONUP = 0x0202;
    internal const int WM_RBUTTONDOWN = 0x0204;

    internal delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(
        int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // ---- Input state -------------------------------------------------------

    internal const int VK_LBUTTON = 0x01;
    internal const int VK_SHIFT = 0x10;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_MENU = 0x12;       // Alt
    internal const int VK_RETURN = 0x0D;
    internal const int VK_SPACE = 0x20;
    internal const int VK_ESCAPE = 0x1B;
    internal const int VK_LSHIFT = 0xA0;
    internal const int VK_RSHIFT = 0xA1;
    internal const int VK_LCONTROL = 0xA2;
    internal const int VK_RCONTROL = 0xA3;
    internal const int VK_LMENU = 0xA4;
    internal const int VK_RMENU = 0xA5;

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    internal static bool IsKeyDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    // ---- Windows ----------------------------------------------------------

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_TRANSPARENT = 0x00000020;
    internal const int WS_EX_TOPMOST = 0x00000008;
    internal const int WS_EX_LAYERED = 0x00080000;

    internal static readonly IntPtr HWND_TOPMOST = new(-1);

    internal const int SW_HIDE = 0;

    /// <summary>
    /// Hides a window without WPF knowing, so it can be taken out of a screen capture
    /// and put back in the same breath. Window.Hide() would tear down more than that and
    /// is visible as a flicker; this is a single call either way.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    // ---- Working set ------------------------------------------------------

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSizeEx(
        IntPtr process, IntPtr minimum, IntPtr maximum, uint flags);

    private const uint QuotaLimitsHardWorkingSetMinEnable = 0x00000001;
    private const uint QuotaLimitsHardWorkingSetMaxDisable = 0x00000008;

    /// <summary>
    /// Asks Windows not to trim this process below a floor.
    ///
    /// Satsuma spends nearly all its time doing nothing, and an idle process has its
    /// working set trimmed, so the pages holding the wheel and its compiled code go out
    /// to disk. The next drag then faults them all back in, which is felt as lag on the
    /// first use after a quiet spell and on no use after that.
    ///
    /// A floor rather than a pin: the maximum stays unbounded, so this asks to keep what
    /// is already resident rather than to reserve anything.
    /// </summary>
    internal static bool KeepWorkingSetResident(long minimumBytes, long maximumBytes)
    {
        return SetProcessWorkingSetSizeEx(
            GetCurrentProcess(),
            new IntPtr(minimumBytes),
            new IntPtr(maximumBytes),
            QuotaLimitsHardWorkingSetMinEnable | QuotaLimitsHardWorkingSetMaxDisable);
    }

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    internal const uint GA_ROOT = 2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    internal struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    /// <summary>
    /// The control with keyboard focus inside another process's window.
    ///
    /// GetFocus only answers for the calling thread, so a tray app asking what has
    /// focus in Explorer gets nothing. This asks about Explorer's own input thread.
    /// </summary>
    internal static IntPtr FocusedControlIn(IntPtr window)
    {
        if (window == IntPtr.Zero) return IntPtr.Zero;

        var thread = GetWindowThreadProcessId(window, IntPtr.Zero);
        if (thread == 0) return IntPtr.Zero;

        var info = new GUITHREADINFO();
        info.cbSize = Marshal.SizeOf<GUITHREADINFO>();

        return GetGUIThreadInfo(thread, ref info) ? info.hwndFocus : IntPtr.Zero;
    }

    internal static string ClassNameOf(IntPtr hWnd)
    {
        var buffer = new char[256];
        var length = GetClassName(hWnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    // ---- Monitors and DPI -------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// The DPI of the monitor at a point, which is needed before a window exists to ask
    /// <see cref="GetDpiForWindow"/> about.
    /// </summary>
    internal static uint DpiAt(POINT point)
    {
        try
        {
            var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
            const int MDT_EFFECTIVE_DPI = 0;

            if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX;
            }
        }
        catch (DllNotFoundException)
        {
            // shcore is present on everything Satsuma supports, but never crash over DPI.
        }
        catch (EntryPointNotFoundException)
        {
        }

        return 96;
    }

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    internal const int SM_CXDRAG = 68;
    internal const int SM_CYDRAG = 69;

    // ---- Shell -----------------------------------------------------------

    [DllImport("shell32.dll", SetLastError = true)]
    internal static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder, uint cidl, IntPtr[]? apidl, uint dwFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr ILCreateFromPath(string pszPath);

    [DllImport("shell32.dll")]
    internal static extern void ILFree(IntPtr pidl);
}
