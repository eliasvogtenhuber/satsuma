using System.Windows.Media;
using System.Windows.Media.Imaging;
using Satsuma.Core.Catalog;
using Satsuma.Core.Engine;
using Satsuma.Core.Model;
using Satsuma.Core.Tools;

namespace Satsuma.SelfTest;

/// <summary>
/// Drives the engine over generated sample files and reports what really converts on this
/// machine. It exists because the wheel cannot be clicked from a test: this covers
/// everything behind it, so a failure after a drag is a UI problem and nothing else.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static int _skipped;

    private static readonly JobRunner Runner = new();

    private static async Task<int> Main()
    {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

        var workspace = Path.Combine(Path.GetTempPath(), "Satsuma.SelfTest",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(workspace);

        Console.WriteLine("Satsuma self-test");
        Console.WriteLine($"  workspace : {workspace}");
        Console.WriteLine($"  ffmpeg    : {FfmpegLocator.Ffmpeg ?? "not found"}");
        Console.WriteLine($"  ffprobe   : {FfmpegLocator.Ffprobe ?? "not found"}");
        Console.WriteLine();

        try
        {
            TestHitTesting();
            await TestWheelBuilding(workspace);
            await TestImages(workspace);
            await TestImageTools(workspace);
            await TestSubtitles(workspace);
            await TestArchives(workspace);

            if (FfmpegLocator.IsAvailable)
            {
                await TestAudio(workspace);
                await TestVideo(workspace);
                await TestVideoTools(workspace);
                await TestAudioTools(workspace);
            }
            else
            {
                Section("Audio and video");
                Skip("everything", "ffmpeg not installed");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"Harness blew up: {ex}");
            _failed++;
        }

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed, {_skipped} skipped");
        Console.WriteLine($"Output left in {workspace}");

        return _failed == 0 ? 0 : 1;
    }

    // ---- Hit testing ------------------------------------------------------

    /// <summary>
    /// The behaviour behind the hover flicker. A real pointer cannot test this, because
    /// the person's own mouse moves during the run, so it is driven as pure geometry.
    /// </summary>
    private static void TestHitTesting()
    {
        Section("Wheel hit testing");

        const double inner = 62, outer = 160;
        const int count = 6;      // 60 degrees per pane
        const int none = WheelHitTest.None;

        // A point at a given angle (degrees clockwise from up) and radius.
        static (double X, double Y) At(double degrees, double radius)
        {
            var radians = (degrees - 90) * Math.PI / 180.0;
            return (radius * Math.Cos(radians), radius * Math.Sin(radians));
        }

        static int Probe(double degrees, double radius, int current)
        {
            var (x, y) = At(degrees, radius);
            return WheelHitTest.IndexAt(x, y, inner, outer, count, current);
        }

        Check("hub selects nothing", Probe(30, 20, none) == none, "inside the inner radius");
        Check("beyond the rim selects nothing", Probe(30, 400, none) == none, "past the outer radius");

        Check("pane 0 is the first 60 degrees", Probe(30, 110, none) == 0, "30 degrees picks pane 0");
        Check("pane 1 follows it", Probe(90, 110, none) == 1, "90 degrees picks pane 1");
        Check("pane 5 wraps round", Probe(330, 110, none) == 5, "330 degrees picks pane 5");

        // The flicker: a hand resting on a boundary, crossing it by a degree or two.
        var flips = 0;
        var held = Probe(58, 110, none);
        for (var i = 0; i < 60; i++)
        {
            var next = Probe(i % 2 == 0 ? 58 : 62, 110, held);
            if (next != held) flips++;
            held = next;
        }
        Check("tremor across a pane boundary does not strobe", flips == 0,
            $"{flips} changes over 60 crossings of the 60 degree line");

        // The same at the outer rim, which is the other hard line.
        flips = 0;
        held = Probe(30, 155, none);
        for (var i = 0; i < 60; i++)
        {
            var next = Probe(30, i % 2 == 0 ? 158 : 168, held);
            if (next != held) flips++;
            held = next;
        }
        Check("tremor across the outer rim does not strobe", flips == 0,
            $"{flips} changes over 60 crossings of the rim");

        // Hysteresis must not become stickiness: a deliberate move still lands.
        Check("a deliberate move to the next pane lands",
            Probe(95, 110, 0) == 1, "35 degrees past the boundary switches");
        Check("a deliberate move to the hub lands",
            Probe(30, 20, 0) == none, "well inside the hub releases");
        Check("leaving the wheel entirely releases",
            Probe(30, 400, 0) == none, "well past the rim releases");

        // Two panes meet at 180 degrees on a two-item wheel; the wrap must not confuse it.
        Check("wrap across the top is handled",
            WheelHitTest.DegreesOutside(359, 0, 60) < WheelHitTest.DegreesOutside(200, 0, 60),
            "359 degrees is nearer pane 0 than 200 degrees is");
    }

    // ---- Wheel ------------------------------------------------------------

    private static Task TestWheelBuilding(string workspace)
    {
        Section("Wheel building");

        var image = new[] { new SourceFile(Path.Combine(workspace, "x.png")) };
        var formats = Runner.BuildWheel(WheelKind.Formats, FileCategory.Image, image);

        Check("image formats offered", formats.Count >= 5, $"{formats.Count} slices");
        Check("source format hidden",
            formats.All(f => f.OutputExtension != ".png"),
            "no PNG slice for a PNG");

        var tools = Runner.BuildWheel(WheelKind.Tools, FileCategory.Image, image);
        Check("image tools offered", tools.Count >= 5, $"{tools.Count} slices");
        Check("presets kept as children",
            tools.Any(t => t.HasChildren),
            "Compress and Resize drill in");

        var actions = Runner.BuildWheel(WheelKind.Actions, FileCategory.Image, image);
        Check("actions always offered", actions.Count == 6, $"{actions.Count} slices");

        // Nothing writes these yet, so they must not reach the wheel.
        var pdf = Runner.BuildWheel(WheelKind.Formats, FileCategory.Pdf,
            new[] { new SourceFile(Path.Combine(workspace, "x.pdf")) });
        Check("unhandled formats filtered out", pdf.Count == 0, "PDF wheel is empty");

        return Task.CompletedTask;
    }

    // ---- Images -----------------------------------------------------------

    private static async Task TestImages(string workspace)
    {
        Section("Image formats");

        var source = Path.Combine(workspace, "sample.png");
        WriteTestPng(source, 640, 480);

        foreach (var extension in new[] { ".jpg", ".bmp", ".tiff", ".gif", ".webp", ".avif" })
        {
            if (!ImageCodec.CanWrite(extension))
            {
                Skip($"png -> {extension}", "no writer available");
                continue;
            }

            var item = FormatCatalog.For(FileCategory.Image)
                .First(i => i.OutputExtension == extension);

            await RunAndCheck($"png -> {extension}", item, source, FileCategory.Image);
        }
    }

    private static async Task TestImageTools(string workspace)
    {
        Section("Image tools");

        var source = Path.Combine(workspace, "tool-sample.png");
        WriteTestPng(source, 800, 600);

        var tools = ToolCatalog.For(FileCategory.Image);

        var resize = Leaf(tools, "tool.img.resize.50");
        var outputs = await RunAndCheck("resize to 50%", resize, source, FileCategory.Image);
        if (outputs.Count == 1)
        {
            var decoded = Sta(() => ImageCodec.TryDecode(outputs[0], out _));
            Check("resize really halved it",
                decoded is { PixelWidth: 400, PixelHeight: 300 },
                decoded is null ? "could not read result" : $"{decoded.PixelWidth}x{decoded.PixelHeight}");
        }

        var longEdge = Leaf(tools, "tool.img.resize.720");
        outputs = await RunAndCheck("resize long edge to 720", longEdge, source, FileCategory.Image);
        if (outputs.Count == 1)
        {
            var decoded = Sta(() => ImageCodec.TryDecode(outputs[0], out _));
            Check("long edge is 720",
                decoded is { PixelWidth: 720 },
                decoded is null ? "could not read result" : $"{decoded.PixelWidth}x{decoded.PixelHeight}");
        }

        var rotate = Leaf(tools, "tool.img.rotate.90");
        outputs = await RunAndCheck("rotate 90", rotate, source, FileCategory.Image);
        if (outputs.Count == 1)
        {
            var decoded = Sta(() => ImageCodec.TryDecode(outputs[0], out _));
            Check("rotate swapped the axes",
                decoded is { PixelWidth: 600, PixelHeight: 800 },
                decoded is null ? "could not read result" : $"{decoded.PixelWidth}x{decoded.PixelHeight}");
        }

        await RunAndCheck("grayscale", Leaf(tools, "tool.img.grayscale"), source, FileCategory.Image);
        await RunAndCheck("strip metadata", Leaf(tools, "tool.img.strip"), source, FileCategory.Image);

        var jpegSource = Path.Combine(workspace, "compress-sample.jpg");
        Sta(() =>
        {
            var decodedPng = ImageCodec.TryDecode(source, out _)!;
            ImageCodec.SaveWithWic(decodedPng, jpegSource, 100);
            return true;
        });

        var compress = Leaf(tools, "tool.img.compress.50");
        outputs = await RunAndCheck("compress a jpg", compress, jpegSource, FileCategory.Image);
        if (outputs.Count == 1)
        {
            var before = new FileInfo(jpegSource).Length;
            var after = new FileInfo(outputs[0]).Length;
            Check("compressing shrank it", after < before, $"{Kb(before)} -> {Kb(after)}");
        }

        // Rotating should not also mangle the aspect ratio of a non-square image, and the
        // output format should follow the source rather than silently becoming something else.
        Check("tools keep the source format",
            outputs.Count == 1 && Path.GetExtension(outputs[0]) == ".jpg",
            "jpg in, jpg out");
    }

    // ---- Subtitles --------------------------------------------------------

    private static async Task TestSubtitles(string workspace)
    {
        Section("Subtitles");

        var source = Path.Combine(workspace, "sample.srt");
        await File.WriteAllTextAsync(source,
            "1\r\n00:00:01,000 --> 00:00:03,500\r\n<i>First line</i>\r\n\r\n" +
            "2\r\n00:00:04,000 --> 00:00:06,000\r\nSecond line\r\n");

        var formats = FormatCatalog.For(FileCategory.Subtitle);

        var text = formats.First(f => f.OutputExtension == ".txt");
        var outputs = await RunAndCheck("srt -> txt", text, source, FileCategory.Subtitle);

        if (outputs.Count == 1)
        {
            var content = await File.ReadAllTextAsync(outputs[0]);
            Check("timings stripped", !content.Contains("-->"), "no arrows left");
            Check("tags stripped", !content.Contains("<i>"), "no markup left");
            Check("dialogue kept",
                content.Contains("First line") && content.Contains("Second line"),
                "both lines survived");
        }

        if (FfmpegLocator.IsAvailable)
        {
            var vtt = formats.First(f => f.OutputExtension == ".vtt");
            await RunAndCheck("srt -> vtt", vtt, source, FileCategory.Subtitle);
        }
        else
        {
            Skip("srt -> vtt", "ffmpeg not installed");
        }
    }

    // ---- Archives ---------------------------------------------------------

    private static async Task TestArchives(string workspace)
    {
        Section("Archives");

        var folder = Path.Combine(workspace, "to-archive");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        await File.WriteAllTextAsync(Path.Combine(folder, "one.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(folder, "nested", "two.txt"), "second");

        var zipItem = FormatCatalog.For(FileCategory.Folder).First(f => f.OutputExtension == ".zip");
        var zipped = await Run(zipItem, new[] { new SourceFile(folder) }, FileCategory.Folder);

        Check("folder -> zip", zipped.Outputs.Count == 1 && File.Exists(zipped.Outputs[0]),
            Describe(zipped));

        if (zipped.Outputs.Count == 1)
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(zipped.Outputs[0]);
            Check("zip holds both files", archive.Entries.Count == 2, $"{archive.Entries.Count} entries");
            Check("zip keeps the folder name",
                archive.Entries.All(entry => entry.FullName.StartsWith("to-archive")),
                "entries are prefixed, so it will not explode on extract");

            var extract = new WheelItem("fmt.extract", "Extract", WheelKind.Formats);
            var extracted = await Run(extract,
                new[] { new SourceFile(zipped.Outputs[0]) }, FileCategory.Archive);

            Check("zip -> extract",
                extracted.Outputs.Count == 1 && Directory.Exists(extracted.Outputs[0]),
                Describe(extracted));

            if (extracted.Outputs.Count == 1)
            {
                var files = Directory.GetFiles(extracted.Outputs[0], "*", SearchOption.AllDirectories);
                Check("both files came back out", files.Length == 2, $"{files.Length} files");
            }
        }

        var tarItem = FormatCatalog.For(FileCategory.Folder).First(f => f.OutputExtension == ".tar");
        var tarred = await Run(tarItem, new[] { new SourceFile(folder) }, FileCategory.Folder);
        Check("folder -> tar", tarred.Outputs.Count == 1 && File.Exists(tarred.Outputs[0]),
            Describe(tarred));

        var single = Path.Combine(workspace, "single.txt");
        await File.WriteAllTextAsync(single, new string('x', 5000));

        var gzItem = FormatCatalog.For(FileCategory.Archive).First(f => f.OutputExtension == ".gz");
        var gzipped = await Run(gzItem, new[] { new SourceFile(single) }, FileCategory.Archive);
        Check("file -> gz", gzipped.Outputs.Count == 1 && File.Exists(gzipped.Outputs[0]),
            Describe(gzipped));

        // A gzip member is one file, so a folder must not be offered the slice at all.
        var folderRequest = new JobRequest(gzItem, new[] { new SourceFile(folder) }, FileCategory.Folder);
        Check("gz refused for a folder", !Runner.CanHandle(folderRequest), "slice hidden");
    }

    // ---- Audio ------------------------------------------------------------

    private static async Task TestAudio(string workspace)
    {
        Section("Audio formats");

        var source = Path.Combine(workspace, "tone.wav");
        if (!await GenerateAsync(new[]
            {
                "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
                "-c:a", "pcm_s16le", source
            }))
        {
            Skip("audio", "could not generate a test tone");
            return;
        }

        foreach (var extension in new[] { ".mp3", ".m4a", ".flac", ".ogg", ".opus", ".aiff", ".wma" })
        {
            var item = FormatCatalog.For(FileCategory.Audio)
                .First(i => i.OutputExtension == extension);

            await RunAndCheck($"wav -> {extension}", item, source, FileCategory.Audio);
        }
    }

    private static async Task TestAudioTools(string workspace)
    {
        Section("Audio tools");

        var source = Path.Combine(workspace, "tools-tone.mp3");
        if (!await GenerateAsync(new[]
            {
                "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
                // Deliberately high bitrate, so "compress to 64k" has something to remove.
                "-c:a", "libmp3lame", "-b:a", "320k", source
            }))
        {
            Skip("audio tools", "could not generate a test tone");
            return;
        }

        var tools = ToolCatalog.For(FileCategory.Audio);

        await RunAndCheck("normalize", Leaf(tools, "tool.aud.normalize"), source, FileCategory.Audio);
        await RunAndCheck("to mono", Leaf(tools, "tool.aud.mono"), source, FileCategory.Audio);
        await RunAndCheck("strip tags", Leaf(tools, "tool.aud.strip"), source, FileCategory.Audio);

        var compress = Leaf(tools, "tool.aud.compress.64");
        var outputs = await RunAndCheck("compress to 64k", compress, source, FileCategory.Audio);
        if (outputs.Count == 1)
        {
            var before = new FileInfo(source).Length;
            var after = new FileInfo(outputs[0]).Length;
            Check("compressing shrank it", after < before, $"{Kb(before)} -> {Kb(after)}");
        }

        // Join folds the whole selection, so it needs at least two files.
        var second = Path.Combine(workspace, "tools-tone-2.mp3");
        await GenerateAsync(new[]
        {
            "-f", "lavfi", "-i", "sine=frequency=660:duration=2",
            "-c:a", "libmp3lame", second
        });

        var join = Leaf(tools, "tool.aud.join");
        var joined = await Run(join,
            new[] { new SourceFile(source), new SourceFile(second) }, FileCategory.Audio);

        Check("join two tracks", joined.Outputs.Count == 1 && File.Exists(joined.Outputs[0]),
            Describe(joined));

        if (joined.Outputs.Count == 1)
        {
            var duration = await Ffmpeg.ProbeDurationAsync(joined.Outputs[0]);
            Check("joined length adds up",
                duration is not null && duration.Value.TotalSeconds > 5,
                duration is null ? "unknown" : $"{duration.Value.TotalSeconds:0.0}s of an expected ~6s");
        }

        var singleRequest = new JobRequest(join, new[] { new SourceFile(source) }, FileCategory.Audio);
        Check("join hidden for one file", !Runner.CanHandle(singleRequest), "slice hidden");
    }

    // ---- Video ------------------------------------------------------------

    private static async Task TestVideo(string workspace)
    {
        Section("Video formats");

        var source = Path.Combine(workspace, "clip.mp4");
        if (!await GenerateAsync(new[]
            {
                "-f", "lavfi", "-i", "testsrc=size=320x240:rate=15:duration=2",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source
            }))
        {
            Skip("video", "could not generate a test clip");
            return;
        }

        foreach (var extension in new[] { ".mov", ".mkv", ".webm", ".avi", ".wmv", ".gif", ".mp3" })
        {
            var item = FormatCatalog.For(FileCategory.Video)
                .First(i => i.OutputExtension == extension);

            await RunAndCheck($"mp4 -> {extension}", item, source, FileCategory.Video);
        }
    }

    private static async Task TestVideoTools(string workspace)
    {
        Section("Video tools");

        var source = Path.Combine(workspace, "tools-clip.mp4");
        if (!await GenerateAsync(new[]
            {
                "-f", "lavfi", "-i", "testsrc=size=640x480:rate=15:duration=4",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source
            }))
        {
            Skip("video tools", "could not generate a test clip");
            return;
        }

        var tools = ToolCatalog.For(FileCategory.Video);

        var compress = Leaf(tools, "tool.vid.compress.34");
        var outputs = await RunAndCheck("compress", compress, source, FileCategory.Video);
        if (outputs.Count == 1)
        {
            var before = new FileInfo(source).Length;
            var after = new FileInfo(outputs[0]).Length;
            Check("compressing shrank it", after < before, $"{Kb(before)} -> {Kb(after)}");
        }

        var resize = Leaf(tools, "tool.vid.resize.50");
        outputs = await RunAndCheck("resize to 50%", resize, source, FileCategory.Video);
        if (outputs.Count == 1)
        {
            var size = await Ffmpeg.ProbeSizeAsync(outputs[0]);
            Check("resize really halved it", size is { Width: 320, Height: 240 },
                size is null ? "unknown" : $"{size.Value.Width}x{size.Value.Height}");
        }

        var longEdge = Leaf(tools, "tool.vid.resize.720");
        outputs = await RunAndCheck("resize long edge to 720", longEdge, source, FileCategory.Video);
        if (outputs.Count == 1)
        {
            var size = await Ffmpeg.ProbeSizeAsync(outputs[0]);
            Check("long edge is 640 and untouched, since it was already under 720",
                size is { Width: 640 },
                size is null ? "unknown" : $"{size.Value.Width}x{size.Value.Height}");
        }

        var speed = Leaf(tools, "tool.vid.speed.2");
        outputs = await RunAndCheck("double speed", speed, source, FileCategory.Video);
        if (outputs.Count == 1)
        {
            var duration = await Ffmpeg.ProbeDurationAsync(outputs[0]);
            Check("duration halved",
                duration is not null && duration.Value.TotalSeconds is > 1.5 and < 2.6,
                duration is null ? "unknown" : $"{duration.Value.TotalSeconds:0.0}s of an expected ~2s");
        }

        var rotate = Leaf(tools, "tool.vid.rotate.90");
        outputs = await RunAndCheck("rotate 90", rotate, source, FileCategory.Video);
        if (outputs.Count == 1)
        {
            var size = await Ffmpeg.ProbeSizeAsync(outputs[0]);
            Check("rotate swapped the axes", size is { Width: 480, Height: 640 },
                size is null ? "unknown" : $"{size.Value.Width}x{size.Value.Height}");
        }

        var mute = Leaf(tools, "tool.vid.mute");
        outputs = await RunAndCheck("remove audio", mute, source, FileCategory.Video);
        if (outputs.Count == 1)
        {
            Check("audio really gone", !await Ffmpeg.HasAudioAsync(outputs[0]), "no audio stream");
        }

        await RunAndCheck("snapshot", Leaf(tools, "tool.vid.snapshot"), source, FileCategory.Video);
        await RunAndCheck("extract audio", Leaf(tools, "tool.vid.audio"), source, FileCategory.Video);

        var second = Path.Combine(workspace, "tools-clip-2.mp4");
        await GenerateAsync(new[]
        {
            "-f", "lavfi", "-i", "testsrc=size=640x480:rate=15:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=660:duration=2",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", second
        });

        var join = Leaf(tools, "tool.vid.join");
        var joined = await Run(join,
            new[] { new SourceFile(source), new SourceFile(second) }, FileCategory.Video);

        Check("join two clips", joined.Outputs.Count == 1 && File.Exists(joined.Outputs[0]),
            Describe(joined));

        if (joined.Outputs.Count == 1)
        {
            var duration = await Ffmpeg.ProbeDurationAsync(joined.Outputs[0]);
            Check("joined length adds up",
                duration is not null && duration.Value.TotalSeconds > 5,
                duration is null ? "unknown" : $"{duration.Value.TotalSeconds:0.0}s of an expected ~6s");
        }
    }

    // ---- Harness ----------------------------------------------------------

    private static WheelItem Leaf(IReadOnlyList<WheelItem> items, string id)
    {
        foreach (var item in items)
        {
            if (item.Id == id) return item;

            foreach (var child in item.Children)
            {
                if (child.Id == id) return child;
            }
        }

        throw new InvalidOperationException($"No wheel item with id {id}.");
    }

    private static Task<BatchResult> Run(
        WheelItem item, IReadOnlyList<SourceFile> files, FileCategory category) =>
        Runner.RunAsync(new JobRequest(item, files, category));

    private static async Task<IReadOnlyList<string>> RunAndCheck(
        string label, WheelItem item, string sourcePath, FileCategory category)
    {
        var files = new[] { new SourceFile(sourcePath) };

        if (!Runner.CanHandle(new JobRequest(item, files, category)))
        {
            Skip(label, "no handler claims it");
            return Array.Empty<string>();
        }

        var result = await Run(item, files, category);

        var ok = result.AllSucceeded
                 && result.Outputs.Count > 0
                 && result.Outputs.All(p => File.Exists(p) || Directory.Exists(p))
                 && result.Outputs.All(p => Directory.Exists(p) || new FileInfo(p).Length > 0);

        Check(label, ok, Describe(result));
        return result.Outputs;
    }

    private static string Describe(BatchResult result)
    {
        if (result.Failures.Count > 0) return result.Failures[0];
        if (result.Outputs.Count == 0) return "no output";

        var first = result.Outputs[0];
        if (Directory.Exists(first)) return Path.GetFileName(first) + "/";

        return $"{Path.GetFileName(first)}, {Kb(new FileInfo(first).Length)}";
    }

    /// <summary>Makes a sample file with ffmpeg. Returns false when it could not.</summary>
    private static async Task<bool> GenerateAsync(IEnumerable<string> arguments)
    {
        try
        {
            var outcome = await Ffmpeg.RunAsync(arguments);
            return outcome.Succeeded;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes a PNG with some structure in it, so a resize or a rotate has something to
    /// actually change and a compression has something to lose.
    /// </summary>
    private static void WriteTestPng(string path, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                var checker = ((x / 32) + (y / 32)) % 2 == 0;

                pixels[offset + 0] = (byte)(checker ? x * 255 / width : 20);          // blue
                pixels[offset + 1] = (byte)(checker ? y * 255 / height : 40);         // green
                pixels[offset + 2] = (byte)(checker ? 255 - x * 255 / width : 180);   // red
                pixels[offset + 3] = 255;                                             // alpha
            }
        }

        Sta(() =>
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96,
                PixelFormats.Bgra32, null, pixels, stride);

            ImageCodec.SaveWithWic(bitmap, path, 100);
            return true;
        });
    }

    /// <summary>WIC is apartment-threaded, so even the harness has to ask politely.</summary>
    private static T Sta<T>(Func<T> work) =>
        StaExecutor.RunAsync(work).GetAwaiter().GetResult();

    private static string Kb(long bytes) => $"{bytes / 1024.0:0.#} KB";

    private static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine(name);
        Console.WriteLine(new string('-', name.Length));
    }

    private static void Check(string label, bool condition, string detail)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  pass  {label,-44}  {detail}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  FAIL  {label,-44}  {detail}");
        }
    }

    private static void Skip(string label, string reason)
    {
        _skipped++;
        Console.WriteLine($"  skip  {label,-44}  {reason}");
    }
}
