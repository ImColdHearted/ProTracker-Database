namespace Foot_Tracker.Tracking.Capture;

/// <summary>
/// Finds and captures the PRO game client window. Implementations are entirely
/// platform-specific - see WindowCaptureServiceFactory.Create() for how the right
/// one gets picked at runtime.
///
/// This interface only covers finding/selecting a client window and grabbing a raw
/// screenshot of it (PNG bytes - the one image format every platform can produce
/// and every imaging library can decode, so it's a safe interchange format here).
/// It does NOT cover the OCR/encounter-detection pipeline (BattleWindowLocator,
/// EncounterDetector, CatchDetector, RareEncounterDetector) - those are a separate
/// concern living in ImageOps.cs and the detector classes themselves.
///
/// Historical note: this comment used to say the detection pipeline "still uses
/// System.Drawing.Common directly and remains Windows-only" - true when this
/// interface was first written (Phase 1, window finding/capture only), false
/// since Phase 2 ported the whole OCR pipeline to SkiaSharp + TesseractOCR
/// specifically so it would run on every OS. See MIGRATION_GUIDE.md §7.
/// </summary>
public interface IWindowCaptureService
{
    /// <summary>Human-readable OS/backend name, shown in status messages (e.g. "Windows", "Linux (X11)").</summary>
    string PlatformName { get; }

    /// <summary>
    /// True if this backend can actually run on the current machine (e.g. the
    /// Linux backend needs wmctrl + import/maim installed). When false, callers
    /// should show <see cref="LastError"/> instead of attempting to use it.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Set whenever a find/capture call fails, with a message meant to be shown directly to the user.</summary>
    string? LastError { get; }

    /// <summary>Finds all currently-running windows belonging to the given process name (e.g. "PROClient").</summary>
    IReadOnlyList<ClientWindowInfo> FindClientWindows(string processName);

    /// <summary>
    /// §140. Whether the game is running at all - the question the hunt asks
    /// every few seconds so it can stop itself when the client is closed
    /// (MainWindowViewModel.StopHuntIfClientGone). Deliberately about the
    /// PROCESS, not a capturable window: a minimized client is still open,
    /// and a hunt must not be stopped over it. The default answer is "any
    /// window found" - right on Linux, where wmctrl lists minimized windows
    /// too, and on macOS, whose enumeration is process-based already; the
    /// Windows backend overrides it because its window finder skips
    /// minimized windows on purpose. A failed enumeration is not "gone":
    /// LastError is set by every FindClientWindows failure, and this answers
    /// true in that case rather than stop a hunt over a query that did not
    /// run.
    /// </summary>
    bool IsClientProcessRunning(string processName)
    {
        IReadOnlyList<ClientWindowInfo> clients = FindClientWindows(processName);

        return clients.Count > 0 || LastError is not null;
    }

    void SelectWindow(long handle);
    void ClearSelectedWindow();
    bool HasSelectedClient { get; }

    /// <summary>Captures the currently-selected window as PNG-encoded bytes, or null on failure (see LastError).</summary>
    byte[]? CaptureSelectedWindowPng();
}
