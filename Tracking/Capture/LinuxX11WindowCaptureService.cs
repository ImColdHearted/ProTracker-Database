using System.Globalization;
using System.Runtime.Versioning;

namespace Foot_Tracker.Tracking.Capture;

/// <summary>
/// Linux window finding/capture, targeting X11 (including XWayland - Wayland apps
/// running under an XWayland compatibility layer show up here too). Native Wayland
/// windows do NOT show up here - Wayland deliberately restricts window listing and
/// screen capture to a permissioned portal (D-Bus + PipeWire), which is a separate,
/// larger implementation. See MIGRATION_GUIDE.md.
///
/// Deliberately shells out to well-known CLI tools (wmctrl, import/maim) instead of
/// P/Invoking libX11 directly:
///   - Far less native-interop surface area to get wrong without a Linux machine
///     to test on.
///   - If something fails, a tester can run the exact same command themselves
///     (e.g. `wmctrl -l -p`) and paste the output/error back - much easier to
///     debug blind than a native crash.
///   - These tools are small, common, and scriptable (`sudo apt install wmctrl
///     imagemagick` on Debian/Ubuntu, `sudo dnf install wmctrl ImageMagick` on
///     Fedora, `sudo pacman -S wmctrl imagemagick` on Arch).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxX11WindowCaptureService : IWindowCaptureService
{
    private long _selectedHandle;
    private bool _hasSelection;

    public string PlatformName => "Linux (X11)";
    public string? LastError { get; private set; }
    public bool HasSelectedClient => _hasSelection;

    public bool IsAvailable
    {
        get
        {
            if (!ProcessRunner.IsToolAvailable("wmctrl"))
            {
                LastError = "wmctrl is not installed. Install it with your package manager, " +
                             "e.g. 'sudo apt install wmctrl' (Debian/Ubuntu), " +
                             "'sudo dnf install wmctrl' (Fedora), or 'sudo pacman -S wmctrl' (Arch).";
                return false;
            }

            if (!ProcessRunner.IsToolAvailable("import") && !ProcessRunner.IsToolAvailable("maim"))
            {
                LastError = "Neither ImageMagick's 'import' nor 'maim' is installed - one is " +
                             "needed to capture window contents. Install with " +
                             "'sudo apt install imagemagick' or 'sudo apt install maim' " +
                             "(package names vary slightly by distro).";
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

            byte[]? output = ProcessRunner.RunCaptureStdout("wmctrl", new[] { "-l", "-p" }, out string stderr);

            if (output is null)
            {
                LastError = string.IsNullOrWhiteSpace(stderr)
                    ? "wmctrl did not return any output. Is it installed and on PATH?"
                    : $"wmctrl failed: {stderr.Trim()}";
                return results;
            }

            string text = System.Text.Encoding.UTF8.GetString(output);

            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // Format: <window_id> <desktop> <pid> <client_machine> <title...>
                string[] parts = line.Split((char[]?)null, 5, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5)
                    continue;

                string windowIdText = parts[0];
                string pidText = parts[2];
                string title = parts[4];

                if (!TryParseWindowId(windowIdText, out long windowId))
                    continue;

                if (!int.TryParse(pidText, out int pid))
                    continue;

                if (!ProcessNameMatches(pid, processName))
                    continue;

                results.Add(new ClientWindowInfo
                {
                    Handle = windowId,
                    ProcessId = pid,
                    DisplayName = string.IsNullOrWhiteSpace(title) ? $"{processName} - PID {pid}" : title
                });
            }
        }
        catch (Exception ex)
        {
            LastError = $"Could not enumerate windows via wmctrl: {ex.Message}";
        }

        return results;
    }

    public void SelectWindow(long handle)
    {
        _selectedHandle = handle;
        _hasSelection = true;
    }

    public void ClearSelectedWindow()
    {
        _selectedHandle = 0;
        _hasSelection = false;
    }

    public byte[]? CaptureSelectedWindowPng()
    {
        if (!_hasSelection)
        {
            LastError = "No window is currently selected.";
            return null;
        }

        LastError = null;

        // ImageMagick's `import` accepts the X11 window id in decimal or 0x-hex.
        string windowIdArg = "0x" + _selectedHandle.ToString("x", CultureInfo.InvariantCulture);

        if (ProcessRunner.IsToolAvailable("import"))
        {
            byte[]? png = ProcessRunner.RunCaptureStdout("import", new[] { "-window", windowIdArg, "png:-" }, out string stderr);

            if (png is { Length: > 0 })
                return png;

            LastError = string.IsNullOrWhiteSpace(stderr)
                ? "import produced no image data."
                : $"import failed: {stderr.Trim()}";
        }

        if (ProcessRunner.IsToolAvailable("maim"))
        {
            byte[]? png = ProcessRunner.RunCaptureStdout("maim", new[] { "-i", windowIdArg }, out string stderr);

            if (png is { Length: > 0 })
                return png;

            LastError = string.IsNullOrWhiteSpace(stderr)
                ? "maim produced no image data."
                : $"maim failed: {stderr.Trim()}";
        }

        LastError ??= "No supported screenshot tool (import/maim) is available.";
        return null;
    }

    private static bool ProcessNameMatches(int pid, string processName)
    {
        try
        {
            // /proc/<pid>/comm holds the kernel-recorded process name (truncated to
            // 15 chars, not an issue for names like "PROClient"). Fall back to
            // reading cmdline's first token if comm can't be read.
            string commPath = $"/proc/{pid}/comm";

            if (File.Exists(commPath))
            {
                string comm = File.ReadAllText(commPath).Trim();
                if (comm.Equals(processName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            string cmdlinePath = $"/proc/{pid}/cmdline";

            if (File.Exists(cmdlinePath))
            {
                string cmdline = File.ReadAllText(cmdlinePath);
                string firstArg = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                string exeName = Path.GetFileName(firstArg);

                return exeName.Contains(processName, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Process may have exited mid-scan, or /proc may not be readable - skip it.
        }

        return false;
    }

    private static bool TryParseWindowId(string text, out long value)
    {
        text = text.Trim();

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

        return long.TryParse(text, out value);
    }
}
