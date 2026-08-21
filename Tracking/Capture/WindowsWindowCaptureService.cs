using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace Foot_Tracker.Tracking.Capture;

/// <summary>
/// Wraps the existing, already-tested ProWindowFinder + ScreenCapture (Win32/GDI)
/// code behind IWindowCaptureService. Behavior is unchanged from before this
/// abstraction existed - this is purely a thin adapter so the ViewModels can go
/// through one interface regardless of OS.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWindowCaptureService : IWindowCaptureService
{
    public string PlatformName => "Windows";
    public bool IsAvailable => true;
    public string? LastError { get; private set; }
    public bool HasSelectedClient => ScreenCapture.HasSelectedClient;

    public IReadOnlyList<ClientWindowInfo> FindClientWindows(string processName)
    {
        try
        {
            LastError = null;

            // ProWindowFinder is hardcoded to "PROClient" today; processName is
            // accepted here for interface symmetry with the Linux backend.
            return ProWindowFinder.FindAllProWindows()
                .Select(c => new ClientWindowInfo
                {
                    Handle = c.Handle.ToInt64(),
                    ProcessId = c.ProcessId,
                    DisplayName = c.DisplayName
                })
                .ToList();
        }
        catch (Exception ex)
        {
            LastError = $"Could not enumerate PRO client windows: {ex.Message}";
            return Array.Empty<ClientWindowInfo>();
        }
    }

    public void SelectWindow(long handle) =>
        ScreenCapture.SelectProWindow(new IntPtr(handle));

    public void ClearSelectedWindow() =>
        ScreenCapture.ClearSelectedProWindow();

    public byte[]? CaptureSelectedWindowPng()
    {
        try
        {
            LastError = null;

            using Bitmap? bitmap = ScreenCapture.CaptureProWindow();

            if (bitmap is null)
            {
                LastError = "The PRO client window could not be captured (it may be minimized or closed).";
                return null;
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception ex)
        {
            LastError = $"Window capture failed: {ex.Message}";
            return null;
        }
    }
}
