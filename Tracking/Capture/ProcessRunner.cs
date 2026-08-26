using System.Diagnostics;

namespace Foot_Tracker.Tracking.Capture;

/// <summary>
/// Shared "run a CLI tool and get its output" helper for the shell-out-based
/// capture backends (Linux/wmctrl+import, macOS/osascript+screencapture).
/// </summary>
internal static class ProcessRunner
{
    /// <summary>
    /// Runs a process and returns its raw stdout bytes (binary-safe - important for
    /// PNG data, which ReadToEnd()-as-text would corrupt). stderr is drained
    /// concurrently to avoid a classic deadlock where both streams' OS pipe
    /// buffers fill up while only one is being read. Uses ArgumentList (not a
    /// single argument string) so arguments containing spaces/quotes - e.g.
    /// AppleScript source lines - don't need manual shell-style escaping.
    /// </summary>
    public static byte[]? RunCaptureStdout(string fileName, IEnumerable<string> arguments, out string stderr)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string arg in arguments)
            startInfo.ArgumentList.Add(arg);

        using Process? process = Process.Start(startInfo);

        if (process is null)
        {
            stderr = $"Could not start '{fileName}'.";
            return null;
        }

        using var stdoutBuffer = new MemoryStream();
        Task copyTask = process.StandardOutput.BaseStream.CopyToAsync(stdoutBuffer);

        stderr = process.StandardError.ReadToEnd();

        copyTask.GetAwaiter().GetResult();
        process.WaitForExit();

        return process.ExitCode == 0 ? stdoutBuffer.ToArray() : null;
    }

    public static bool IsToolAvailable(string toolName)
    {
        try
        {
            // "command -v" is a POSIX shell builtin, unlike the external "which"
            // binary this used to call directly - some minimal Linux distros and
            // container base images don't ship "which" at all (it's a separate,
            // sometimes-optional package on top of the shell itself), which would
            // make every one of these checks report a tool as "not installed"
            // even when it genuinely is. "command -v" is guaranteed to exist
            // anywhere /bin/sh does, which is effectively everywhere. toolName is
            // passed as "$1" (a shell positional parameter) rather than
            // interpolated into the command string, so it can't be interpreted as
            // shell syntax - moot today since every call site passes a hardcoded
            // literal ("wmctrl", "import", "maim"), but cheap insurance.
            byte[]? output = RunCaptureStdout(
                "/bin/sh",
                new[] { "-c", "command -v \"$1\"", "_", toolName },
                out _);
            return output is { Length: > 0 };
        }
        catch
        {
            return false;
        }
    }
}
