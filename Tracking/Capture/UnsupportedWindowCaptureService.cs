namespace Foot_Tracker.Tracking.Capture;

/// <summary>
/// Used on any OS without a real IWindowCaptureService implementation yet (macOS,
/// for now). Never throws - always returns empty results with a clear explanation,
/// so the rest of the app (hunt timer, manual stats, everything else) keeps working.
/// </summary>
public sealed class UnsupportedWindowCaptureService : IWindowCaptureService
{
    public string PlatformName { get; }
    public bool IsAvailable => false;
    public string? LastError { get; private set; }
    public bool HasSelectedClient => false;

    public UnsupportedWindowCaptureService(string platformName)
    {
        PlatformName = platformName;
        LastError = $"Live window capture is not yet implemented for {platformName}.";
    }

    public IReadOnlyList<ClientWindowInfo> FindClientWindows(string processName) =>
        Array.Empty<ClientWindowInfo>();

    public void SelectWindow(long handle) { }
    public void ClearSelectedWindow() { }
    public byte[]? CaptureSelectedWindowPng() => null;
}
