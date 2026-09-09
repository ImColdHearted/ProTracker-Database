namespace Foot_Tracker.Tracking.Capture;

public static class WindowCaptureServiceFactory
{
    private static IWindowCaptureService? _instance;

    /// <summary>Returns a shared instance for the current OS. Safe to call repeatedly.</summary>
    public static IWindowCaptureService Instance =>
        _instance ??= Create();

    private static IWindowCaptureService Create()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsWindowCaptureService();

        if (OperatingSystem.IsLinux())
            return new LinuxX11WindowCaptureService();

        if (OperatingSystem.IsMacOS())
            return new MacOSWindowCaptureService();

        return new UnsupportedWindowCaptureService(Environment.OSVersion.Platform.ToString());
    }
}
