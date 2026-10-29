namespace Satsuma.Core.Model;

/// <summary>
/// Reading an Explorer window title to find out which tab is in front.
///
/// Every tab of a Windows 11 Explorer window reports the same window handle, so a handle
/// alone cannot say which tab the user is looking at. The title can, because the active
/// tab's folder always comes first in it. That matters because an inactive tab sits on
/// whatever selection it was last left with, and acting on one of those means acting on a
/// file nobody is looking at.
///
/// Pure string work, kept out of the shell plumbing so it can be tested against real
/// titles rather than by driving Explorer around.
/// </summary>
public static class ExplorerTitle
{
    /// <summary>
    /// Whether <paramref name="title"/> names <paramref name="folder"/> as its active tab.
    ///
    /// The title is decorated, and decorated differently in every display language:
    /// "Pictures - File Explorer", or with several tabs, "Pictures and 3 more tabs -
    /// File Explorer", and in German "Bilder und 3 weitere Registerkarten - Explorer".
    /// What holds in all of them is that the folder comes first, so this matches on that
    /// prefix instead of parsing separators that change with the language.
    /// </summary>
    public static bool NamesFolder(string? title, string? folder)
    {
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(folder)) return false;
        if (!title.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return false;

        // The whole title, or the folder followed by something that is not more name.
        // Without this a tab called "Docs" would claim a window showing "Documents".
        return title.Length == folder.Length
               || !char.IsLetterOrDigit(title[folder.Length]);
    }
}
