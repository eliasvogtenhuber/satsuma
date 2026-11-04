namespace Satsuma.Core.Tools;

/// <summary>
/// Picks the H.264 encoder this machine actually has, and the quality flags it takes.
///
/// Satsuma asked for <c>libx264</c> everywhere, which is in every ffmpeg anyone builds
/// for themselves and in none that can be shipped: x264 is GPL, so the LGPL builds
/// Satsuma bundles leave it out. On a developer's machine the lookup fell through to a
/// winget ffmpeg that had it, and every release went out with MP4, MOV, MKV, Compress,
/// Resize, Speed, Rotate and Join broken.
///
/// So the encoder is discovered rather than assumed, and discovered by encoding
/// something rather than by reading a list: <c>-encoders</c> happily advertises
/// h264_nvenc on a machine with no NVIDIA card in it. A quarter of a second of black
/// video is the only answer that cannot be wrong.
/// </summary>
public static class H264
{
    /// <summary>
    /// Best first. libx264 where it exists, because it is the one with real rate
    /// control; then Media Foundation, which is part of Windows and uses whatever the
    /// machine has; then OpenH264, which is always there and always software.
    ///
    /// Hardware encoders are deliberately not in this list. They are listed by ffmpeg
    /// whether or not the hardware exists, they fail in ways that depend on the driver,
    /// and h264_mf already goes through the hardware path when Windows has one.
    /// </summary>
    private static readonly string[] Preference = { "libx264", "h264_mf", "libopenh264" };

    private static readonly Lazy<string?> Discovered = new(Discover, isThreadSafe: true);

    /// <summary>The encoder to use, or null when none of them work.</summary>
    public static string? Encoder => Discovered.Value;

    public static bool IsAvailable => Encoder is not null;

    /// <summary>
    /// The arguments that encode H.264 at roughly the quality a libx264 CRF would give.
    ///
    /// CRF is libx264's scale and nothing else speaks it, so the others are mapped onto
    /// it: lower means better, 18 is near-transparent and 34 is visibly soft, which is
    /// the range Satsuma's presets use.
    /// </summary>
    public static IReadOnlyList<string> Args(int crf) => Encoder switch
    {
        "libx264" => new[] { "-c:v", "libx264", "-crf", crf.ToString(), "-preset", "medium" },

        // Media Foundation counts the other way, 0 to 100, higher being better.
        "h264_mf" => new[]
        {
            "-c:v", "h264_mf", "-rate_control", "quality",
            "-quality", MediaFoundationQuality(crf).ToString()
        },

        // OpenH264's quality mode has no dial worth exposing; it targets a sensible
        // quality on its own and ignores a bitrate it was not given.
        "libopenh264" => new[] { "-c:v", "libopenh264", "-rc_mode", "quality" },

        _ => new[] { "-c:v", "libx264", "-crf", crf.ToString(), "-preset", "medium" }
    };

    private static int MediaFoundationQuality(int crf) =>
        Math.Clamp((int)Math.Round(118 - (1.8 * crf)), 10, 95);

    private static string? Discover()
    {
        if (!FfmpegLocator.IsAvailable) return null;

        string listed;

        try
        {
            listed = ProcessRunner
                .RunAsync(FfmpegLocator.Ffmpeg!, new[] { "-hide_banner", "-encoders" })
                .GetAwaiter().GetResult().StdOut;
        }
        catch (Exception)
        {
            return null;
        }

        foreach (var name in Preference)
        {
            if (!listed.Contains(name, StringComparison.Ordinal)) continue;
            if (Works(name)) return name;
        }

        return null;
    }

    /// <summary>Encodes one black frame, which is the only proof that counts.</summary>
    private static bool Works(string encoder)
    {
        try
        {
            var outcome = ProcessRunner.RunAsync(FfmpegLocator.Ffmpeg!, new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "error",
                "-f", "lavfi", "-i", "color=c=black:s=64x64:d=0.1",
                "-c:v", encoder, "-pix_fmt", "yuv420p", "-frames:v", "1",
                "-f", "null", "-"
            }).GetAwaiter().GetResult();

            return outcome.Succeeded;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
