namespace Satsuma.Core.Tools;

/// <summary>
/// Finds ffmpeg and ffprobe. A release build ships them in <c>tools\</c> next to the exe;
/// during development it falls back to anything already on the machine, including the
/// usual winget install locations, so there is nothing to set up before a first run.
/// </summary>
public static class FfmpegLocator
{
    private static readonly Lazy<string?> FfmpegPath = new(() => Locate("ffmpeg.exe"));
    private static readonly Lazy<string?> FfprobePath = new(() => Locate("ffprobe.exe"));

    public static string? Ffmpeg => FfmpegPath.Value;
    public static string? Ffprobe => FfprobePath.Value;

    public static bool IsAvailable => Ffmpeg is not null;

    /// <summary>Where a release build expects the bundled binaries to live.</summary>
    public static string BundledToolsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "tools");

    /// <summary>Where Satsuma downloads them to when the user opts in from the tray.</summary>
    public static string UserToolsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Satsuma", "tools");

    private static string? Locate(string exeName)
    {
        foreach (var candidate in CandidatePaths(exeName))
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> CandidatePaths(string exeName)
    {
        yield return Path.Combine(BundledToolsDirectory, exeName);
        yield return Path.Combine(AppContext.BaseDirectory, exeName);
        yield return Path.Combine(UserToolsDirectory, exeName);

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string combined;
            try { combined = Path.Combine(dir.Trim(), exeName); }
            catch (ArgumentException) { continue; }   // PATH entries can hold junk
            yield return combined;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links", exeName);

        foreach (var found in SearchWinGetPackages(localAppData, exeName))
        {
            yield return found;
        }
    }

    /// <summary>
    /// winget keeps ffmpeg under a versioned folder, so the path cannot be hardcoded.
    /// Only the shallow layers are searched, to keep this cheap.
    /// </summary>
    private static IEnumerable<string> SearchWinGetPackages(string localAppData, string exeName)
    {
        var packagesRoot = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(packagesRoot)) yield break;

        IEnumerable<string> packageDirs;
        try
        {
            packageDirs = Directory.EnumerateDirectories(packagesRoot, "*FFmpeg*");
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var packageDir in packageDirs)
        {
            foreach (var hit in SafeEnumerate(packageDir, exeName))
            {
                yield return hit;
            }
        }
    }

    private static IEnumerable<string> SafeEnumerate(string root, string exeName)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 3,
            IgnoreInaccessible = true
        };

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, exeName, options); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }

        return files;
    }
}
