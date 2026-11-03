using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Satsuma.App.Glass;
using Satsuma.App.Interop;
using Satsuma.App.Settings;
using Satsuma.Core.Catalog;
using Satsuma.Core.Engine;
using Satsuma.Core.Model;
using static Satsuma.App.Interop.NativeMethods;
using ShapePath = System.Windows.Shapes.Path;

namespace Satsuma.App.Wheel;

/// <summary>What the user picked, and what they picked it for.</summary>
public sealed record WheelChoice(WheelItem Item, IReadOnlyList<SourceFile> Files, FileCategory Category);

/// <summary>
/// The wheel itself: a transparent, never-activated, topmost window that appears under the
/// pointer mid-drag and accepts the drop.
///
/// Two things make it work. It never takes focus, so the drag source keeps its modal loop
/// and the drag survives. And it is only painted where it means to catch something, so the
/// OS routes the drag to Satsuma inside the circle and to whatever is underneath outside
/// it, with no need to guess what the user meant.
///
/// Slices are chosen by angle rather than by hitting the slice itself, which is what makes
/// a radial menu quick: the pointer only has to lean the right way.
/// </summary>
public partial class WheelWindow : Window
{
    private readonly JobRunner _runner;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _dwellTimer;

    private WheelKind _kind = WheelKind.Formats;
    private IReadOnlyList<SourceFile> _files = Array.Empty<SourceFile>();
    private FileCategory _category = FileCategory.Unknown;
    private IReadOnlyList<WheelItem> _items = Array.Empty<WheelItem>();

    /// <summary>Set once the real dragged paths arrive, so the guess is not re-applied.</summary>
    private bool _haveRealFiles;

    /// <summary>The ring we came from, when the user has drilled into presets.</summary>
    private readonly Stack<IReadOnlyList<WheelItem>> _parentRings = new();
    private WheelItem? _parentItem;

    private IReadOnlyList<SegmentPlacement> _placements = Array.Empty<SegmentPlacement>();
    private readonly List<ShapePath> _sliceShapes = new();
    private readonly List<FrameworkElement> _labels = new();

    private int _hoverIndex = -1;
    private WheelItem? _dwellCandidate;

    /// <summary>The pending dwell steps back out of a preset ring rather than into one.</summary>
    private bool _dwellGoesBack;

    /// <summary>Swallows the release of the click that opened a sticky wheel.</summary>
    private bool _ignoreNextMouseUp;

    /// <summary>
    /// Releasing the button over the wheel can arrive twice: once as the OLE drop and
    /// once as a plain mouse-up. Whichever lands first wins.
    /// </summary>
    private bool _committed;

    private double _outerRadius;
    private double _innerRadius;
    private Point _centre;

    public event EventHandler<WheelChoice>? Chosen;
    public event EventHandler? Dismissed;

    // Glass, layered the way a pane of frosted glass actually reads: a dark scrim to keep
    // text legible over any wallpaper, a pale wash that catches the light from above, and
    // a bright rim where the edge turns. The blurred capture sits underneath all of it.
    /// <summary>Set per appearance from the brightness of whatever the wheel covers.</summary>
    private Brush _scrim = Freeze(new SolidColorBrush(Color.FromArgb(0x5C, 0x06, 0x06, 0x09)));

