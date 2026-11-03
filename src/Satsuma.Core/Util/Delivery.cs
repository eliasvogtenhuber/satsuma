using Satsuma.Core.Model;
using Satsuma.Core.Tools;

namespace Satsuma.Core.Util;

/// <summary>Where the results ended up, and anything that went wrong on the way.</summary>
public sealed record DeliveryResult(
    IReadOnlyList<ProducedFile> Produced,
    IReadOnlyList<string> Problems);

/// <summary>
/// Moves finished results to wherever the user asked for them.
///
/// This runs after the handlers rather than inside them. A handler's job is to make the
/// file; where it then goes is the same question for all fourteen of them, and threading
/// an answer through every one would mean fourteen chances to get it wrong. Results are
/// written beside the original as they always were, and moved once, here.
/// </summary>
public static class Delivery
{
    public static DeliveryResult Deliver(
        Destination destination,
        IReadOnlyList<ProducedFile> produced,
        string label,
        string? chosenFolder = null)
    {
        if (produced.Count == 0 || destination == Destination.Beside)
        {
            return new DeliveryResult(produced, Array.Empty<string>());
        }

        return destination switch
        {
            Destination.NewFolder => IntoFolder(produced, NewFolderBeside(produced, label)),
            Destination.ChosenFolder => IntoFolder(produced, chosenFolder),
            Destination.InPlace => OverTheOriginals(produced),
            _ => new DeliveryResult(produced, Array.Empty<string>())
        };
    }

    /// <summary>
    /// A folder named after what was done, beside the results.
    ///
    /// Named for the action rather than the file, because one action can have produced
    /// results from several files and only the action is true of all of them.
    /// </summary>
    private static string NewFolderBeside(IReadOnlyList<ProducedFile> produced, string label)
    {
        var parent = Path.GetDirectoryName(produced[0].Path);
        if (string.IsNullOrEmpty(parent)) parent = Directory.GetCurrentDirectory();

        return OutputNaming.NextDirectoryIn(parent, label);
    }

    private static DeliveryResult IntoFolder(IReadOnlyList<ProducedFile> produced, string? folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return new DeliveryResult(produced, new[] { "No folder to move these into." });
        }

        var landed = new List<ProducedFile>(produced.Count);
        var problems = new List<string>();

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(produced, new[] { $"Could not make that folder: {ex.Message}" });
        }

        foreach (var file in produced)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file.Path);
                var extension = Path.GetExtension(file.Path);

                var target = Directory.Exists(file.Path)
                    ? OutputNaming.NextDirectoryIn(folder, Path.GetFileName(file.Path))
                    : OutputNaming.NextIn(folder, name, extension);

                Move(file.Path, target);
                landed.Add(file with { Path = target });
            }
            catch (Exception ex)
            {
                problems.Add($"{Path.GetFileName(file.Path)}: {ex.Message}");
                landed.Add(file);
            }
        }

        return new DeliveryResult(landed, problems);
    }

    /// <summary>
    /// Puts each result where its originals were, and sends those originals to the bin.
    ///
    /// Three shapes, settled by one rule. One file in and one out -- a conversion -- and
    /// the result takes the original's name. One in and several out, which is a split:
    /// there is no single place for those to land, so the original still goes, because
    /// that is what was asked for, and the pieces stay beside where it used to be.
    /// Several in and one out, which is a merge: every one of them goes, and the result
    /// takes the name of the first, the file the wheel was opened on.
    ///
    /// The rule: group the results by the set of files they came from, bin that set, and
    /// rename onto the first of them when the group produced exactly one file.
    /// </summary>
    private static DeliveryResult OverTheOriginals(IReadOnlyList<ProducedFile> produced)
    {
        var landed = new List<ProducedFile>(produced.Count);
        var problems = new List<string>();

        // Nothing claims to have come from anything: nothing to replace.
        foreach (var orphan in produced.Where(file => file.Sources.Count == 0))
        {
            landed.Add(orphan);
        }

        var groups = produced
            .Where(file => file.Sources.Count > 0)
            .GroupBy(
                file => string.Join("|", file.Sources).ToLowerInvariant(),
                StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var pieces = group.ToList();
            var sources = pieces[0].Sources
                .Where(path => File.Exists(path) || Directory.Exists(path))
                .ToList();

            if (sources.Count == 0)
            {
                landed.AddRange(pieces);
                continue;
            }

            // Nowhere to put an original back from, so none of them go. A quiet
            // unrecoverable delete is not a feature, and half a merge's inputs in the
            // bin and half not would be worse than neither.
            var unprotected = sources.Where(path => !RecycleBin.Covers(path)).ToList();

            if (unprotected.Count > 0)
            {
                problems.Add(
                    $"{Path.GetFileName(unprotected[0])}: kept, because a delete here " +
                    "would not reach the Recycle Bin.");

                landed.AddRange(pieces);
                continue;
            }

            // Worked out before anything goes, since the name comes from the first one.
            var first = sources[0];
            var directory = Path.GetDirectoryName(first) ?? Directory.GetCurrentDirectory();
            var stem = Path.GetFileNameWithoutExtension(first);

            var stuck = sources.Where(path => !RecycleBin.Send(path)).ToList();

            if (stuck.Count > 0)
            {
                problems.Add($"{Path.GetFileName(stuck[0])}: could not be moved to the bin.");
            }

            if (pieces.Count > 1)
            {
                landed.AddRange(pieces);
                continue;
            }

            var piece = pieces[0];

            try
            {
                var target = Path.Combine(directory, stem + Path.GetExtension(piece.Path));
                if (File.Exists(target) || Directory.Exists(target))
                {
                    target = OutputNaming.NextIn(directory, stem, Path.GetExtension(piece.Path));
                }

                Move(piece.Path, target);
                landed.Add(piece with { Path = target });
            }
            catch (Exception ex)
            {
                problems.Add($"{Path.GetFileName(piece.Path)}: {ex.Message}");
                landed.Add(piece);
            }
        }

        return new DeliveryResult(landed, problems);
    }

    /// <summary>
    /// Moves a file or a whole folder.
    ///
    /// <see cref="Directory.Move"/> is a rename and refuses to cross volumes, which a
    /// chosen folder very well might, so a folder that will not move is copied across
    /// and the original removed.
    /// </summary>
    private static void Move(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            File.Move(from, to);
            return;
        }

        try
        {
            Directory.Move(from, to);
        }
        catch (IOException)
        {
            CopyTree(from, to);
            Directory.Delete(from, recursive: true);
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(from))
        {
            CopyTree(directory, Path.Combine(to, Path.GetFileName(directory)));
        }
    }
}
