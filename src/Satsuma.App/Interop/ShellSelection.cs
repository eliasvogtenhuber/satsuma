using Satsuma.App.Diagnostics;
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

    /// <summary>The selection of whichever window is in front.</summary>
    public static IReadOnlyList<SourceFile> FromForeground() =>
        FromWindow(GetForegroundWindow());

    public static IReadOnlyList<SourceFile> FromPoint(int screenX, int screenY) =>
        FromWindow(GetAncestor(WindowFromPoint(new POINT(screenX, screenY)), GA_ROOT));

    /// <summary>
    /// The selection of a specific window.
    ///
    /// Callers pass a window rather than a screen point wherever the wheel might already
    /// be covering the place of interest: the wheel is topmost, so asking what is under a
    /// point it occupies answers "the wheel".
    /// </summary>
    public static IReadOnlyList<SourceFile> FromWindow(IntPtr window)
    {
        try
        {
            if (window == IntPtr.Zero) return Array.Empty<SourceFile>();

            window = GetAncestor(window, GA_ROOT);
            if (window == IntPtr.Zero) return Array.Empty<SourceFile>();

            var className = ClassNameOf(window);
            var isDesktop = className is "Progman" or "WorkerW";

            Log.Write(
                $"selection probe: root=0x{window.ToInt64():X} " +
                $"class={className} desktop={isDesktop}");

            var found = Read(window, isDesktop);
            Log.Write($"selection probe found {found.Count} file(s)");
            return found;
        }
        catch (Exception ex)
        {
            // Shell COM is allowed to fail; the drop path will fill the wheel instead.
            // Logged rather than swallowed silently: this catch hid the real reason the
            // lookup was coming back empty for a long time.
            Log.Write($"selection probe threw: {ex.GetType().Name}: {ex.Message}");
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

            Log.Write(
                $"shell views: {count}, looking for 0x{sourceWindow.ToInt64():X}, " +
                $"apartment={Thread.CurrentThread.GetApartmentState()}");

            // Every tab of a Windows 11 Explorer window reports that window's handle, so
            // matching on the handle alone yields one view per tab. Only the tab in front
            // is the one the user means; the others sit on whatever they were last left
            // on, and reading one of those opens a wheel for a file nobody is looking at.
            //
            // The window's title is the active tab's folder, which is what tells the
            // views apart. Where that cannot be matched, nothing is reported rather than
            // something wrong.
            var activeFolder = WindowTitleOf(sourceWindow);
            Log.Write($"active tab: '{activeFolder}'");

            for (var index = 0; index < count; index++)
            {
                // Typed explicitly, not var. TryReadWindow takes a dynamic parameter, so
                // its call site binds dynamically and its result is dynamic too; reading
                // .Count off that resolved against SourceFile[] at runtime, where Count is
                // an explicit interface implementation the binder cannot see. It threw on
                // the first view that did not match, which aborted the whole scan and is
                // why this always came back empty.
                IReadOnlyList<SourceFile> paths = TryReadWindow(
                    windows, index, sourceWindow, matchHandle: true,
                    requiredFolder: activeFolder);

                if (paths.Count > 0) return paths;
            }

            // Pass two: the Desktop, whose shell view is not the Progman window the
            // pointer is actually over, so it can only be found by asking each view
            // whether it is the desktop.
            if (isDesktop)
            {
                // Ask for the desktop itself first. It is not in the Windows()
                // collection -- that is Explorer windows and browser windows -- so
                // walking the collection could only ever find an Explorer window that
                // happened to be showing the Desktop folder, and read its selection
                // instead of the desktop's. Files selected on the desktop therefore
                // never registered at all, and pressing a key over them opened an
                // empty wheel.
                IReadOnlyList<SourceFile> onDesktop = FromDesktopView(windows);
                if (onDesktop.Count > 0) return onDesktop;

                for (var index = 0; index < count; index++)
                {
                    IReadOnlyList<SourceFile> paths =
                        TryReadWindow(windows, index, sourceWindow, matchHandle: false);

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

    /// <summary>The desktop's own shell view, which is not one of the shell windows.</summary>
    private const int SwcDesktop = 8;

    /// <summary>Ask for the view object rather than just a window handle.</summary>
    private const int SwfoNeedDispatch = 1;

    /// <summary>
    /// What is selected on the desktop.
    ///
    /// IShellWindows.FindWindowSW is the documented way to reach the desktop's view:
    /// it is the one shell view that does not appear in the collection, because it is
    /// not a window anybody opened. Called through reflection rather than dynamic
    /// because two of its parameters are by-reference, which the dynamic binder will
    /// not do.
    /// </summary>
    private static IReadOnlyList<SourceFile> FromDesktopView(dynamic windows)
    {
        try
        {
            var arguments = new object?[]
            {
                Type.Missing,       // pvarLoc: no particular location
                Type.Missing,       // pvarLocRoot
                SwcDesktop,
                0,                  // out: the window handle, which we do not need
                SwfoNeedDispatch
            };

            var byReference = new System.Reflection.ParameterModifier(arguments.Length);
            byReference[3] = true;

            var view = ((object)windows).GetType().InvokeMember(
                "FindWindowSW",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: windows,
                args: arguments,
                modifiers: new[] { byReference },
                culture: null,
                namedParameters: null);

            if (view is null) return Array.Empty<SourceFile>();

            dynamic? document = ((dynamic)view).Document;
            if (document is null) return Array.Empty<SourceFile>();

            var found = PathsIn(document);
            Log.Write($"desktop view: {found.Count} selected");
            return found;
        }
        catch (Exception ex)
        {
            Log.Write($"desktop view unavailable: {ex.GetType().Name}: {ex.Message}");
            return Array.Empty<SourceFile>();
        }
    }

    /// <summary>The real files a shell view has selected.</summary>
    private static IReadOnlyList<SourceFile> PathsIn(dynamic document)
    {
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

    private static IReadOnlyList<SourceFile> TryReadWindow(
        dynamic windows, int index, IntPtr sourceWindow, bool matchHandle,
        string? requiredFolder = null)
    {
        try
        {
            dynamic? window = windows.Item(index);
            if (window is null) return Array.Empty<SourceFile>();

            if (matchHandle)
            {
                var handle = new IntPtr(Convert.ToInt64(window.HWND));

                Log.Write($"  view {index}: hwnd=0x{handle.ToInt64():X} " +
                          $"match={handle == sourceWindow}");

                if (handle != sourceWindow) return Array.Empty<SourceFile>();
            }

            dynamic? document = window.Document;
            if (document is null) return Array.Empty<SourceFile>();

            if (!matchHandle && !IsDesktopView(document)) return Array.Empty<SourceFile>();

            // Several tabs share the handle, so the folder is what identifies the one in
            // front. Without this, an inactive tab's leftover selection wins.
            if (requiredFolder is { Length: > 0 })
            {
                var folder = FolderNameOf(document);
                var path = FolderPathOf(document);

                // Either form of title: the leaf name, or the full path when Folder
                // Options is set to show one.
                var active = ExplorerTitle.NamesFolder(requiredFolder, folder)
                             || ExplorerTitle.NamesPath(requiredFolder, path);

                Log.Write($"  view {index}: folder='{folder}' path='{path}' active={active}");

                if (!active) return Array.Empty<SourceFile>();
            }

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

            if (files.Count > 0)
            {
                Log.Write($"  view {index}: {files.Count} selected");
            }

            return files;
        }
        catch (Exception ex)
        {
            // Not an Explorer view, or it went away mid-read.
            Log.Write($"  view {index}: {ex.GetType().Name} {ex.Message}");
            return Array.Empty<SourceFile>();
        }
    }

    /// <summary>The full path a shell view is showing, for a title that carries one.</summary>
    private static string FolderPathOf(dynamic document)
    {
        try
        {
            return document.Folder?.Self?.Path as string ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>The leaf folder name a shell view is showing, for matching against a tab.</summary>
    private static string FolderNameOf(dynamic document)
    {
        try
        {
            string? name = document.Folder?.Self?.Name as string;
            if (!string.IsNullOrEmpty(name)) return name;

            string? path = document.Folder?.Self?.Path as string;
            return string.IsNullOrEmpty(path)
                ? string.Empty
                : Path.GetFileName(path.TrimEnd('\\'));
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// An Explorer window's title, which is the folder name of the tab in front.
    /// </summary>
    private static string WindowTitleOf(IntPtr window)
    {
        var buffer = new System.Text.StringBuilder(512);
        return GetWindowText(window, buffer, buffer.Capacity) > 0
            ? buffer.ToString().Trim()
            : string.Empty;
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