    private static readonly Brush SliceFill = Freeze(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), 0.0),
            new(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF), 0.5),
            new(Color.FromArgb(0x07, 0xFF, 0xFF, 0xFF), 1.0)
        },
        new Point(0.5, 0), new Point(0.5, 1)));

    /// <summary>
    /// Selection by density: the chosen slice thickens into a brighter pane, the way a
    /// frosted surface does when there is more material in the way.
    /// </summary>
    private static readonly Brush SliceDenseFill = Freeze(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromArgb(0xC4, 0xFF, 0xFF, 0xFF), 0.0),
            new(Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF), 1.0)
        },
        new Point(0.5, 0), new Point(0.5, 1)));

    /// <summary>
    /// Selection by depth: the frost under the chosen slice goes much softer, and only a
    /// little extra film is added so the edge still reads.
    /// </summary>
    private static readonly Brush SliceDeepFill = Freeze(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromArgb(0x52, 0xFF, 0xFF, 0xFF), 0.0),
            new(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF), 1.0)
        },
        new Point(0.5, 0), new Point(0.5, 1)));

    private static readonly Brush SliceStroke = Freeze(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush SliceHoverStroke = Freeze(new SolidColorBrush(Color.FromArgb(0x96, 0xFF, 0xFF, 0xFF)));

    private static readonly Brush LabelFill = Freeze(new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7)));
    private static readonly Brush LabelHoverFill = Freeze(new SolidColorBrush(Color.FromRgb(0x24, 0x13, 0x05)));
    private static readonly Brush HintFill = Freeze(new SolidColorBrush(Color.FromArgb(0xB3, 0xDE, 0xDE, 0xDE)));
    private static readonly Brush HintHoverFill = Freeze(new SolidColorBrush(Color.FromArgb(0xC4, 0x3D, 0x1F, 0x06)));

    /// <summary>Alpha 1: invisible, but solid as far as hit testing is concerned.</summary>
    private static readonly Brush CatcherFill = Freeze(new SolidColorBrush(Color.FromArgb(0x01, 0x00, 0x00, 0x00)));

    public WheelWindow(JobRunner runner, AppSettings settings)
    {
        _runner = runner;
        _settings = settings;

        InitializeComponent();

        Width = settings.WheelDiameter;
        Height = settings.WheelDiameter;
        AllowDrop = true;

        // The deep layer keeps a fixed ratio to the base blur, so a user who softens the
        // glass does not lose the contrast that marks the selection.
        if (Backdrop.Effect is BlurEffect baseBlur)
        {
            baseBlur.Radius = settings.EffectiveGlassBlur;
        }
        if (BackdropDeep.Effect is BlurEffect deepBlur)
        {
            deepBlur.Radius = Math.Max(settings.EffectiveGlassBlur * 3.2, 36);
        }

        _dwellTimer = new DispatcherTimer
        {
            // Long enough that passing over a preset slice on the way elsewhere does not
            // drill into it, short enough to feel like it opened on its own.
            Interval = TimeSpan.FromMilliseconds(420)
        };
        _dwellTimer.Tick += OnDwellElapsed;

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Render();

        DragEnter += OnDragOverWheel;
        DragOver += OnDragOverWheel;
        DragLeave += OnDragLeaveWheel;
        Drop += OnDropOnWheel;

        MouseMove += OnMouseMoveWheel;
        MouseLeftButtonUp += OnMouseUpWheel;
    }

    /// <summary>
    /// Shows the wheel, or re-skins the open one when the user presses a different
    /// modifier mid-drag. Only the first call moves the window, so switching wheels does
    /// not yank the ring out from under the pointer.
    /// </summary>
    public void ShowFor(WheelKind kind, IReadOnlyList<SourceFile> seedFiles, int screenX, int screenY)
    {
        var firstShow = !IsVisible || _hidePending;

        if (_hidePending)
        {
            _hideGeneration++;   // the pending hide no longer applies
            _hidePending = false;
        }

        // Asking again for the wheel already on screen is not a request for anything.
        // Rebuilding on it would throw away the selection under the pointer, which is
        // what made a held key strobe the wheel.
        if (!firstShow && kind == _kind) return;

        _kind = kind;

        if (firstShow)
        {
            _haveRealFiles = false;
            _committed = false;
            _parentRings.Clear();
            _parentItem = null;
            _hoverIndex = -1;
        }

        if (!_haveRealFiles) SetFiles(seedFiles);

        if (firstShow)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var bounds = ComputeBounds(screenX, screenY);

            // Photograph the desktop before the window covers it, or the frosted backdrop
            // ends up being a picture of the wheel itself.
            // Only a drag has an overlay to work around, and avoiding it costs a
            // window redraw, so the click path keeps the cheaper plain copy.
            var capture = IsSticky
                ? ScreenCapture.Capture(bounds.Left, bounds.Top, bounds.Width, bounds.Height)
                : ScreenCapture.CaptureWithoutOverlays(
                    bounds.Left, bounds.Top, bounds.Width, bounds.Height);

            var captureMs = clock.Elapsed.TotalMilliseconds;

            Backdrop.Source = capture;
            BackdropDeep.Source = capture;
            _scrim = GlassTint.For(capture, _settings.EffectiveGlassOpacity);
            var tintMs = clock.Elapsed.TotalMilliseconds - captureMs;

            // Built before the window is shown, not after. Showing first means the first
            // painted frame is whatever the window still held from last time, which the
            // next frame then replaces: one frame of the wrong wheel, every time.
            //
            // The layout does not depend on the window being visible. It is sized in
            // device-independent units, which ApplyBounds does not change; that only
            // changes how many physical pixels those units cover.
            RebuildItems();
            var buildMs = clock.Elapsed.TotalMilliseconds - captureMs - tintMs;

            // Shown off screen, then moved into place once a frame carrying the new
            // contents has actually been composed.
            //
            // Building before showing is not enough on its own: changes to the visual
            // tree are queued and composed on the next frame, so a window shown now still
            // presents what it last rendered. That was the previous wheel -- a preset
            // ring, if the last use had drilled into one -- for a single frame.
            PositionOffScreen(bounds);
            Show();

            MoveIntoPlaceNextFrame(bounds);

            Diagnostics.Log.Write(
                $"open: capture {captureMs:0}ms, tint {tintMs:0}ms, build {buildMs:0}ms, " +
                $"show {clock.Elapsed.TotalMilliseconds - captureMs - tintMs - buildMs:0}ms, " +
                $"total {clock.Elapsed.TotalMilliseconds:0}ms");
            return;
        }

        RebuildItems();
    }

    /// <summary>
    /// True when the wheel was opened by clicking rather than by dragging, and so stays
    /// up on its own until something is picked or it is dismissed.
    /// </summary>
    public bool IsSticky { get; private set; }

    /// <summary>True once the wheel knows which files it is working on.</summary>
    public bool HasFiles => _files.Count > 0;

    /// <summary>
    /// Opens the wheel for a click rather than a drag: it stays up after the button and
    /// the key are released, and is driven by ordinary clicking.
    ///
    /// The file is whatever the click selected, which has to be read from Explorer,
    /// because unlike a drag there is no data object to ask.
    /// </summary>
    public void ShowForClick(
        WheelKind kind, IReadOnlyList<SourceFile> files,
        int screenX, int screenY, bool buttonDown = false)
    {
        if (IsVisible) return;

        IsSticky = true;

        // Only when a click is what opened the wheel, because then that click has not
        // been released yet and its release would land on the hub sitting under the
        // pointer and dismiss the wheel the instant it appeared. Opened by the key alone
        // there is no such release, and swallowing one made the first real click do
        // nothing -- which is the "you have to click twice".
        _ignoreNextMouseUp = buttonDown;

        ShowFor(kind, files, screenX, screenY);
    }

    public void Dismiss()
    {
        _dwellTimer.Stop();
        IsSticky = false;
        _ignoreNextMouseUp = false;

        if (!IsVisible) return;

        // A hidden window keeps the last surface it composed, and shows that surface
        // again the moment it is shown, before anything new can be composed. So the
        // wheel is emptied and parked off screen now, and only hidden once that empty
        // state has actually been painted. Whatever the next open presents first is then
        // nothing at all rather than the wheel that was last up.
        //
        // Done on the way out because a frame or two costs nothing here, where the same
        // wait on the way in would be felt.
        _items = Array.Empty<WheelItem>();
        _parentRings.Clear();
        _parentItem = null;
        _hoverIndex = -1;

        Render();
        PositionOffScreen(_bounds);

        HideOnceEmptied();
    }

    /// <summary>Pending hides, so a reopen can cancel one that has not fired yet.</summary>
    private int _hideGeneration;

    /// <summary>A hide is scheduled, so the window counts as closed already.</summary>
    private bool _hidePending;

    private void HideOnceEmptied()
    {
        var generation = ++_hideGeneration;
        _hidePending = true;

        // Two frames: one to compose the emptied wheel, one for it to be presented.
        var remaining = 2;

        void OnFrame(object? sender, EventArgs e)
        {
            if (generation != _hideGeneration)
            {
                CompositionTarget.Rendering -= OnFrame;
                return;
            }

            if (--remaining > 0) return;

            CompositionTarget.Rendering -= OnFrame;

            if (generation != _hideGeneration) return;

            _hidePending = false;
            Hide();
        }

        CompositionTarget.Rendering += OnFrame;
    }

    /// <summary>
    /// Builds and renders the wheel once, off-screen and invisible, so the cost of doing
    /// it the first time is not paid in front of the user.
    ///
    /// Measured, the first real open took about 1.3 seconds, over a second of which was
    /// the first Show: creating the window, loading fonts, compiling the blur, setting up
    /// a layered surface. Every later open took about 40ms. None of that work depends on
    /// what is being dragged, so it can all happen at startup, where nobody is waiting.
    /// </summary>
    public void Prewarm()
    {
        if (IsVisible) return;

        var clock = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            Opacity = 0;

            var size = (int)Math.Round(_settings.WheelDiameter * (DpiAt(new POINT(0, 0)) / 96.0));

            // Any pixels will do; this is only here to run the blur once.
            var capture = ScreenCapture.Capture(0, 0, size, size);
            Backdrop.Source = capture;
            BackdropDeep.Source = capture;
            _scrim = GlassTint.For(capture, _settings.EffectiveGlassOpacity);

            // Build the ring the real way rather than fabricating one. This is what pulls
            // in the job runner and, through it, the hunt for ffmpeg across PATH and the
            // winget install tree, which is disk work that only happens once.
            foreach (var category in new[]
                     {
                         FileCategory.Image, FileCategory.Video, FileCategory.Audio,
                         FileCategory.Archive, FileCategory.Folder, FileCategory.Subtitle
                     })
            {
                var sample = new[] { new SourceFile($"warm{category}.tmp") };

                foreach (var kind in new[] { WheelKind.Formats, WheelKind.Tools, WheelKind.Actions })
                {
                    _items = _runner.BuildWheel(kind, category, sample);
                }
            }

            // Fall back to a full ring if the last build came back empty, so the geometry
            // and label paths are exercised whatever the machine has installed.
            if (_items.Count == 0)
            {
                _items = Enumerable.Range(0, 6)
                    .Select(i => new WheelItem($"warm{i}", "WARM", WheelKind.Formats, Hint: "warming"))
                    .ToList();
            }

            Show();

            // On screen, not parked off it. A window placed far outside every monitor can
            // be skipped by the compositor, which is most of what the first show pays for.
            // Fully transparent, so there is nothing to see.
            ApplyBounds(new Bounds(0, 0, size, size));

            Render();
            UpdateLayout();

            // Let real render passes complete before tearing it back down.
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            // Touch the drag-data path too. A real drop arrives as a cross-process COM
            // data object, and the managed wrapper plus the shell clipboard formats
            // behind it are set up once, on first use. Doing it here cannot cover the
            // cross-process part, but it does cover the rest.
            try
            {
                var probe = new DataObject();
                probe.SetFileDropList(new System.Collections.Specialized.StringCollection
                {
                    Path.Combine(Path.GetTempPath(), "satsuma-warm.tmp")
                });

                if (probe.GetDataPresent(DataFormats.FileDrop)) _ = probe.GetData(DataFormats.FileDrop);
            }
            catch (Exception)
            {
                // Warming is best effort and never worth failing over.
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Write($"prewarm failed: {ex.Message}");
        }
        finally
        {
            Opacity = 1;

            _items = Array.Empty<WheelItem>();
            _hoverIndex = -1;
            _committed = false;
            Backdrop.Source = null;
            BackdropDeep.Source = null;

            // Render the emptiness, park off screen, and hide only once that has been
            // painted, or the warm-up ring stays as the surface the first open presents.
            Render();
            PositionOffScreen(_bounds);
            HideOnceEmptied();

            Diagnostics.Log.Write($"prewarm: {clock.Elapsed.TotalMilliseconds:0}ms");
        }
    }

    /// <summary>
    /// Applies a guessed selection that arrived after the wheel was already up, and only
    /// while the wheel still has nothing better. Reading the Explorer selection costs
    /// enough that it cannot be done before showing.
    /// </summary>
    public void SeedLate(IReadOnlyList<SourceFile> files)
    {
        if (!IsVisible || _haveRealFiles || files.Count == 0) return;
        if (_files.Count > 0) return;

        SetFiles(files);
        RebuildItems();
    }

    private void SetFiles(IReadOnlyList<SourceFile> files)
    {
        _files = files;
        _category = FileTypeResolver.ResolveSelection(files);
    }

    /// <summary>Rebuilds the ring for the current wheel, selection and drill level.</summary>
    private void RebuildItems()
    {
        _items = _parentItem is not null
            ? _parentItem.Children
            : _runner.BuildWheel(_kind, _category, _files);

        // Keep whatever is under the pointer if it still exists. A rebuild happens when
        // the real dragged paths arrive, and dropping the selection there would blink the
        // pane out for a frame just as the user is aiming at it.
        if (_hoverIndex >= _items.Count) _hoverIndex = -1;

        Render();
        UpdateHub();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;

        // NOACTIVATE is the whole trick: without it, showing this window would steal
        // focus from the drag source and the drag would be cancelled out from under us.
        // TOOLWINDOW keeps it out of Alt-Tab.
        var exStyle = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// Centres the wheel on the pointer, nudged inwards so it never hangs off the screen.
    private readonly record struct Bounds(int Left, int Top, int Width, int Height);

    /// <summary>
    /// Works out where the wheel goes, before the window exists. The backdrop capture
    /// needs the rectangle up front, so this cannot wait for a window handle to ask about
    /// DPI, and reads it from the monitor under the pointer instead.
    /// </summary>
    private Bounds ComputeBounds(int screenX, int screenY)
    {
        var cursor = new POINT(screenX, screenY);
        var scale = DpiAt(cursor) / 96.0;
        var size = (int)Math.Round(_settings.WheelDiameter * scale);

        var left = screenX - size / 2;
        var top = screenY - size / 2;

        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>()
        };

        if (GetMonitorInfo(monitor, ref info))
        {
            var work = info.rcWork;
            left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - size));
            top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - size));
        }

        Diagnostics.Log.Write(
            $"wheel bounds: {left},{top} {size}x{size} " +
            $"({_settings.WheelDiameter:0} at {scale:0.##}x)");

        return new Bounds(left, top, size, size);
    }

    /// <summary>
    /// Somewhere unseen to paint a frame, just below the monitor the wheel is about to
    /// appear on.
    ///
    /// Parking in a far corner of the coordinate space is the obvious way to do this and
    /// is wrong on a mixed-DPI desktop: that corner belongs to whichever monitor is
    /// nearest to it, so the window changes DPI on the way out and again on the way back,
    /// and Windows rescales it both times. Directly below its own monitor is still off
    /// every screen, but it is that monitor the window stays nearest to, so the DPI never
    /// moves.
    /// </summary>
    private static (int Left, int Top) ParkBelow(Bounds bounds)
    {
        var centre = new POINT(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var monitor = MonitorFromPoint(centre, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>()
        };

        return GetMonitorInfo(monitor, ref info)
            ? (bounds.Left, info.rcMonitor.Bottom + 8)
            : (-32000, -32000);
    }

    /// <summary>
    /// Puts the window at its final size but off screen, while recording the bounds it is
    /// about to occupy so that hit testing already refers to where it will be.
    /// </summary>
    private void PositionOffScreen(Bounds bounds)
    {
        var handle = new WindowInteropHelper(this).Handle;

        _bounds = bounds;
        _scale = bounds.Width > 0 ? bounds.Width / _settings.WheelDiameter : 1.0;

        var (left, top) = ParkBelow(bounds);

        WindowPlacement.Apply(handle, left, top, bounds.Width, bounds.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW, "wheel park");
    }

    private void ApplyBounds(Bounds bounds)
    {
        var handle = new WindowInteropHelper(this).Handle;

        // Kept so the cursor can be placed in window coordinates later without trusting
        // a routed event to say where it is.
        _bounds = bounds;
        _scale = bounds.Width > 0 ? bounds.Width / _settings.WheelDiameter : 1.0;

        WindowPlacement.Apply(handle, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW, "wheel");
    }

    /// <summary>
    /// Moves the window to where it belongs after one frame has been composed, so what
    /// first appears on screen is this wheel and not the last one.
    /// </summary>
    private void MoveIntoPlaceNextFrame(Bounds bounds)
    {
        void OnFrame(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnFrame;

            // Dismissed again within the frame; nothing to place.
            if (!IsVisible) return;

            ApplyBounds(bounds);
        }

        CompositionTarget.Rendering += OnFrame;
    }

    private Bounds _bounds;
    private double _scale = 1.0;

    // ---- Drawing ----------------------------------------------------------

    private void Render()
    {
        SegmentLayer.Children.Clear();
        _sliceShapes.Clear();
        _labels.Clear();

        var width = RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : Width;
        var height = RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : Height;
        if (width <= 0 || height <= 0) return;

        _centre = new Point(width / 2, height / 2);
        _outerRadius = Math.Min(width, height) / 2 - 14;
        _innerRadius = Hub.Width / 2 + 16;

        Diagnostics.Log.Write(
            $"render: size={width:0.#}x{height:0.#} centre={_centre.X:0.#},{_centre.Y:0.#} " +
            $"inner={_innerRadius:0.#} outer={_outerRadius:0.#} items={_items.Count}");

        if (_outerRadius <= _innerRadius) return;

        // The catcher is what the OS sees, so the whole disc is one hit target and a pane
        // can be chosen by leaning towards it rather than reaching it. It is invisible
        // but solid, which is also what keeps the gaps between panes live.
        var catcher = new ShapePath
        {
            Data = new EllipseGeometry(_centre, _outerRadius, _outerRadius),
            Fill = CatcherFill,
            AllowDrop = true
        };
        SegmentLayer.Children.Add(catcher);

        // The hub is glass too, so it joins the union and gets the same treatment.
        var hubGeometry = new EllipseGeometry(_centre, Hub.Width / 2, Hub.Height / 2);
        HubBase.Fill = _scrim;
        HubFilm.Fill = SliceFill;

        if (_items.Count == 0)
        {
            ApplyGlassShape(new GeometryGroup { Children = { hubGeometry } });
            UpdateHub();
            return;
        }

        _placements = WheelGeometry.Place(_items.Count, _innerRadius, _outerRadius);

        // Everything the glass covers, so the backdrop and the shadow follow the panes
        // instead of a circle enclosing them.
        var union = new GeometryGroup { Children = { hubGeometry } };

        for (var index = 0; index < _items.Count; index++)
        {
            var placement = _placements[index];
            var geometry = WheelGeometry.BuildSegment(_centre, placement);
            union.Children.Add(geometry);

            // The scrim sits per pane rather than over a whole disc, so the gaps show
            // the desktop rather than a tinted halo.
            SegmentLayer.Children.Add(new ShapePath
            {
                Data = geometry,
                Fill = _scrim,
                IsHitTestVisible = false
            });

            var slice = new ShapePath
            {
                Data = geometry,
                Fill = SliceFill,
                Stroke = SliceStroke,
                StrokeThickness = 1,
                // The catcher handles hit testing for the whole disc.
                IsHitTestVisible = false
            };

            SegmentLayer.Children.Add(slice);
            _sliceShapes.Add(slice);

            var label = BuildLabel(_items[index], placement);
            SegmentLayer.Children.Add(label);
            _labels.Add(label);
        }

        ApplyGlassShape(union);
        ApplyHover(_hoverIndex);
    }

    /// <summary>
    /// Points the frost and the shadow at the shape the glass actually occupies.
    ///
    /// Clipping to the union rather than to a circle is what removes the halo: with a
    /// disc, the rounded corners of each pane leave crescents of tinted backdrop around
    /// them, which read as a ring pressing in on the slices.
    /// </summary>
    private void ApplyGlassShape(Geometry union)
    {
        Backdrop.Clip = union;
        ShadowShape.Data = union;
    }

    /// <summary>
    /// A soft dark halo behind label text.
    ///
    /// This is what lets the glass be as thin as it is. Without it, readability would have
    /// to come from the scrim, and the scrim would have to be heavy enough for the worst
    /// background the wheel might land on. Shadowing the text instead keeps that cost on
    /// the few hundred pixels that actually need it.
    /// </summary>
    private static Effect BuildTextShadow() => new DropShadowEffect
    {
        BlurRadius = 5,
        ShadowDepth = 0,
        Opacity = 0.85,
        Color = Colors.Black
    };

    private FrameworkElement BuildLabel(WheelItem item, SegmentPlacement placement)
    {
        var stack = new StackPanel { IsHitTestVisible = false, Effect = BuildTextShadow() };

        var title = new TextBlock
        {
            Text = item.Label,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = LabelFill,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stack.Children.Add(title);

        if (!string.IsNullOrEmpty(item.Hint))
        {
            stack.Children.Add(new TextBlock
            {
                Text = item.Hint,
                FontSize = 9,
                Foreground = HintFill,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = 80,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        if (item.HasChildren)
        {
            // A quiet cue that leaning here opens more, rather than acting.
            stack.Children.Add(new TextBlock
            {
                Text = "⋯",
                FontSize = 11,
                Foreground = HintFill,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

        // Measure so the label can be centred on its slice rather than hung off a corner.
        stack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = stack.DesiredSize;

        var anchor = WheelGeometry.PointOnCircle(_centre, placement.MidRadius, placement.MidAngle);
        Canvas.SetLeft(stack, anchor.X - desired.Width / 2);
        Canvas.SetTop(stack, anchor.Y - desired.Height / 2);

        return stack;
    }

    /// <summary>
    /// Shows where the results are headed, while a key is saying so.
    ///
    /// Live rather than sampled: the mark appears the moment the key goes down and
    /// leaves the moment it comes up, so what is on screen is always what would happen
    /// if the file were dropped now.
    /// </summary>
    public void ShowDestination(Destination destination)
    {
        var glyph = ModeGlyphs.For(destination);

        ModeGlyph.Data = glyph;
        ModeGlyph.Visibility = glyph is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateHub()
    {
        if (_hoverIndex >= 0 && _hoverIndex < _items.Count)
        {
            var item = _items[_hoverIndex];
            HubPrimary.Text = item.Label;
            HubSecondary.Text = item.HasChildren ? "Hold to open" : DescribeSelection();
            return;
        }

        if (_parentItem is not null)
        {
            HubPrimary.Text = _parentItem.Label;
            HubSecondary.Text = IsSticky ? "Click here to go back" : "Rest here to go back";
            return;
        }

        HubPrimary.Text = _items.Count == 0 ? "Nothing to do" : WheelTitle();
        HubSecondary.Text = _items.Count == 0
            ? DescribeUnsupported()
            : DescribeSelection();
    }

    private string WheelTitle() => _kind switch
    {
        WheelKind.Formats => "Convert to",
        WheelKind.Tools => "Tools",
        WheelKind.Actions => "Actions",
        _ => "Satsuma"
    };

    private string DescribeSelection()
    {
        if (_files.Count == 0) return "Drop a file here";
        if (_files.Count == 1) return _files[0].Name;

        var noun = _category == FileCategory.Unknown ? "items" : _category.ToString().ToLowerInvariant() + "s";
        return $"{_files.Count} {noun}";
    }

    private string DescribeUnsupported()
    {
        if (_files.Count == 0) return "Drop a file here";

        if (_category is FileCategory.Video or FileCategory.Audio
            && !Core.Tools.FfmpegLocator.IsAvailable)
        {
            return "Install ffmpeg for this";
        }

        return _category switch
        {
            FileCategory.Unknown when _files.Count > 1 => "Mixed selection",
            FileCategory.Pdf or FileCategory.Document or FileCategory.Slides
                or FileCategory.Spreadsheet => "Not supported yet",
            _ => "Nothing available for this file"
        };
    }

    // ---- Hit testing -----------------------------------------------------

    /// <summary>
    /// Which slice the pointer is leaning towards, or -1 for the hub, which means cancel
    /// or go back.
    /// </summary>
    private int IndexAt(Point position) => WheelHitTest.IndexAt(
        position.X - _centre.X,
        position.Y - _centre.Y,
        _innerRadius,
        _outerRadius,
        _items.Count,
        _hoverIndex);

    private void ApplyHover(int index)
    {
        var byDensity = _settings.SelectionStyle == SelectionStyle.Density;

        // Deepening the frost only applies to the chosen pane, so the harder blur is
        // clipped to it and clipped away to nothing when there is no selection.
        //
        // Clipped rather than collapsed on purpose: collapsing throws away the bitmap
        // cache, so the hard blur would be recomputed on the next hover. Moving the clip
        // on a cached layer costs nothing.
        BackdropDeep.Clip = !byDensity && index >= 0 && index < _sliceShapes.Count
            ? _sliceShapes[index].Data
            : Geometry.Empty;

        for (var i = 0; i < _sliceShapes.Count; i++)
        {
            var hovered = i == index;

            _sliceShapes[i].Fill = hovered
                ? (byDensity ? SliceDenseFill : SliceDeepFill)
                : SliceFill;

            _sliceShapes[i].Stroke = hovered ? SliceHoverStroke : SliceStroke;

            if (_labels[i] is not StackPanel stack) continue;

            // A dense white pane needs dark text on it; a deepened frost stays dark
            // enough underneath to keep the white.
            var darkText = hovered && byDensity;

            for (var child = 0; child < stack.Children.Count; child++)
            {
                if (stack.Children[child] is not TextBlock text) continue;

                text.Foreground = child == 0
                    ? (darkText ? LabelHoverFill : LabelFill)
                    : (darkText ? HintHoverFill : HintFill);
            }
        }
    }

    private void SetHover(int index)
    {
        if (index == _hoverIndex) return;

        Diagnostics.Log.Write($"hover {_hoverIndex} -> {index}");

        _hoverIndex = index;
        ApplyHover(index);
        UpdateHub();

        // Restart the dwell countdown, for drilling into presets or backing out of them.
        _dwellTimer.Stop();
        _dwellCandidate = null;
        _dwellGoesBack = false;

        if (index >= 0 && index < _items.Count && _items[index].HasChildren)
        {
            _dwellCandidate = _items[index];
            _dwellTimer.Start();
        }
        else if (index < 0 && _parentRings.Count > 0)
        {
            // Going back has to work on hover, for the same reason going in does: the
            // button is still held, and releasing it to click the hub would end the drag
            // rather than step back out. Resting in the hub is the way out.
            _dwellGoesBack = true;
            _dwellTimer.Start();
        }
    }

    private void OnDwellElapsed(object? sender, EventArgs e)
    {
        _dwellTimer.Stop();

        if (_dwellGoesBack)
        {
            _dwellGoesBack = false;
            GoBack();
            return;
        }

        if (_dwellCandidate is null || !_dwellCandidate.HasChildren) return;

        _parentRings.Push(_items);
        _parentItem = _dwellCandidate;
        _dwellCandidate = null;

        RebuildItems();
    }

    private void GoBack()
    {
        if (_parentRings.Count == 0) return;

        _parentRings.Pop();
        _parentItem = null;
        RebuildItems();
    }

    // ---- Drag and drop ---------------------------------------------------

    /// <summary>Set once the first drag event of the process has been timed.</summary>
    private static bool _timedFirstDragEvent;

    private void OnDragOverWheel(object sender, DragEventArgs e)
    {
        if (!_timedFirstDragEvent)
        {
            _timedFirstDragEvent = true;

            // The first drag event in a process is where the cross-process data object is
            // first marshalled, which no amount of warming inside this process can cover.
            // Timed so it can be ruled in or out rather than argued about.
            var firstClock = System.Diagnostics.Stopwatch.StartNew();
            AdoptDraggedFiles(e);
            Diagnostics.Log.Write(
                $"first drag event handled in {firstClock.Elapsed.TotalMilliseconds:0}ms, " +
                $"files={_files.Count}");

            SetHover(IndexAt(e.GetPosition(RootGrid)));
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        AdoptDraggedFiles(e);

        SetHover(IndexAt(e.GetPosition(RootGrid)));

        // Always report Copy. Satsuma writes new files and never touches the original,
        // whatever Explorer would have done with this modifier held.
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDragLeaveWheel(object sender, DragEventArgs e)
    {
        e.Handled = true;

        // DragLeave does not mean what it sounds like. WPF raises it on the window
        // whenever the drag crosses between hit-testable elements inside it, and it
        // bubbles, so re-rendering the selected pane is enough to produce one. Clearing
        // the selection on every DragLeave made the pane drop out and come straight back
        // on the next DragOver, over and over, for a pointer that never moved.
        //
        // So the pointer is asked where it actually is, and the selection is only given
        // up when it has really left.
        if (PointerIsOnWheel())
        {
            Diagnostics.Log.Write("drag leave ignored: pointer still on a pane");
            return;
        }

        Diagnostics.Log.Write("drag leave: pointer has left");
        SetHover(-1);
    }

    /// <summary>True when a screen point falls inside the wheel at all.</summary>
    public bool HitTestScreen(int screenX, int screenY)
    {
        if (!IsVisible || _scale <= 0) return false;

        var dx = (screenX - _bounds.Left) / _scale - _centre.X;
        var dy = (screenY - _bounds.Top) / _scale - _centre.Y;

        return Math.Sqrt(dx * dx + dy * dy) <= _outerRadius + 4;
    }

    /// <summary>
    /// True when the system cursor is over a pane right now, independent of whatever
    /// routed event is being handled.
    /// </summary>
    private bool PointerIsOnWheel()
    {
        if (_scale <= 0) return false;
        if (!GetCursorPos(out var cursor)) return false;

        // Window-relative, in the device-independent units the geometry is built in.
        var local = new Point(
            (cursor.X - _bounds.Left) / _scale,
            (cursor.Y - _bounds.Top) / _scale);

        return IndexAt(local) >= 0;
    }

    private void OnDropOnWheel(object sender, DragEventArgs e)
    {
        AdoptDraggedFiles(e);

        var index = IndexAt(e.GetPosition(RootGrid));
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;

        Commit(index);
    }

    private void OnMouseMoveWheel(object sender, MouseEventArgs e)
    {
        if (!IsVisible) return;
        SetHover(IndexAt(e.GetPosition(RootGrid)));
    }

    /// <summary>
    /// Covers the case where the wheel is open and the user clicks a slice rather than
    /// dropping on it, which happens because the wheel stays up after the key is released.
    /// </summary>
    private void OnMouseUpWheel(object sender, MouseButtonEventArgs e)
    {
        if (!IsVisible) return;

        if (_ignoreNextMouseUp)
        {
            // The release of the click that opened this wheel.
            _ignoreNextMouseUp = false;
            return;
        }

        Commit(IndexAt(e.GetPosition(RootGrid)));
    }

    private void Commit(int index)
    {
        if (_committed) return;
        _dwellTimer.Stop();

        // The hub means back when drilled in, cancel otherwise.
        if (index < 0)
        {
            if (_parentRings.Count > 0)
            {
                GoBack();
                return;
            }

            _committed = true;
            Dismiss();
            Dismissed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (index >= _items.Count) return;

        var item = _items[index];

        // Dropping on a preset group opens it rather than guessing which preset was meant.
        if (item.HasChildren)
        {
            _parentRings.Push(_items);
            _parentItem = item;
            RebuildItems();
            return;
        }

        if (_files.Count == 0)
        {
            _committed = true;
            Dismiss();
            Dismissed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var choice = new WheelChoice(item, _files, _category);

        _committed = true;
        Dismiss();
        Chosen?.Invoke(this, choice);
    }

    /// <summary>
    /// Replaces the guessed selection with the paths actually being dragged, the first
    /// time the drag data is available. This is what makes the wheel correct for drags
    /// that did not start in an Explorer window.
    /// </summary>
    private void AdoptDraggedFiles(DragEventArgs e)
    {
        if (_haveRealFiles) return;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        var files = paths
            .Where(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
            .Select(p => new SourceFile(p))
            .ToList();

        if (files.Count == 0) return;

        _haveRealFiles = true;

        var sameAsGuess = files.Count == _files.Count
                          && files.Select(f => f.Path)
                              .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                              .SequenceEqual(_files.Select(f => f.Path)
                                  .OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
                                  StringComparer.OrdinalIgnoreCase);

        if (sameAsGuess) return;

        // The guess was wrong, so start the ring over from the real selection. Any drill
        // level belonged to the old ring.
        _parentRings.Clear();
        _parentItem = null;

        SetFiles(files);
        RebuildItems();
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
