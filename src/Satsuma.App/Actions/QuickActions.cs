using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using Satsuma.Core.Model;
using static Satsuma.App.Interop.NativeMethods;

namespace Satsuma.App.Actions;

public sealed record ActionOutcome(bool Succeeded, string Message);

/// <summary>
/// The actions wheel. None of these transform a file, so they all run instantly and none
/// of them needs the job runner.
/// </summary>
public static class QuickActions
{
    public static ActionOutcome Run(
        string actionId, IReadOnlyList<SourceFile> files, Action<IReadOnlyList<SourceFile>> onFloat)
    {
        if (files.Count == 0) return new ActionOutcome(false, "Nothing selected.");

        try
        {
            return actionId switch
            {
                "act.reveal" => Reveal(files),
                "act.copy" => CopyToClipboard(files),
                "act.path" => CopyPaths(files),
                "act.terminal" => OpenTerminal(files),
                "act.move" => SendTo(files, keepOriginal: false),
                "act.copyto" => SendTo(files, keepOriginal: true),
                "act.float" => Float(files, onFloat),
                _ => new ActionOutcome(false, $"Unknown action {actionId}.")
            };
        }
        catch (Exception ex)
        {
            return new ActionOutcome(false, ex.Message);
        }
    }

    /// <summary>
    /// Opens Explorer with the files selected. Several files in one folder are shown in a
    /// single window; spread across folders, one window each.
    /// </summary>
    private static ActionOutcome Reveal(IReadOnlyList<SourceFile> files)
    {
        foreach (var group in files.GroupBy(f => f.Directory, StringComparer.OrdinalIgnoreCase))
        {
            SelectInExplorer(group.Key, group.Select(f => f.Path).ToList());
        }

        return new ActionOutcome(true, files.Count == 1 ? "Revealed" : $"Revealed {files.Count} files");
    }

    internal static void SelectInExplorer(string folder, IReadOnlyList<string> paths)
    {
        var folderId = ILCreateFromPath(folder);
        if (folderId == IntPtr.Zero) return;

        var itemIds = new List<IntPtr>();
        try
        {
            foreach (var path in paths)
            {
                var id = ILCreateFromPath(path);
                if (id != IntPtr.Zero) itemIds.Add(id);
            }

            SHOpenFolderAndSelectItems(folderId, (uint)itemIds.Count,
                itemIds.Count > 0 ? itemIds.ToArray() : null, 0);
        }
        finally
        {
            foreach (var id in itemIds) ILFree(id);
            ILFree(folderId);
        }
    }

    private static ActionOutcome CopyToClipboard(IReadOnlyList<SourceFile> files)
    {
        var collection = new StringCollection();
        foreach (var file in files) collection.Add(file.Path);

        RetryClipboard(() => Clipboard.SetFileDropList(collection));

        return new ActionOutcome(true,
            files.Count == 1 ? "Copied" : $"Copied {files.Count} files");
    }

    private static ActionOutcome CopyPaths(IReadOnlyList<SourceFile> files)
    {
        var text = string.Join(Environment.NewLine, files.Select(f => f.Path));
        RetryClipboard(() => Clipboard.SetText(text));

        return new ActionOutcome(true, files.Count == 1 ? "Path copied" : $"{files.Count} paths copied");
    }

    private static ActionOutcome OpenTerminal(IReadOnlyList<SourceFile> files)
    {
        // A folder opens in itself; a file opens in the folder holding it.
        var first = files[0];
        var directory = first.IsDirectory ? first.Path : first.Directory;

        if (!Directory.Exists(directory))
        {
            return new ActionOutcome(false, "That folder is gone.");
        }

        // Windows Terminal if it is installed, otherwise PowerShell.
        foreach (var (exe, args) in new[]
                 {
                     ("wt.exe", new[] { "-d", directory }),
                     ("powershell.exe", new[] { "-NoExit", "-Command", $"Set-Location -LiteralPath '{directory.Replace("'", "''")}'" })
                 })
        {
            try
            {
                var info = new ProcessStartInfo { FileName = exe, UseShellExecute = true };
                foreach (var arg in args) info.ArgumentList.Add(arg);

                Process.Start(info);
                return new ActionOutcome(true, "Terminal opened");
            }
            catch (Exception)
            {
                // Try the next one.
            }
        }

        return new ActionOutcome(false, "Could not open a terminal.");
    }

    /// <summary>
    /// Move and Copy to, which differ only in whether the original stays.
    ///
    /// Neither ever overwrites: the target name goes through the same collision naming
    /// everything else uses, so dropping a second report.pdf into a folder leaves the
    /// first one alone and lands beside it.
    /// </summary>
    private static ActionOutcome SendTo(IReadOnlyList<SourceFile> files, bool keepOriginal)
    {
        var verb = keepOriginal ? "Copy" : "Move";

        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = files.Count == 1
                ? $"{verb} {files[0].Name} to"
                : $"{verb} {files.Count} items to",
            UseDescriptionForTitle = true,
            InitialDirectory = files[0].Directory,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return new ActionOutcome(false, "Cancelled");
        }

        var destination = dialog.SelectedPath;
        var done = 0;
        var failures = new List<string>();

        foreach (var file in files)
        {
            try
            {
                var target = Core.Util.OutputNaming.NextIn(
                    destination,
                    Path.GetFileNameWithoutExtension(file.Path),
                    file.IsDirectory ? string.Empty : Path.GetExtension(file.Path));

                if (keepOriginal) Core.Util.Delivery.CopyTo(file.Path, target);
                else Core.Util.Delivery.MoveTo(file.Path, target);

                done++;
            }
            catch (Exception ex)
            {
                failures.Add($"{file.Name}: {ex.Message}");
            }
        }

        var past = keepOriginal ? "Copied" : "Moved";

        if (failures.Count > 0)
        {
            return new ActionOutcome(done > 0, $"{past} {done}, {failures.Count} failed");
        }

        return new ActionOutcome(true, done == 1 ? past : $"{past} {done} items");
    }

    private static ActionOutcome Float(
        IReadOnlyList<SourceFile> files, Action<IReadOnlyList<SourceFile>> onFloat)
    {
        onFloat(files);
        return new ActionOutcome(true, files.Count == 1 ? "On the shelf" : $"{files.Count} on the shelf");
    }

    /// <summary>
    /// The clipboard is a shared, single-owner resource, so another app holding it open
    /// makes a set fail. Retrying briefly is the usual remedy.
    /// </summary>
    private static void RetryClipboard(Action set)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                set();
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                System.Threading.Thread.Sleep(40);
            }
        }

        set();   // final attempt, and let it throw if it still will not take
    }
}
