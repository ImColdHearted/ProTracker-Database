using System.Runtime.Versioning;

namespace Foot_Tracker.Tracking.Capture;

/// <summary>
/// macOS window finding/capture using tools that ship with every stock macOS
/// install - no extra packages needed (an advantage over the Linux backend,
/// which needs wmctrl + import/maim installed separately):
///   - `ps` to find PROClient's process id(s).
///   - `osascript` (AppleScript, via System Events) to read that process's
///     front window position/size/title.
///   - `screencapture -R x,y,w,h` to grab that screen region to a PNG file.
///
/// Two macOS permission prompts are involved the first time this runs - both
/// are one-time grants in System Settings > Privacy & Security:
///   - "Automation" access for this app to control System Events (needed for
///     the AppleScript window-bounds query).
///   - "Screen Recording" access for this app (needed for screencapture to
///     return real pixel data instead of a black/empty image).
/// If either is missing, macOS does not always give a clean error back to the
/// calling process - a blank/black capture with no exception is the most
/// common symptom. See MIGRATION_GUIDE.md.
///
/// Known limitation: unlike Windows' PrintWindow or a true CGWindowID-based
/// capture, `screencapture -R` grabs a rectangular region of the screen, not
/// the window's own compositor buffer. If another window overlaps the PRO
/// client while scanning, the capture will show whatever is on top instead.
/// This is a deliberate v1 tradeoff to avoid hand-written Core Foundation
/// P/Invoke marshaling (CGWindowListCopyWindowInfo/CGWindowListCreateImage)
/// that can't be verified without a Mac to test against - see the guide for
/// the upgrade path if occlusion turns out to be a real problem in practice.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacOSWindowCaptureService : IWindowCaptureService
{
    private int _selectedProcessId;
    private bool _hasSelection;

    public string PlatformName => "macOS";
    public string? LastError { get; private set; }
    public bool HasSelectedClient => _hasSelection;

    public bool IsAvailable
    {
        get
        {
            // osascript and screencapture ship with every stock macOS install,
            // so this is mostly a defensive check for unusual/minimal setups.
            if (!ProcessRunner.IsToolAvailable("osascript") || !ProcessRunner.IsToolAvailable("screencapture"))
            {
                LastError = "osascript/screencapture were not found. These ship with macOS by " +
                             "default - if they're missing, something unusual is going on with this system.";
                return false;
            }

            return true;
        }
    }

    public IReadOnlyList<ClientWindowInfo> FindClientWindows(string processName)
    {
        var results = new List<ClientWindowInfo>();

        try
        {
            LastError = null;

            byte[]? output = ProcessRunner.RunCaptureStdout(
                "ps", new[] { "-Ao", "pid=,comm=" }, out string stderr);

            if (output is null)
            {
                LastError = string.IsNullOrWhiteSpace(stderr)
                    ? "ps did not return any output."
                    : $"ps failed: {stderr.Trim()}";
                return results;
            }

            string text = System.Text.Encoding.UTF8.GetString(output);

            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                int firstSpace = trimmed.IndexOf(' ');
                if (firstSpace <= 0)
                    continue;

                string pidText = trimmed[..firstSpace];
                string comm = trimmed[(firstSpace + 1)..].Trim();

                if (!int.TryParse(pidText, out int pid))
                    continue;

                // `ps comm` on macOS usually shows the full executable path
                // (e.g. /Applications/PROClient.app/Contents/MacOS/PROClient).
                string exeName = Path.GetFileName(comm);

                if (!exeName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!TryGetFrontWindowInfo(pid, out string title, out _))
                {
                    // Process exists but has no visible front window yet (e.g.
                    // still launching) - still list it, just with a generic name.
                    title = $"{processName} - PID {pid}";
                }

                results.Add(new ClientWindowInfo
                {
                    // No stable CGWindowID in this approach - the PID doubles as
                    // the "handle" and window bounds are re-queried live on every
                    // capture (see CaptureSelectedWindowPng), which also means
                    // window moves/resizes between scans are handled for free.
                    Handle = pid,
                    ProcessId = pid,
                    DisplayName = title
                });
            }
        }
        catch (Exception ex)
        {
            LastError = $"Could not enumerate windows via ps: {ex.Message}";
        }

        return results;
    }

    public void SelectWindow(long handle)
    {
        _selectedProcessId = (int)handle;
        _hasSelection = true;
    }

    public void ClearSelectedWindow()
    {
        _selectedProcessId = 0;
        _hasSelection = false;
    }

    public byte[]? CaptureSelectedWindowPng()
    {
        if (!_hasSelection)
        {
            LastError = "No window is currently selected.";
            return null;
        }

        if (!TryGetFrontWindowInfo(_selectedProcessId, out _, out (int X, int Y, int Width, int Height) bounds))
        {
            LastError ??= "The PRO client window could not be located (it may be minimized or closed).";
            return null;
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            LastError = "The PRO client window has no visible area to capture.";
            return null;
        }

        string tempPath = Path.Combine(Path.GetTempPath(), $"protracker-capture-{Guid.NewGuid():N}.png");

        try
        {
            string region = $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}";

            byte[]? output = ProcessRunner.RunCaptureStdout(
                "screencapture",
                new[] { "-x", "-R" + region, tempPath },
                out string stderr);

            // screencapture writes to the given file path rather than stdout, so a
            // null/empty stdout here is normal - check the file instead.
            _ = output;

            if (!File.Exists(tempPath))
            {
                LastError = string.IsNullOrWhiteSpace(stderr)
                    ? "screencapture did not produce an image. This usually means Screen " +
                      "Recording permission hasn't been granted (System Settings > Privacy " +
                      "& Security > Screen Recording)."
                    : $"screencapture failed: {stderr.Trim()}";
                return null;
            }

            byte[] pngBytes = File.ReadAllBytes(tempPath);

            if (pngBytes.Length == 0)
            {
                LastError = "screencapture produced an empty file - check Screen Recording permission.";
                return null;
            }

            LastError = null;
            return pngBytes;
        }
        catch (Exception ex)
        {
            LastError = $"Window capture failed: {ex.Message}";
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    /// <summary>
    /// Queries the given process's front window via AppleScript (System Events).
    /// Requires the user to have granted this app "Automation" access to control
    /// System Events - see the class-level remarks.
    /// </summary>
    private static bool TryGetFrontWindowInfo(int pid, out string title, out (int X, int Y, int Width, int Height) bounds)
    {
        title = string.Empty;
        bounds = default;

        string[] scriptLines =
        {
            "on run argv",
            "  set targetPid to (item 1 of argv) as integer",
            "  tell application \"System Events\"",
            "    tell (first process whose unix id is targetPid)",
            "      set winPos to position of front window",
            "      set winSize to size of front window",
            "      set winTitle to \"\"",
            "      try",
            "        set winTitle to name of front window",
            "      end try",
            "      return ((item 1 of winPos) as string) & \",\" & ((item 2 of winPos) as string) & \",\" & ((item 1 of winSize) as string) & \",\" & ((item 2 of winSize) as string) & \",\" & winTitle",
            "    end tell",
            "  end tell",
            "end run"
        };

        var arguments = new List<string>();
        foreach (string line in scriptLines)
        {
            arguments.Add("-e");
            arguments.Add(line);
        }
        arguments.Add(pid.ToString());

        byte[]? output = ProcessRunner.RunCaptureStdout("osascript", arguments, out string stderr);

        if (output is null)
        {
            return false;
        }

        string resultText = System.Text.Encoding.UTF8.GetString(output).Trim();
        string[] parts = resultText.Split(',', 5);

        if (parts.Length < 4 ||
            !int.TryParse(parts[0].Trim(), out int x) ||
            !int.TryParse(parts[1].Trim(), out int y) ||
            !int.TryParse(parts[2].Trim(), out int width) ||
            !int.TryParse(parts[3].Trim(), out int height))
        {
            return false;
        }

        bounds = (x, y, width, height);
        title = parts.Length >= 5 ? parts[4].Trim() : string.Empty;

        return true;
    }
}
