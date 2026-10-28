using System.Diagnostics;
using System.Text;

namespace Satsuma.Core.Tools;

public sealed record ProcessOutcome(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// The last few lines of stderr, which is where console tools put the actual reason
    /// they failed. Used for the message shown on the result toast.
    /// </summary>
    public string Tail(int lines = 3)
    {
        var all = StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.TrimEnd('\r'))
                        .Where(l => l.Trim().Length > 0)
                        .ToList();
        return all.Count == 0
            ? $"Exit code {ExitCode}"
            : string.Join(" ", all.TakeLast(lines)).Trim();
    }
}

/// <summary>Runs a console tool without ever showing a console window.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessOutcome> RunAsync(
        string exePath,
        IEnumerable<string> arguments,
        Action<string>? onStdOutLine = null,
        Action<string>? onStdErrLine = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdOut.Append(e.Data).Append('\n');
            onStdOutLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdErr.Append(e.Data).Append('\n');
            onStdErrLine?.Invoke(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // ffmpeg reads stdin and will sit waiting on it if we leave it open.
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessOutcome(process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already gone */ }
        catch (System.ComponentModel.Win32Exception) { /* already gone */ }
    }
}
