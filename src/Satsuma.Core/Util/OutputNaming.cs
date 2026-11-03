namespace Satsuma.Core.Util;

/// <summary>
/// Picks the name for a result file. Satsuma never writes over an original, so every name
/// this hands back is free at the moment it is returned.
/// </summary>
public static class OutputNaming
{
    /// <summary>
    /// A free path next to <paramref name="sourcePath"/>, with the given extension and an
    /// optional descriptive suffix. Collisions get " (2)", " (3)" and so on.
    /// </summary>
    public static string Next(string sourcePath, string extension, string? suffix = null)
    {
        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(directory))
        {
            directory = Directory.GetCurrentDirectory();
        }

        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        return NextIn(directory, baseName, extension, suffix);
    }

    /// <summary>As <see cref="Next"/>, but into a directory you choose.</summary>
    public static string NextIn(string directory, string baseName, string extension, string? suffix = null)
    {
        if (!extension.StartsWith('.')) extension = "." + extension;

        var stem = string.IsNullOrEmpty(suffix) ? baseName : $"{baseName}-{suffix}";
        stem = Sanitize(stem);

        var candidate = Path.Combine(directory, stem + extension);
        if (!Exists(candidate)) return candidate;

        for (var counter = 2; counter < 10_000; counter++)
        {
            candidate = Path.Combine(directory, $"{stem} ({counter}){extension}");
            if (!Exists(candidate)) return candidate;
        }

        // Pathological case: fall back to something guaranteed unique.
        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    /// <summary>A free directory path next to the source, for results that are a folder.</summary>
    public static string NextDirectory(string sourcePath, string? suffix = null)
    {
        var parent = Path.GetDirectoryName(sourcePath) ?? Directory.GetCurrentDirectory();
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var stem = Sanitize(string.IsNullOrEmpty(suffix) ? baseName : $"{baseName}-{suffix}");

        var candidate = Path.Combine(parent, stem);
        if (!Exists(candidate)) return candidate;

        for (var counter = 2; counter < 10_000; counter++)
        {
            candidate = Path.Combine(parent, $"{stem} ({counter})");
            if (!Exists(candidate)) return candidate;
        }

        return Path.Combine(parent, $"{stem} ({Guid.NewGuid():N})");
    }

    /// <summary>A free directory path inside a parent you choose.</summary>
    public static string NextDirectoryIn(string parent, string name)
    {
        var stem = Sanitize(name);

        var candidate = Path.Combine(parent, stem);
        if (!Exists(candidate)) return candidate;

        for (var counter = 2; counter < 10_000; counter++)
        {
            candidate = Path.Combine(parent, $"{stem} ({counter})");
            if (!Exists(candidate)) return candidate;
        }

        return Path.Combine(parent, $"{stem} ({Guid.NewGuid():N})");
    }

    /// <summary>
    /// A name for a result built from several inputs, e.g. a merge or a join.
    /// Lands next to the first input.
    /// </summary>
    public static string ForGroup(IReadOnlyList<string> sourcePaths, string extension, string label)
    {
        if (sourcePaths.Count == 0) throw new ArgumentException("No sources.", nameof(sourcePaths));

        var directory = Path.GetDirectoryName(sourcePaths[0]) ?? Directory.GetCurrentDirectory();
        var baseName = Path.GetFileNameWithoutExtension(sourcePaths[0]);
        return NextIn(directory, baseName, extension, label);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        // Trailing dots and spaces are legal to construct but awkward on Windows.
        cleaned = cleaned.TrimEnd('.', ' ');
        return cleaned.Length == 0 ? "satsuma" : cleaned;
    }
}
