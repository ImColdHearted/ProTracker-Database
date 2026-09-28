using System.Diagnostics;

namespace Foot_Tracker.Tracking.Capture;

/// <summary>
/// Shared "run a CLI tool and get its output" helper for the shell-out-based
/// capture backends (Linux/wmctrl+import, macOS/osascript+screencapture).
/// </summary>
internal static class ProcessRunner
{
    /// <summary>§300. How long a capture tool gets before it is killed.
    ///
    /// ImageMagick's `import`, handed a -window id that no longer resolves,
    /// does not fail: it falls back to picking a window INTERACTIVELY, which
    /// means grabbing the pointer and waiting for a click. A Linux tester's
    /// log caught it mid-fall ("unable to grab mouse") and his report was
    /// titled "Mouse is a cross". The grab is why a capture helper must never
    /// be waited on without a limit: one that does go interactive would hold
    /// both this thread and the user's pointer until the app was killed.
    ///
    /// Four seconds is twenty times the longest capture in any log here and
    /// a fifth of the watchdog's patience, so a tool that trips this is
    /// genuinely stuck rather than slow.</summary>
    private const int DefaultTimeoutMs = 4000;

    /// <summary>
    /// Runs a process and returns its raw stdout bytes (binary-safe - important for
    /// PNG data, which ReadToEnd()-as-text would corrupt). stderr is drained
    /// concurrently to avoid a classic deadlock where both streams' OS pipe
    /// buffers fill up while only one is being read. Uses ArgumentList (not a
    /// single argument string) so arguments containing spaces/quotes - e.g.
    /// AppleScript source lines - don't need manual shell-style escaping.
    /// </summary>
    public static byte[]? RunCaptureStdout(string fileName, IEnumerable<string> arguments, out string stderr) =>
        RunCaptureStdout(fileName, arguments, DefaultTimeoutMs, out stderr);

    public static byte[]? RunCaptureStdout(
        string fileName, IEnumerable<string> arguments, int timeoutMs, out string stderr)
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
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        // §300: the timeout covers the WAIT, not the reads - a process that
        // is holding a pointer grab is not writing to either pipe, so reading
        // stderr to the end first (as this used to) would block before the
        // timeout could ever be reached.
        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
            catch
            {
                // Already gone, or not ours to kill; either way there is
                // nothing further to do about it.
            }

            stderr =
                $"'{fileName}' did not finish within {timeoutMs}ms and was stopped. " +
                "On Linux this is what an `import` that fell back to picking a window " +
                "interactively looks like - it grabs the pointer and waits for a click.";

            return null;
        }

        // The timed overload can return before the redirected streams are
        // finished; the parameterless one is what waits for them.
        process.WaitForExit();

        copyTask.GetAwaiter().GetResult();
        stderr = stderrTask.GetAwaiter().GetResult();

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
