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
            byte[]? output = RunCaptureStdout("which", new[] { toolName }, out _);
            return output is { Length: > 0 };
        }
        catch
        {
            return false;
        }
    }
}
