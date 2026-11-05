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

        // Nothing was written, so soffice has the only account of why. It used to be
            // discarded, which made every failure read "LibreOffice could not convert
            // this file" whether the document was corrupt, locked, or of a kind this
            // build has no filter for.
        throw new LibreOfficeFailedException(Explain(outcome));
    }

    private static string Explain(ProcessOutcome outcome)
    {
        var said = string.Join(
            " ",
            (outcome.StdErr + " " + outcome.StdOut)
                .Split('\r', '\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .TakeLast(3));

        return said.Length > 0
            ? said
            : $"LibreOffice wrote nothing and said nothing (exit {outcome.ExitCode}).";
    }

    /// <summary>The extension a filter writes, which is everything before the first colon.</summary>
    private static string ExtensionOf(string filter)
    {
        var colon = filter.IndexOf(':');
        return colon < 0 ? filter : filter[..colon];
    }

    /// <summary>
    /// A file: URI soffice will accept, with the parts that need escaping escaped.
    ///
    /// The private profile lives under the temp path, which carries the user's name, so
    /// it can hold spaces and umlauts. Unescaped, -env:UserInstallation either failed or
    /// quietly put the profile somewhere else -- and a profile somewhere else is a
    /// second instance, which is the thing the private profile exists to avoid.
    /// </summary>
    private static string ToFileUri(string path)
    {
        // Uri does this correctly and the hand-rolled version did not: escaping each
        // segment with EscapeDataString also escapes the colon after the drive letter,
        // so C:\ became C%3A\ and LibreOffice could not open its own profile.
        try
        {
            return new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri;
        }
        catch (UriFormatException)
        {
            return "file:///" + path.Replace('\\', '/').TrimStart('/');
        }
    }

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
        // Beside the exe first, the same way ffmpeg is found, so a copy dropped into
        // tools\LibreOffice makes a folder that needs nothing installed at all. Satsuma
        // does not ship one -- it is several hundred megabytes for a feature most people
        // never reach, and it releases on its own schedule -- but anyone who wants a
        // self-contained Satsuma can put one there and it is found without settings.
        yield return System.IO.Path.Combine(
            AppContext.BaseDirectory, "tools", "LibreOffice", "program", "soffice.exe");

        yield return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Satsuma", "tools", "LibreOffice", "program", "soffice.exe");

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

/// <summary>
/// Thrown when LibreOffice ran and produced nothing, carrying whatever it said about
/// why. Every failure used to read the same sentence regardless of cause.
/// </summary>
public sealed class LibreOfficeFailedException : Exception
{
    public LibreOfficeFailedException(string message) : base(message)
    {
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
