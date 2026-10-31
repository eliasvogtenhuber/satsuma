using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Satsuma.Core.Tools;

/// <summary>
/// Image load and save through Windows Imaging Component, which is what WPF's imaging
/// classes sit on.
///
/// Going through WIC rather than ffmpeg buys two things: it is in-process, so a single
/// photo converts with no process spawn, and it picks up the codecs Windows itself has,
/// which is how HEIC from an iPhone opens without Satsuma shipping a HEIC decoder.
/// Formats WIC cannot write (WebP, AVIF) fall through to ffmpeg.
/// </summary>
public static class ImageCodec
{
    /// <summary>Extensions WIC can write on a stock Windows 11.</summary>
    private static readonly HashSet<string> WicWritable =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".tiff", ".tif", ".bmp", ".gif" };

    /// <summary>Extensions that need ffmpeg to write.</summary>
    private static readonly HashSet<string> FfmpegWritable =
        new(StringComparer.OrdinalIgnoreCase) { ".webp", ".avif" };

    public static bool CanWrite(string extension) =>
        WicWritable.Contains(extension) ||
        (FfmpegWritable.Contains(extension) && FfmpegLocator.IsAvailable);

    public static bool CanWriteWithWic(string extension) => WicWritable.Contains(extension);

    public static bool NeedsFfmpegToWrite(string extension) => FfmpegWritable.Contains(extension);

    /// <summary>
    /// Decodes a file, applying any EXIF orientation so the result is upright.
    /// Returns null when no installed codec can read it.
    /// </summary>
    public static BitmapSource? TryDecode(string path, out BitmapMetadata? metadata) =>
        TryDecode(path, out metadata, out _);

    /// <summary>
    /// As <see cref="TryDecode(string, out BitmapMetadata?)"/>, but reports why it failed
    /// so the caller can say something more useful than "could not read this image".
    /// </summary>
    public static BitmapSource? TryDecode(
        string path, out BitmapMetadata? metadata, out string? error)
    {
        metadata = null;
        error = null;

        try
        {
            // Decoded from a stream rather than a Uri: the Uri overloads route through
            // WPF's resource loading, which drags in packaging and policy that a plain
            // file does not need, and they keep the file open in some cache modes.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);   // OnLoad so the stream can close underneath us

            if (decoder.Frames.Count == 0)
            {
                error = "The file holds no image frames.";
                return null;
            }

            var frame = decoder.Frames[0];
            metadata = TryCloneMetadata(frame);

            // Frozen before it leaves, so callers cannot mutate it. Note this does not
            // detach it from the decoder, which stays bound to this thread: the whole
            // decode, transform and encode happens in one STA call for that reason.
            return Frozen(ApplyExifOrientation(frame, metadata));
        }
        catch (Exception ex)
        {
            // Unreadable, or no codec installed for this format. The caller falls back.
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="image"/> with a WIC encoder. <paramref name="quality"/> is
    /// 1..100 and only means anything for JPEG.
    /// </summary>
    public static void SaveWithWic(
        BitmapSource image,
        string path,
        int quality = 92,
        BitmapMetadata? metadata = null)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();

        // JPEG, BMP and GIF have no usable alpha, so flatten onto white first.
        if (extension is ".jpg" or ".jpeg" or ".bmp" && HasAlpha(image))
        {
            image = FlattenOntoWhite(image);
        }

        BitmapEncoder encoder = extension switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) },
            ".png" => new PngBitmapEncoder(),
            ".tiff" or ".tif" => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            _ => throw new NotSupportedException($"WIC cannot write {extension} files.")
        };

        encoder.Frames.Add(BuildFrame(image, metadata, extension));

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    /// <summary>
    /// Builds the frame, carrying metadata over when the target format accepts it.
    /// Cross-format metadata transfer throws more often than it works, so a failure here
    /// just means the result has no EXIF, which beats failing the conversion.
    /// </summary>
    private static BitmapFrame BuildFrame(
        BitmapSource image, BitmapMetadata? metadata, string extension)
    {
        if (metadata is null) return BitmapFrame.Create(image);

        // GIF and BMP have no metadata container worth writing to.
        if (extension is ".bmp" or ".gif") return BitmapFrame.Create(image);

        try
        {
            var copy = metadata.Clone();

            // The orientation was already baked into the pixels on decode, so leaving the
            // tag behind would make viewers rotate the image a second time.
            TryRemoveQuery(copy, "/app1/ifd/{ushort=274}");

            copy.Freeze();
            return BitmapFrame.Create(image, thumbnail: null, copy, colorContexts: null);
        }
        catch (Exception)
        {
            return BitmapFrame.Create(image);
        }
    }

    /// <summary>
    /// Encodes to JPEG in memory, for the PDF writers, which embed JPEG directly rather
    /// than take a bitmap.
    /// </summary>
    public static byte[] EncodeToJpeg(BitmapSource image, int quality)
    {
        if (HasAlpha(image)) image = FlattenOntoWhite(image);

        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(image));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static BitmapSource Scale(BitmapSource image, double factor)
    {
        if (Math.Abs(factor - 1.0) < 0.0001) return image;

        return Freeze(new TransformedBitmap(Frozen(image), new ScaleTransform(factor, factor)));
    }

    /// <summary>
    /// Scales to exact dimensions, which is the one resize that does not keep the aspect
    /// ratio: having been asked for a size, that is what gets written.
    /// </summary>
    public static BitmapSource ScaleTo(BitmapSource image, int width, int height)
    {
        if (image.PixelWidth == width && image.PixelHeight == height) return image;

        return Freeze(new TransformedBitmap(Frozen(image), new ScaleTransform(
            width / (double)image.PixelWidth,
            height / (double)image.PixelHeight)));
    }

    /// <summary>Scales so the longer edge becomes <paramref name="longEdge"/> pixels.</summary>
    public static BitmapSource ScaleToLongEdge(BitmapSource image, int longEdge)
    {
        var current = Math.Max(image.PixelWidth, image.PixelHeight);
        if (current <= longEdge) return image;   // never upscale
        return Scale(image, (double)longEdge / current);
    }

    public static BitmapSource Rotate(BitmapSource image, int degrees)
    {
        var normalized = ((degrees % 360) + 360) % 360;
        if (normalized == 0) return image;

        return Freeze(new TransformedBitmap(Frozen(image), new RotateTransform(normalized)));
    }

    public static BitmapSource Flip(BitmapSource image, bool horizontal)
    {
        var transform = horizontal
            ? new ScaleTransform(-1, 1)
            : new ScaleTransform(1, -1);

        return Freeze(new TransformedBitmap(Frozen(image), transform));
    }

    public static BitmapSource Grayscale(BitmapSource image) =>
        Freeze(new FormatConvertedBitmap(Frozen(image), PixelFormats.Gray8, null, 0));

    private static BitmapSource FlattenOntoWhite(BitmapSource image)
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var bounds = new System.Windows.Rect(0, 0, image.PixelWidth, image.PixelHeight);
            context.DrawRectangle(System.Windows.Media.Brushes.White, null, bounds);
            context.DrawImage(image, bounds);
        }

        var target = new RenderTargetBitmap(
            image.PixelWidth, image.PixelHeight,
            96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        return Freeze(target);
    }

    private static bool HasAlpha(BitmapSource image)
    {
        var format = image.Format;
        return format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32
            || format == PixelFormats.Prgba64 || format == PixelFormats.Rgba64
            || format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float
            || format == PixelFormats.Indexed8 || format == PixelFormats.Indexed4
            || format == PixelFormats.Indexed2 || format == PixelFormats.Indexed1;
    }

    /// <summary>TransformedBitmap and FormatConvertedBitmap both demand a frozen source.</summary>
    private static BitmapSource Frozen(BitmapSource image)
    {
        if (!image.IsFrozen && image.CanFreeze) image.Freeze();
        return image;
    }

    private static BitmapSource Freeze(BitmapSource image)
    {
        if (image.CanFreeze) image.Freeze();
        return image;
    }

    private static BitmapMetadata? TryCloneMetadata(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata source) return null;
            var clone = source.Clone();
            clone.Freeze();
            return clone;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Bakes EXIF orientation into the pixels. WIC hands back the raw sensor orientation,
    /// so without this a portrait phone photo converts to a sideways file.
    /// </summary>
    private static BitmapSource ApplyExifOrientation(BitmapSource image, BitmapMetadata? metadata)
    {
        var orientation = ReadOrientation(metadata);

        return orientation switch
        {
            2 => Flip(image, horizontal: true),
            3 => Rotate(image, 180),
            4 => Flip(image, horizontal: false),
            5 => Flip(Rotate(image, 90), horizontal: true),
            6 => Rotate(image, 90),
            7 => Flip(Rotate(image, 270), horizontal: true),
            8 => Rotate(image, 270),
            _ => image
        };
    }

    private static ushort ReadOrientation(BitmapMetadata? metadata)
    {
        if (metadata is null) return 1;

        foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try
            {
                if (!metadata.ContainsQuery(query)) continue;
                if (metadata.GetQuery(query) is ushort value && value is >= 1 and <= 8) return value;
            }
            catch (Exception)
            {
                // Some decoders throw rather than report a missing query.
            }
        }

        return 1;
    }

    private static void TryRemoveQuery(BitmapMetadata metadata, string query)
    {
        try
        {
            if (metadata.ContainsQuery(query)) metadata.RemoveQuery(query);
        }
        catch (Exception)
        {
            // Not writable on this container; harmless.
        }
    }
}
