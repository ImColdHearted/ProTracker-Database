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

    // §300. The bound window has gone, and when that was last checked.
    //
    // A Linux tester's client window closed while the tracker was bound to
    // it. Every capture from then on ran `import -window 0x260000a`, which
    // answered "no window with specified ID exists" and then - this is the
    // part that matters - fell back to picking a window INTERACTIVELY:
    // "import: unable to grab mouse". The scan loop repeats five times a
    // second, so his pointer became a crosshair and stayed one. His report
    // was titled "Mouse is a cross".
    //
    // So a failed capture asks wmctrl whether the window is still there, and
    // if it is not, no capture tool is run again until it comes back. The
    // check is the failure path's alone: a working capture never pays for it.
    private bool _windowMissing;
    private DateTime _windowCheckedUtc = DateTime.MinValue;

    // Long enough that the re-check costs a fraction of what the failing
    // captures it replaces did, short enough that a client which comes back
    // on the same window id is picked up while the player is still looking
    // at the status line.
    private static readonly TimeSpan WindowRecheckInterval = TimeSpan.FromSeconds(2);

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

        // §300: a deliberate (re)bind is a fresh start - the new handle has
        // not failed yet, whatever the old one did.
        _windowMissing = false;
        _windowCheckedUtc = DateTime.MinValue;

        // §114 recorded the session type here because wmctrl and import
        // talk to an X server, and the theory was that under Wayland an
        // XWayland window would still be LISTED - so the app believes it has
        // bound a client - while the frames coming back were stale or black.
        //
        // §117: that theory was WRONG, and §114's own logging is what
        // disproved it. Three separate runs on an Ubuntu 24.04 Wayland
        // session logged "Capture 1024x640, mean brightness 161/255 - ok"
        // and "127/255 - ok" - real, non-black frames, captured through
        // XWayland exactly as they are under Xorg. The actual failure in
        // those runs was a missing OCR native library, several layers away
        // from capture (see §115 and §116).
        //
        // So the session type is still worth recording - it is free, and it
        // is the first thing anyone asks about a Linux capture report - but
        // it is recorded as a FACT and nothing more. The old text told the
        // reader to log out and switch to Xorg, which is a genuinely costly
        // thing to ask, and it was the loudest line in a log whose real
        // cause sat forty lines below it. A warning that confidently names
        // the wrong culprit is worse than no warning: it spends the
        // reader's attention and buys a wrong diagnosis with it.
        string sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "(unset)";
        bool waylandDisplay = !string.IsNullOrEmpty(
            Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

        TrackerDiagnostics.LogCaptureEnvironment(
            $"Linux/wmctrl+import; XDG_SESSION_TYPE={sessionType}; " +
            $"WAYLAND_DISPLAY set={waylandDisplay}");
    }

    public void ClearSelectedWindow()
    {
        _selectedHandle = 0;
        _hasSelection = false;
        _windowMissing = false;
        _windowCheckedUtc = DateTime.MinValue;
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

        // §300: while the window is known to be gone, run nothing at all.
        // This is the whole fix for the crosshair - a tool that is never
        // started cannot grab the pointer - and it re-checks on its own, so
        // a client that comes back on the same id needs no click from anyone.
        if (_windowMissing)
        {
            if (DateTime.UtcNow - _windowCheckedUtc < WindowRecheckInterval)
            {
                LastError = MissingWindowMessage(windowIdArg);
                return null;
            }

            _windowCheckedUtc = DateTime.UtcNow;

            if (!WindowStillListed(_selectedHandle))
            {
                LastError = MissingWindowMessage(windowIdArg);
                return null;
            }

            _windowMissing = false;
        }

        bool toolRan = false;

        if (ProcessRunner.IsToolAvailable("import"))
        {
            toolRan = true;

            byte[]? png = ProcessRunner.RunCaptureStdout("import", new[] { "-window", windowIdArg, "png:-" }, out string stderr);

            if (png is { Length: > 0 })
                return png;

            LastError = string.IsNullOrWhiteSpace(stderr)
                ? "import produced no image data."
                : $"import failed: {stderr.Trim()}";
        }

        if (ProcessRunner.IsToolAvailable("maim"))
        {
            toolRan = true;

            byte[]? png = ProcessRunner.RunCaptureStdout("maim", new[] { "-i", windowIdArg }, out string stderr);

            if (png is { Length: > 0 })
                return png;

            LastError = string.IsNullOrWhiteSpace(stderr)
                ? "maim produced no image data."
                : $"maim failed: {stderr.Trim()}";
        }

        LastError ??= "No supported screenshot tool (import/maim) is available.";

        // §300: a capture tool ran and did not produce a frame. Was it the
        // window, or the tool? wmctrl - already required, and already how
        // this class finds windows in the first place - is asked rather than
        // the answer being guessed from the wording of someone else's error
        // message, which is localised: the tester's read "Ο πόρος είναι
        // προσωρινά μη διαθέσιμος".
        //
        // Only when a tool actually ran, so a machine with neither installed
        // does not spawn a wmctrl five times a second for an answer that
        // cannot help it; and no more often than the re-check interval, so
        // neither does a tool that is failing for its own reasons.
        if (!toolRan || DateTime.UtcNow - _windowCheckedUtc < WindowRecheckInterval)
            return null;

        _windowCheckedUtc = DateTime.UtcNow;

        if (!WindowStillListed(_selectedHandle))
        {
            _windowMissing = true;
            LastError = MissingWindowMessage(windowIdArg);
        }

        return null;
    }

    /// <summary>§300. Is this window id still one the window manager lists?
    /// Same read as FindClientWindows, without the per-window /proc work -
    /// the question here is only whether the id exists.</summary>
    private static bool WindowStillListed(long handle)
    {
        try
        {
            byte[]? output = ProcessRunner.RunCaptureStdout("wmctrl", new[] { "-l" }, out _);

            if (output is null)
            {
                // wmctrl itself is not answering. That is not evidence the
                // window has gone, and treating it as such would stop
                // captures for a reason that has nothing to do with them.
                return true;
            }

            string text = System.Text.Encoding.UTF8.GetString(output);

            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length == 0)
                    continue;

                if (TryParseWindowId(parts[0], out long listed) && listed == handle)
                    return true;
            }

            return false;
        }
        catch
        {
            // Same reasoning as a wmctrl that returns nothing.
            return true;
        }
    }

    private static string MissingWindowMessage(string windowIdArg) =>
        $"The PRO client window {windowIdArg} no longer exists - it was closed, or the client " +
        "was restarted and has a new one. Nothing is being captured until it is back; pick the " +
        "client again if it has restarted.";

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
