namespace Satsuma.Core.Tools;

/// <summary>
/// Document conversion through a local LibreOffice.
///
/// Office formats are not something to reimplement. A DOCX is a document model, not a
/// container of text, and converting one faithfully means laying it out: pagination,
/// styles, tables, embedded objects. LibreOffice already does that, offline, and is the
/// same engine most "convert my document" services run server-side.
///
/// It is optional, like ffmpeg. Where it is missing, those slices never reach the wheel,
/// rather than failing after the drop.
/// </summary>
public static class LibreOffice
{
    private static readonly Lazy<string?> Executable = new(Locate);

    public static string? Path => Executable.Value;

    public static bool IsAvailable => Path is not null;

    /// <summary>
    /// Converts a file and returns where the result landed, or null when the conversion
    /// produced nothing.
    /// </summary>
    /// <param name="filter">
    /// A LibreOffice output filter: an extension, optionally followed by the specific
    /// filter and its options, as in <c>csv:Text - txt - csv (StarCalc):9,34,76,1</c>.
    /// </param>
    public static async Task<string?> ConvertAsync(
        string sourcePath,
        string filter,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var exe = Path ?? throw new LibreOfficeMissingException();

        Directory.CreateDirectory(outputDirectory);

        // A private profile, so this never collides with a LibreOffice the user already
        // has open: a second instance sharing a profile refuses to start and the
        // conversion silently produces nothing.
        var profile = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "Satsuma", "libreoffice-profile");
        Directory.CreateDirectory(profile);

        var outcome = await ProcessRunner.RunAsync(exe, new[]
        {
            "--headless",
            "--norestore",
            "--invisible",
            "--nolockcheck",
            "--nologo",
            $"-env:UserInstallation={ToFileUri(profile)}",
            "--convert-to", filter,
            "--outdir", outputDirectory,
            sourcePath
        }, cancellationToken: cancellationToken).ConfigureAwait(false);

        // The exit code is not reliable on its own: LibreOffice reports success having
        // written nothing when it cannot read the input. The file is the answer.
        var expected = System.IO.Path.Combine(
            outputDirectory,
            System.IO.Path.GetFileNameWithoutExtension(sourcePath) + "." + ExtensionOf(filter));

        if (File.Exists(expected) && new FileInfo(expected).Length > 0) return expected;

        // Some filters name the output after the filter rather than the request, so take
        // whatever single file appeared.
        var produced = Directory.GetFiles(outputDirectory);
        if (produced.Length == 1 && new FileInfo(produced[0]).Length > 0) return produced[0];

        _ = outcome;   // kept for debugging; the file is what decides
        return null;
    }

    /// <summary>The extension a filter writes, which is everything before the first colon.</summary>
    private static string ExtensionOf(string filter)
    {
        var colon = filter.IndexOf(':');
        return colon < 0 ? filter : filter[..colon];
    }

    private static string ToFileUri(string path) =>
        "file:///" + path.Replace('\\', '/').TrimStart('/');

    private static string? Locate()
    {
        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs")
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return System.IO.Path.Combine(root, "LibreOffice", "program", "soffice.exe");
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVar.Split(
                     System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string combined;
            try { combined = System.IO.Path.Combine(directory.Trim(), "soffice.exe"); }
            catch (ArgumentException) { continue; }

            yield return combined;
        }
    }
}

/// <summary>Thrown when a conversion needs LibreOffice and the machine has none.</summary>
public sealed class LibreOfficeMissingException : Exception
{
    public LibreOfficeMissingException()
        : base("LibreOffice was not found. Install it to convert documents, " +
               "spreadsheets and slides.")
    {
    }
}
