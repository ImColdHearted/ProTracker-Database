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
/// EncounterDetector, CatchDetector, RareEncounterDetector) - those still use
/// System.Drawing.Common directly and remain Windows-only. See MIGRATION_GUIDE.md
/// for why that's a separate, larger follow-up task.
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

    void SelectWindow(long handle);
    void ClearSelectedWindow();
    bool HasSelectedClient { get; }

    /// <summary>Captures the currently-selected window as PNG-encoded bytes, or null on failure (see LastError).</summary>
    byte[]? CaptureSelectedWindowPng();
}
