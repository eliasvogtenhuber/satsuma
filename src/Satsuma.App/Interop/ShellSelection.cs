using Satsuma.Core.Model;
using static Satsuma.App.Interop.NativeMethods;

namespace Satsuma.App.Interop;

/// <summary>
/// Reads the current selection out of an Explorer window or the Desktop.
///
/// Why this exists: the wheel has to appear the moment the modifier goes down, but the
/// dragged file paths only arrive later, in the drop target's DragEnter, once the pointer
/// reaches the wheel. Waiting for that would mean drawing a wheel with nothing in it and
/// then rearranging it under the user's pointer.
///
/// So the selection is read straight from the shell view the drag started in, which is
/// what the user is dragging in the overwhelmingly common case. It is a guess, and it is
/// treated as one: the wheel rebuilds from the real data object on DragEnter, which is
/// also what covers drags from anywhere else, like a mail attachment or another app.
/// </summary>
public static class ShellSelection
{
    /// <summary>
    /// Best guess at what is being dragged, given where the drag started.
    /// Empty when the source is not a shell view, which is not an error.
    /// </summary>
    /// <summary>
    /// Looks where the drag began, then where the pointer is now.
    ///
    /// The origin is what matters: by the time a wheel is asked for, the pointer has left
    /// the window the files came from, so asking what is selected under it finds nothing.
    /// The current point is kept as a fallback for the case where a wheel is opened
    /// without a drag at all.
    /// </summary>
    public static IReadOnlyList<SourceFile> ForDrag(
        int originX, int originY, int pointerX, int pointerY)
    {
        var atOrigin = FromPoint(originX, originY);
        if (atOrigin.Count > 0) return atOrigin;

        return originX == pointerX && originY == pointerY
            ? atOrigin
            : FromPoint(pointerX, pointerY);
    }

    public static IReadOnlyList<SourceFile> FromPoint(int screenX, int screenY)
    {
        try
        {
            var window = GetAncestor(WindowFromPoint(new POINT(screenX, screenY)), GA_ROOT);
            if (window == IntPtr.Zero) return Array.Empty<SourceFile>();

            var className = ClassNameOf(window);
            var isDesktop = className is "Progman" or "WorkerW";

            return Read(window, isDesktop);
        }
        catch (Exception)
        {
            // Shell COM is allowed to fail; the drop path will fill the wheel instead.
            return Array.Empty<SourceFile>();
        }
    }

    private static IReadOnlyList<SourceFile> Read(IntPtr sourceWindow, bool isDesktop)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return Array.Empty<SourceFile>();

        dynamic? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return Array.Empty<SourceFile>();

            dynamic windows = shell.Windows();
            int count = windows.Count;

            // Pass one: the shell view whose window is the one the drag began in.
            for (var index = 0; index < count; index++)
            {
                var paths = TryReadWindow(windows, index, sourceWindow, matchHandle: true);
                if (paths.Count > 0) return paths;
            }

            // Pass two: the Desktop, whose shell view is not the Progman window the
            // pointer is actually over, so it can only be found by asking each view
            // whether it is the desktop.
            if (isDesktop)
            {
                for (var index = 0; index < count; index++)
                {
                    var paths = TryReadWindow(windows, index, sourceWindow, matchHandle: false);
                    if (paths.Count > 0) return paths;
                }
            }

            return Array.Empty<SourceFile>();
        }
        finally
        {
            if (shell is not null) ReleaseCom(shell);
        }
    }

    private static IReadOnlyList<SourceFile> TryReadWindow(
        dynamic windows, int index, IntPtr sourceWindow, bool matchHandle)
    {
        try
        {
            dynamic? window = windows.Item(index);
            if (window is null) return Array.Empty<SourceFile>();

            if (matchHandle)
            {
                var handle = new IntPtr(Convert.ToInt64(window.HWND));
                if (handle != sourceWindow) return Array.Empty<SourceFile>();
            }

            dynamic? document = window.Document;
            if (document is null) return Array.Empty<SourceFile>();

            if (!matchHandle && !IsDesktopView(document)) return Array.Empty<SourceFile>();

            dynamic? selected = document.SelectedItems();
            if (selected is null) return Array.Empty<SourceFile>();

            var files = new List<SourceFile>();
            foreach (dynamic item in selected)
            {
                string? path = item?.Path as string;

                // Virtual items -- Recycle Bin entries, library roots, search results --
                // have a display path that is not a real file.
                if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                {
                    files.Add(new SourceFile(path));
                }
            }

            return files;
        }
        catch (Exception)
        {
            // Not an Explorer view, or it went away mid-read.
            return Array.Empty<SourceFile>();
        }
    }

    private static bool IsDesktopView(dynamic document)
    {
        try
        {
            string? folderPath = document.Folder?.Self?.Path as string;
            if (string.IsNullOrEmpty(folderPath)) return false;

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

            return string.Equals(folderPath.TrimEnd('\\'), desktop.TrimEnd('\\'),
                       StringComparison.OrdinalIgnoreCase)
                   || string.Equals(folderPath.TrimEnd('\\'), publicDesktop.TrimEnd('\\'),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void ReleaseCom(object instance)
    {
        try
        {
            if (System.Runtime.InteropServices.Marshal.IsComObject(instance))
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(instance);
            }
        }
        catch (Exception)
        {
            // Nothing useful to do if the release fails.
        }
    }
}
