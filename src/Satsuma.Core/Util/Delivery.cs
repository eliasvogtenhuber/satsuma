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
    /// Puts each result where its original was, and sends the original to the bin.
    ///
    /// One original can have produced several files -- a split is the obvious case -- and
    /// there is no single place for those to land. Then the original still goes, because
    /// that is what was asked for, and the pieces stay where the handler wrote them,
    /// which is beside where it used to be.
    /// </summary>
    private static DeliveryResult OverTheOriginals(IReadOnlyList<ProducedFile> produced)
    {
        var landed = new List<ProducedFile>(produced.Count);
        var problems = new List<string>();

        // Results built from several inputs at once have no original to replace.
        foreach (var orphan in produced.Where(file => file.SourcePath is null))
        {
            landed.Add(orphan);
        }

        var groups = produced
            .Where(file => file.SourcePath is not null)
            .GroupBy(file => file.SourcePath!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var source = group.Key;
            var pieces = group.ToList();

            if (!File.Exists(source) && !Directory.Exists(source))
            {
                landed.AddRange(pieces);
                continue;
            }

            if (!RecycleBin.Covers(source))
            {
                // Nowhere to put the original back from, so it is left alone and the
                // results stay beside it. A quiet unrecoverable delete is not a feature.
                problems.Add(
                    $"{Path.GetFileName(source)}: kept, because a delete here would not " +
                    "reach the Recycle Bin.");

                landed.AddRange(pieces);
                continue;
            }

            // Worked out before the original goes, since the name comes from it.
            var directory = Path.GetDirectoryName(source) ?? Directory.GetCurrentDirectory();
            var stem = Path.GetFileNameWithoutExtension(source);

            if (!RecycleBin.Send(source))
            {
                problems.Add($"{Path.GetFileName(source)}: could not be moved to the bin.");
                landed.AddRange(pieces);
                continue;
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
