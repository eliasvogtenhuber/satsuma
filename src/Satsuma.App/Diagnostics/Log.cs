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
}
