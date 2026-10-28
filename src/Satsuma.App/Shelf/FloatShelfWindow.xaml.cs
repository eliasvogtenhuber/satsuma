using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using Satsuma.Core.Model;

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
/// </summary>
public partial class FloatShelfWindow : Window
{
    private readonly ObservableCollection<ShelfEntry> _entries = new();
    private Point _pressOrigin;
    private bool _pressed;

    public FloatShelfWindow()
    {
        InitializeComponent();

        Items.ItemsSource = _entries;

        TitleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        CloseButton.MouseLeftButtonUp += (_, _) =>
        {
            _entries.Clear();
            Hide();
        };

        // Dragging a chip off the shelf hands the file to whatever it lands on.
        Items.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _pressOrigin = e.GetPosition(this);
            _pressed = true;
        };

        Items.PreviewMouseMove += OnItemsMouseMove;
        Items.PreviewMouseLeftButtonUp += (_, _) => _pressed = false;
    }

    public void Add(IReadOnlyList<SourceFile> files)
    {
        foreach (var file in files)
        {
            if (_entries.Any(e => string.Equals(e.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                continue;

            _entries.Add(new ShelfEntry(file.Path));
        }

        if (_entries.Count > 0)
        {
            Show();
            Activate();
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

        var entry = ((e.OriginalSource as FrameworkElement)?.DataContext) as ShelfEntry;
        if (entry is null) return;

        var paths = new StringCollection { entry.Path };
        var data = new DataObject();
        data.SetFileDropList(paths);

        // Copy only: the shelf is a staging area, not a move queue, and the file should
        // still be where it was when this is over.
        DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
    }
}
