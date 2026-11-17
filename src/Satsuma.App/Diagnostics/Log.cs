using System.Text;

namespace Satsuma.App.Diagnostics;

/// <summary>
/// Opt-in trace log, off unless asked for.
///
/// The interesting half of Satsuma happens inside global input hooks during somebody
/// else's modal drag loop, where there is nothing to attach a debugger to and no window to
/// print into. When a drag "does nothing", this is the only way to tell which link in the
/// chain broke: the hook, the drag detection, the key binding, or the wheel itself.
///
/// Turn it on with --debug, or by setting SATSUMA_DEBUG=1.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static bool Enabled { get; private set; }

    public static string PathHint => _path ?? "(not enabled)";

    public static void Enable(IReadOnlyList<string> args)
    {
        var asked = args.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase))
                    || Environment.GetEnvironmentVariable("SATSUMA_DEBUG") == "1";

        if (!asked) return;

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Satsuma");
            Directory.CreateDirectory(directory);

            _path = Path.Combine(directory, "satsuma.log");

            // Start clean each run, so a log always describes one session.
            File.WriteAllText(_path, $"Satsuma trace {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
            Enabled = true;
        }
        catch (Exception)
        {
            Enabled = false;
        }
    }

    public static void Write(string message)
    {
        if (!Enabled || _path is null) return;

        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    _path,
                    $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Tracing must never be the thing that breaks a run.
        }
    }

    /// <summary>
    /// Records something that must be recorded whether or not tracing was asked for,
    /// appended to a file of its own that survives the next run.
    ///
    /// The trace log is opt-in and starts clean every run, which is right for tracing
    /// and useless for a death: nobody is running with --debug when the thing they did
    /// not expect happens, and the next start would wipe it anyway. Satsuma went missing
    /// for three days with no crash record of any kind -- this machine writes no Windows
    /// error reports either -- and the only honest conclusion was that it had died
    /// somewhere invisible. Once is enough for that.
    /// </summary>
    public static void Keep(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Satsuma");

            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "crashes.log");

            lock (Gate)
            {
                // Trimmed when it gets silly, oldest first, so this cannot grow without
                // limit on a machine where something fails every few minutes.
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                {
                    var kept = File.ReadAllLines(path);
                    File.WriteAllLines(path, kept.Skip(kept.Length / 2), Encoding.UTF8);
                }

                File.AppendAllText(
                    path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Still never the thing that breaks a run.
        }

        // Also in the trace, where somebody watching a repro will be looking.
        Write(message);
    }

    /// <summary>Where <see cref="Keep"/> writes, for telling a user where to look.</summary>
    public static string CrashPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Satsuma", "crashes.log");
}
