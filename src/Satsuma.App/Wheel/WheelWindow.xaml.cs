using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
        var firstShow = !IsVisible;

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
            var bounds = ComputeBounds(screenX, screenY);

            // Photograph the desktop before the window covers it, or the frosted backdrop
            // ends up being a picture of the wheel itself.
            var capture = ScreenCapture.Capture(
                bounds.Left, bounds.Top, bounds.Width, bounds.Height);

            Backdrop.Source = capture;
            BackdropDeep.Source = capture;
            _scrim = BuildScrim(capture);

            Show();
            ApplyBounds(bounds);
        }

        RebuildItems();
    }

    public void Dismiss()
    {
        _dwellTimer.Stop();
        if (IsVisible) Hide();
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

        _hoverIndex = -1;
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
    /// <summary>
    /// Picks how heavily to tint the glass, from how bright the captured backdrop is,
    /// then applies the user's own bias.
    ///
    /// One fixed translucency cannot work: the same wheel has to sit over a white document
    /// and over a dark desktop, and its labels are white either way. Measuring what it
    /// covers, and leaning on the scrim only as much as that needs, keeps the glass light
    /// where it can be and legible where it must be. GlassOpacity then shifts the whole
    /// scale to taste without flattening that behaviour.
    /// </summary>
    private Brush BuildScrim(BitmapSource? capture)
    {
        const double darkest = 0x10;    // over a dark desktop: all but absent
        const double brightest = 0x54;  // over a white window: just enough to seat the text

        var luminance = EstimateLuminance(capture) ?? 0.35;
        var baseline = darkest + (brightest - darkest) * Math.Clamp(luminance, 0, 1);

        var alpha = (byte)Math.Clamp(baseline * _settings.EffectiveGlassOpacity, 0, 255);

        return Freeze(new SolidColorBrush(Color.FromArgb(alpha, 0x06, 0x06, 0x09)));
    }

    /// <summary>Mean perceived brightness of the capture, 0..1, or null if unreadable.</summary>
    private static double? EstimateLuminance(BitmapSource? capture)
    {
        if (capture is null || capture.PixelWidth == 0 || capture.PixelHeight == 0) return null;

        try
        {
            // Scaled right down first, so this is a handful of pixels rather than a sweep
            // over the whole capture on every appearance.
            const int side = 16;
            var scaled = new TransformedBitmap(capture,
                new ScaleTransform((double)side / capture.PixelWidth,
                                   (double)side / capture.PixelHeight));

            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            converted.Freeze();

            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            if (width == 0 || height == 0) return null;

            var stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            double total = 0;
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                // Rec. 601 weights: how bright this reads, not its raw average.
                total += (0.114 * pixels[offset]       // blue
                          + 0.587 * pixels[offset + 1] // green
                          + 0.299 * pixels[offset + 2] // red
                         ) / 255.0;
            }

            return total / (width * height);
        }
        catch (Exception)
        {
            return null;
        }
    }

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

        return new Bounds(left, top, size, size);
    }

    private void ApplyBounds(Bounds bounds)
    {
        var handle = new WindowInteropHelper(this).Handle;

        // Kept so the cursor can be placed in window coordinates later without trusting
        // a routed event to say where it is.
        _bounds = bounds;
        _scale = bounds.Width > 0 ? bounds.Width / _settings.WheelDiameter : 1.0;

        SetWindowPos(handle, HWND_TOPMOST,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
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
            HubSecondary.Text = "Centre to go back";
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

        // Restart the dwell countdown for drilling into presets.
        _dwellTimer.Stop();
        _dwellCandidate = null;

        if (index >= 0 && index < _items.Count && _items[index].HasChildren)
        {
            _dwellCandidate = _items[index];
            _dwellTimer.Start();
        }
    }

    private void OnDwellElapsed(object? sender, EventArgs e)
    {
        _dwellTimer.Stop();

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

    private void OnDragOverWheel(object sender, DragEventArgs e)
    {
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
