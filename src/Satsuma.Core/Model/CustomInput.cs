using System.Globalization;

namespace Satsuma.Core.Model;

/// <summary>What a slice asks the user to type, which decides how it is read and checked.</summary>
public enum InputKind
{
    /// <summary>A size: a percentage, a long edge in pixels, or exact dimensions.</summary>
    Size,

    /// <summary>A quality from 1 to 100.</summary>
    Quality,

    /// <summary>An audio bitrate in kbps.</summary>
    Bitrate,

    /// <summary>A speed multiplier.</summary>
    Speed,

    /// <summary>Page numbers and ranges.</summary>
    PageRange,

    /// <summary>Page numbers to cut a document before.</summary>
    SplitPoints
}

/// <summary>
/// A slice that needs a value before it can run.
///
/// The wheel cannot take typed input: it is never focused, because taking focus would
/// cancel the drag it lives inside. So a slice that needs a number says so, and the value
/// is asked for after the drop, when the drag is over and a window may take focus.
/// </summary>
public sealed record InputRequest(string Title, string Hint, InputKind Kind)
{
    /// <summary>Shown in the field before anything is typed.</summary>
    public string? Placeholder { get; init; }
}

/// <summary>How much of an image or video to keep, however the user chose to say it.</summary>
public readonly record struct SizeRequest(double? Scale, int? LongEdge, int? Width, int? Height)
{
    public bool IsExact => Width is not null && Height is not null;
}

/// <summary>
/// Reads what people actually type.
///
/// Kept here, away from the window that collects it, because the interesting part is the
/// parsing and it should be tested without a window: "50%", "50", "1200x800" and "1200 x
/// 800" all mean something, and so do the ways of getting them wrong.
/// </summary>
public static class CustomInput
{
    /// <summary>
    /// A size. Accepts a percentage ("50%", "50 %"), a long edge in pixels ("1080"), or
    /// exact dimensions ("1200x800", "1200 × 800").
    ///
    /// A bare number is ambiguous, so the rule is the one people expect: small numbers
    /// are percentages, large ones are pixels.
    /// </summary>
    public static bool TryParseSize(string? text, out SizeRequest size)
    {
        size = default;

        var value = WithDecimalPoint(Clean(text));
        if (value.Length == 0) return false;

        // Exact dimensions.
        var separator = value.IndexOfAny(new[] { 'x', 'X', '*', '×' });
        if (separator > 0)
        {
            var left = value[..separator].Trim();
            var right = value[(separator + 1)..].Trim();

            if (TryPixels(left, out var width) && TryPixels(right, out var height))
            {
                size = new SizeRequest(null, null, width, height);
                return true;
            }

            return false;
        }

        var isPercent = value.EndsWith('%');
        if (isPercent) value = value[..^1].Trim();

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        if (number <= 0) return false;

        // Anything up to 100 without a unit is read as a percentage; a long edge below
        // that would be a thumbnail nobody asks for by typing "50".
        if (isPercent || number <= 100)
        {
            if (number > 1000) return false;   // 1000% is already absurd
            size = new SizeRequest(number / 100.0, null, null, null);
            return true;
        }

        if (number > 100000) return false;
        size = new SizeRequest(null, (int)Math.Round(number), null, null);
        return true;
    }

    /// <summary>A quality from 1 to 100, with or without a percent sign.</summary>
    public static bool TryParseQuality(string? text, out int quality)
    {
        quality = 0;

        var value = WithDecimalPoint(Clean(text)).TrimEnd('%').Trim();

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        if (number is < 1 or > 100) return false;

        quality = number;
        return true;
    }

    /// <summary>A bitrate in kbps, written as "128" or "128k".</summary>
    public static bool TryParseBitrate(string? text, out string bitrate)
    {
        bitrate = string.Empty;

        var value = WithDecimalPoint(Clean(text)).ToLowerInvariant();
        if (value.EndsWith("kbps")) value = value[..^4].Trim();
        else if (value.EndsWith("k")) value = value[..^1].Trim();

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        if (number is < 8 or > 2000) return false;

        bitrate = number + "k";
        return true;
    }

