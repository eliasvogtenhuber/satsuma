using System.Runtime.InteropServices;
using System.Windows.Threading;
using Satsuma.App.Diagnostics;
using Satsuma.App.Settings;
using Satsuma.Core.Model;
using static Satsuma.App.Interop.NativeMethods;

namespace Satsuma.App.Interop;

public sealed record DragModifierEvent(WheelKind Wheel, int ScreenX, int ScreenY);

/// <summary>
/// Watches global input and works out when the user is dragging something and has asked
/// for a wheel.
///
/// Windows has no API for "is an OLE drag in flight", and the drag source sits in a modal
/// loop inside whichever app started it, so there is nothing to ask. What there is:
/// low-level hooks still fire during that modal loop, because they run ahead of normal
/// dispatch. So a drag is inferred the way the shell itself decides one has begun --
/// the left button is down and the pointer has travelled past the system drag threshold --
/// and a bound key pressed while that holds is a request for a wheel.
///
/// Both hooks must return promptly or Windows quietly removes them, so the callbacks only
/// touch fields and hand anything real to the dispatcher.
/// </summary>
public sealed class DragWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IReadOnlyDictionary<int, WheelKind> _keyMap;

    // Kept in fields so the GC cannot collect the delegates the hooks hold.
    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;

    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;

    /// <summary>Bound keys currently held, so auto-repeat can be told from a new press.</summary>
    private readonly HashSet<int> _heldKeys = new();

    private bool _buttonDown;
    private bool _dragging;
    private POINT _origin;
    private readonly int _thresholdX;
    private readonly int _thresholdY;

    private bool _disposed;

    /// <summary>A modifier was pressed while a drag was in flight.</summary>
    public event EventHandler<DragModifierEvent>? WheelRequested;

    /// <summary>Escape or the right button was pressed; the wheel should go away.</summary>
    public event EventHandler? Cancelled;

    /// <summary>The left button came up, so any drag is over.</summary>
    public event EventHandler? DragFinished;

    public DragWatcher(AppSettings settings, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _keyMap = settings.BuildKeyMap();

        _mouseProc = MouseHookProc;
        _keyboardProc = KeyboardHookProc;

        // The shell's own drag threshold, so Satsuma agrees with Explorer about when a
        // click has turned into a drag. Defaults are tiny (4px), hence the floor.
        _thresholdX = Math.Max(GetSystemMetrics(SM_CXDRAG), 4);
        _thresholdY = Math.Max(GetSystemMetrics(SM_CYDRAG), 4);
    }

    /// <summary>True while a wheel is on screen, which suppresses re-triggering.</summary>
    public bool WheelOpen { get; set; }

    public void Start()
    {
        if (_mouseHook != IntPtr.Zero) return;

        // Low-level hooks take a module handle but are not injected, so any valid one
        // works; passing the current module is the documented form.
        var module = GetModuleHandle(null);

        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);

        Log.Write($"hooks installed: mouse={_mouseHook != IntPtr.Zero}, " +
                  $"keyboard={_keyboardHook != IntPtr.Zero}");
        Log.Write("bound keys: " +
                  string.Join(", ", _keyMap.Select(pair => $"0x{pair.Key:X2}={pair.Value}")));
        Log.Write($"drag threshold: {_thresholdX}x{_thresholdY}");

        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "Satsuma could not install its input hooks. " +
                $"Last error: {Marshal.GetLastWin32Error()}.");
        }
    }

    public void Stop()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = (int)wParam;

            switch (message)
            {
                case WM_LBUTTONDOWN:
                {
                    var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    _origin = data.pt;
                    _buttonDown = true;
                    _dragging = false;
                    Log.Write($"button down at {_origin.X},{_origin.Y}");
                    break;
                }

                case WM_MOUSEMOVE:
                {
                    if (_buttonDown && !_dragging)
                    {
                        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        if (Math.Abs(data.pt.X - _origin.X) > _thresholdX ||
                            Math.Abs(data.pt.Y - _origin.Y) > _thresholdY)
                        {
                            _dragging = true;
                            Log.Write("drag detected");
                        }
                    }
                    break;
                }

                case WM_LBUTTONUP:
                {
                    var wasDragging = _dragging;
                    _buttonDown = false;
                    _dragging = false;
                    _heldKeys.Clear();

                    // The drop itself is reported by the wheel window, which gets the
                    // real data object. This only matters for a drag released elsewhere.
                    if (wasDragging && WheelOpen) Post(() => DragFinished?.Invoke(this, EventArgs.Empty));
                    break;
                }

                case WM_RBUTTONDOWN:
                {
                    if (WheelOpen) Post(() => Cancelled?.Invoke(this, EventArgs.Empty));
                    break;
                }
            }
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = (int)wParam;
            if (message is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var key = (int)data.vkCode;

                if (key == VK_ESCAPE)
                {
                    if (WheelOpen)
                    {
                        Post(() => Cancelled?.Invoke(this, EventArgs.Empty));
                        return Swallow;   // the drag source must not also see the Escape
                    }
                }
                else if (_keyMap.TryGetValue(key, out var wheel))
                {
                    // Holding a key makes Windows repeat its key-down about thirty times
                    // a second. Each repeat used to be treated as a fresh request for a
                    // wheel, which rebuilt it and wiped whatever was hovered. Only the
                    // first press counts; the repeats are still swallowed so nothing
                    // underneath sees them either.
                    if (!_heldKeys.Add(key)) return Swallow;

                    Log.Write($"bound key 0x{key:X2} -> {wheel}, dragging={_dragging}");

                    // A drag has to be in flight. Without that check, pressing the key
                    // anywhere in Windows would throw a wheel on screen.
                    if (_dragging && GetCursorPos(out var cursor))
                    {
                        Log.Write($"wheel requested: {wheel} at {cursor.X},{cursor.Y}");
                        Post(() => WheelRequested?.Invoke(
                            this, new DragModifierEvent(wheel, cursor.X, cursor.Y)));

                        // Swallowed so that an app being dragged over never sees the
                        // keystroke. Explorer ignores these keys mid-drag, but an
                        // arbitrary drop target might not.
                        return Swallow;
                    }
                }
            }
            else if (message is WM_KEYUP or WM_SYSKEYUP)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var key = (int)data.vkCode;

                var wasHeld = _heldKeys.Remove(key);

                // The matching key-up is swallowed too, or an app sees a release with no
                // press and can latch a modifier on.
                if (wasHeld || (_dragging && _keyMap.ContainsKey(key))) return Swallow;
            }
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    /// <summary>Returning non-zero from a hook stops the key reaching anything else.</summary>
    private static readonly IntPtr Swallow = new(1);

    /// <summary>
    /// Hands work to the UI thread. Hook callbacks run on the thread that installed the
    /// hook and are on a timer, so nothing slow may happen inside them.
    /// </summary>
    private void Post(Action action) =>
        _dispatcher.BeginInvoke(action, DispatcherPriority.Send);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
