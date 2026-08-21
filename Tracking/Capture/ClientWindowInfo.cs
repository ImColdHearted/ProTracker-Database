namespace Foot_Tracker.Tracking.Capture;

/// <summary>
/// Platform-agnostic stand-in for a found PRO client window. On Windows this wraps
/// a Win32 HWND; on Linux, an X11 window ID (via wmctrl). Both fit in a long.
/// </summary>
public sealed class ClientWindowInfo
{
    public required long Handle { get; init; }
    public required int ProcessId { get; init; }
    public required string DisplayName { get; init; }
}