    /// <summary>A speed multiplier, written as "2", "1.5" or "2x".</summary>
    public static bool TryParseSpeed(string? text, out double factor)
    {
        factor = 0;

        var value = WithDecimalPoint(Clean(text)).ToLowerInvariant().TrimEnd('x').Trim();

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        if (number is <= 0 or > 100) return false;

        factor = number;
        return true;
    }

    /// <summary>
    /// Page numbers and ranges: "3", "2,5,9", "3-7", "4-" for everything from four on,
    /// "-6" for everything up to six, and any mixture of those.
    ///
    /// <paramref name="pageCount"/> closes the open-ended forms and discards anything
    /// past the end. The result is ordered and deduplicated, so "5,3,3-4" is 3, 4, 5.
    /// </summary>
    public static bool TryParsePageRange(string? text, int pageCount, out IReadOnlyList<int> pages)
    {
        pages = Array.Empty<int>();

        var value = Clean(text);
        if (value.Length == 0 || pageCount <= 0) return false;

        var chosen = new SortedSet<int>();

        foreach (var part in value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var piece = part.Trim().Replace('–', '-');   // an en dash is a range too
            if (piece.Length == 0) continue;

            var dash = piece.IndexOf('-');

            if (dash < 0)
            {
                if (!int.TryParse(piece, out var single)) return false;
                if (single >= 1 && single <= pageCount) chosen.Add(single);
                continue;
            }

            var fromText = piece[..dash].Trim();
            var toText = piece[(dash + 1)..].Trim();

            var from = 1;
            var to = pageCount;

            if (fromText.Length > 0 && !int.TryParse(fromText, out from)) return false;
            if (toText.Length > 0 && !int.TryParse(toText, out to)) return false;

            if (from > to) (from, to) = (to, from);

            for (var page = Math.Max(1, from); page <= Math.Min(pageCount, to); page++)
            {
                chosen.Add(page);
            }
        }

        if (chosen.Count == 0) return false;

        pages = chosen.ToList();
        return true;
    }

    /// <summary>
    /// Page numbers to cut before: "12", or "5,12,30" for several cuts.
    ///
    /// Each number starts a new file, which is why page 1 is not a cut — there is nothing
    /// before it to cut off — and nor is anything past the last page. Cuts out of range
    /// are dropped rather than refused, the way a page range drops them, so pasting a
    /// chapter list from a longer edition still does something sensible.
    /// </summary>
    public static bool TryParseSplitPoints(string? text, int pageCount, out IReadOnlyList<int> cuts)
    {
        cuts = Array.Empty<int>();

        var value = Clean(text);
        if (value.Length == 0 || pageCount <= 1) return false;

        var chosen = new SortedSet<int>();

        foreach (var part in value.Split(
            new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(
                part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var page))
            {
                return false;
            }

            if (page >= 2 && page <= pageCount) chosen.Add(page);
        }

        if (chosen.Count == 0) return false;

        cuts = chosen.ToList();
        return true;
    }

    /// <summary>True when a value would be accepted, for checking as it is typed.</summary>
    public static bool IsValid(InputKind kind, string? text, int pageCount = 1) => kind switch
    {
        InputKind.Size => TryParseSize(text, out _),
        InputKind.Quality => TryParseQuality(text, out _),
        InputKind.Bitrate => TryParseBitrate(text, out _),
        InputKind.Speed => TryParseSpeed(text, out _),
        InputKind.PageRange => TryParsePageRange(text, pageCount, out _),
        InputKind.SplitPoints => TryParseSplitPoints(text, pageCount, out _),
        _ => false
    };

    /// <summary>A whole number of pixels, which is what a dimension has to be.</summary>
    private static bool TryPixels(string text, out int pixels)
    {
        pixels = 0;

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        if (number is < 1 or > 100000) return false;

        pixels = (int)Math.Round(number);
        return true;
    }

    private static string Clean(string? text) => (text ?? string.Empty).Trim();

    /// <summary>
    /// Takes a decimal comma, which is how most of the world writes "1,5".
    ///
    /// Only the single-number fields do this. A page range uses commas as its list
    /// separator, so "2,5" there means two pages and not two and a half.
    /// </summary>
    private static string WithDecimalPoint(string value) => value.Replace(',', '.');
}
