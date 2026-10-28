using System.Formats.Tar;
using System.IO.Compression;
using Satsuma.Core.Model;
using Satsuma.Core.Util;

namespace Satsuma.Core.Engine.Handlers;

/// <summary>
/// Archives, all in-process: ZIP and GZIP from the base library, TAR from
/// System.Formats.Tar. RAR and 7z stay read-nothing for now, since neither has a
/// usable managed reader in the box.
/// </summary>
public sealed class ArchiveHandler : IJobHandler
{
    private static readonly HashSet<string> Creatable =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".tar", ".gz" };

    private static readonly HashSet<string> Extractable =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".tar", ".gz", ".tgz" };

    public bool CanHandle(JobRequest request)
    {
        if (request.Item.Kind != WheelKind.Formats && request.Item.Kind != WheelKind.Tools)
            return false;

        if (request.Category is not (FileCategory.Archive or FileCategory.Folder))
            return false;

        if (request.Item.Id == "fmt.extract")
        {
            return request.Category == FileCategory.Archive
                   && request.Files.All(f => Extractable.Contains(f.Extension));
        }

        if (request.Item.Id == "tool.folder.zip") return request.Category == FileCategory.Folder;

        var extension = request.Item.OutputExtension;
        if (extension is null || !Creatable.Contains(extension)) return false;

        // GZIP holds exactly one member, so it cannot wrap a folder or a multi-file pick.
        if (extension == ".gz" && (request.Files.Count != 1 || request.Files[0].IsDirectory))
            return false;

        return true;
    }

    /// <summary>
    /// Creating an archive folds the selection into one file; extracting does not.
    /// A single GZIP is also per-file, since it can only hold one member anyway.
    /// </summary>
    public bool IsAggregate(JobRequest request) =>
        request.Item.Id != "fmt.extract" && request.Item.OutputExtension != ".gz";

    public async Task<JobResult> ExecuteAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (request.Item.Id == "fmt.extract")
        {
            return await ExtractAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }

        var extension = request.Item.Id == "tool.folder.zip"
            ? ".zip"
            : request.Item.OutputExtension!;

        return extension switch
        {
            ".zip" => await CreateZipAsync(request, progress, cancellationToken).ConfigureAwait(false),
            ".tar" => await CreateTarAsync(request, progress, cancellationToken).ConfigureAwait(false),
            ".gz" => await CreateGzipAsync(request, progress, cancellationToken).ConfigureAwait(false),
            _ => JobResult.Fail($"Cannot create {extension} archives.")
        };
    }

    private static async Task<JobResult> CreateZipAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var target = ArchiveTargetPath(request, ".zip");

        await Task.Run(() =>
        {
            using var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

            var entries = CollectEntries(request.Files);
            for (var index = 0; index < entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (absolute, relative) = entries[index];
                archive.CreateEntryFromFile(absolute, relative, CompressionLevel.Optimal);
                progress?.Report((double)(index + 1) / entries.Count);
            }
        }, cancellationToken).ConfigureAwait(false);

        return JobResult.Ok(target);
    }

    private static async Task<JobResult> CreateTarAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var target = ArchiveTargetPath(request, ".tar");

        await Task.Run(async () =>
        {
            await using var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            await using var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: false);

            var entries = CollectEntries(request.Files);
            for (var index = 0; index < entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (absolute, relative) = entries[index];
                // tar always uses forward slashes.
                await writer.WriteEntryAsync(absolute, relative.Replace('\\', '/'), cancellationToken)
                    .ConfigureAwait(false);
                progress?.Report((double)(index + 1) / entries.Count);
            }
        }, cancellationToken).ConfigureAwait(false);

        return JobResult.Ok(target);
    }

    private static async Task<JobResult> CreateGzipAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = request.Files[0];
        var target = OutputNaming.Next(source.Path, source.Extension + ".gz");

        await using var input = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var gzip = new GZipStream(output, CompressionLevel.Optimal);

        await CopyWithProgressAsync(input, gzip, input.Length, progress, cancellationToken)
            .ConfigureAwait(false);

        return JobResult.Ok(target);
    }

    private static async Task<JobResult> ExtractAsync(
        JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = request.Files[0];
        var destination = OutputNaming.NextDirectory(source.Path);
        Directory.CreateDirectory(destination);

        var extension = source.Extension;

        try
        {
            if (extension == ".zip")
            {
                await Task.Run(() =>
                {
                    using var archive = ZipFile.OpenRead(source.Path);
                    var total = archive.Entries.Count;

                    for (var index = 0; index < total; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ExtractEntrySafely(archive.Entries[index], destination);
                        progress?.Report((double)(index + 1) / total);
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            else if (extension == ".tar")
            {
                await TarFile.ExtractToDirectoryAsync(source.Path, destination,
                    overwriteFiles: false, cancellationToken).ConfigureAwait(false);
                progress?.Report(1.0);
            }
            else // .gz or .tgz
            {
                await ExtractGzipAsync(source.Path, destination, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            TryDeleteDirectory(destination);
            throw;
        }

        return JobResult.Ok(destination);
    }

    private static async Task ExtractGzipAsync(
        string sourcePath, string destination, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        // A .tgz, or a .gz wrapping a tar, is a tar once unwrapped.
        var inner = Path.GetFileNameWithoutExtension(sourcePath);
        var looksLikeTar = sourcePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
                           || inner.EndsWith(".tar", StringComparison.OrdinalIgnoreCase);

        if (looksLikeTar)
        {
            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var gzip = new GZipStream(input, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gzip, destination,
                overwriteFiles: false, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var targetName = inner.Length > 0 ? inner : "extracted";
            var targetPath = Path.Combine(destination, targetName);

            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var gzip = new GZipStream(input, CompressionMode.Decompress);
            await using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

            await gzip.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(1.0);
    }

    /// <summary>
    /// Resolves the entry path and refuses anything that climbs out of the destination.
    /// Archives from elsewhere cannot be trusted to stay inside their own folder.
    /// </summary>
    private static void ExtractEntrySafely(ZipArchiveEntry entry, string destination)
    {
        var destinationRoot = Path.GetFullPath(destination);
        var targetPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));

        if (!targetPath.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(targetPath, destinationRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Entry \"{entry.FullName}\" points outside the archive folder.");
        }

        // A trailing slash means a directory entry, which has no content to write.
        if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
        {
            Directory.CreateDirectory(targetPath);
            return;
        }

        var parent = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        entry.ExtractToFile(targetPath, overwrite: false);
    }

    /// <summary>
    /// Flattens the selection into (absolute, relative) pairs. A folder keeps its own name
    /// as the top level inside the archive, which is what stops a zip exploding on extract.
    /// </summary>
    private static List<(string Absolute, string Relative)> CollectEntries(
        IReadOnlyList<SourceFile> files)
    {
        var entries = new List<(string, string)>();

        foreach (var file in files)
        {
            if (file.IsDirectory)
            {
                var root = file.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var folderName = Path.GetFileName(root);
                var parentLength = root.Length - folderName.Length;

                foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    entries.Add((path, path[parentLength..]));
                }
            }
            else
            {
                entries.Add((file.Path, file.Name));
            }
        }

        return entries;
    }

    private static string ArchiveTargetPath(JobRequest request, string extension)
    {
        var first = request.Files[0];

        // A single item names the archive after itself; a multi-file pick is named after
        // the folder they sit in, which is the only name they share.
        if (request.Files.Count == 1)
        {
            return OutputNaming.Next(first.Path.TrimEnd(Path.DirectorySeparatorChar), extension);
        }

        var directory = first.Directory;
        var folderName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderName)) folderName = "archive";

        return OutputNaming.NextIn(directory, folderName, extension);
    }

    private static async Task CopyWithProgressAsync(
        System.IO.Stream input, System.IO.Stream output, long total, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long written = 0;
        int read;

        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;

            if (total > 0) progress?.Report(Math.Clamp((double)written / total, 0, 1));
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
