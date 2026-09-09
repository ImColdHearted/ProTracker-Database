using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace Foot_Tracker.Services;

// Ported verbatim from WinForms. Capturing another process's window client area via
// GDI (CopyFromScreen) is a Windows-only capability - it is not a WinForms limitation,
// so switching UI frameworks doesn't change this. See MIGRATION_GUIDE.md.
[SupportedOSPlatform("windows")]
public static class WindowCaptureService
{
    public static Bitmap? CaptureClientArea(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
            return null;

        // A minimized window cannot be captured with CopyFromScreen.
        if (NativeMethods.IsIconic(windowHandle))
            return null;

        if (!NativeMethods.GetClientRect(
                windowHandle,
                out NativeMethods.NativeRect clientRectangle))
        {
            return null;
        }

        if (clientRectangle.Width <= 0 ||
            clientRectangle.Height <= 0)
        {
            return null;
        }

        NativeMethods.NativePoint upperLeft = new()
        {
            X = clientRectangle.Left,
            Y = clientRectangle.Top
        };

        if (!NativeMethods.ClientToScreen(
                windowHandle,
                ref upperLeft))
        {
            return null;
        }

        Bitmap bitmap = new(
            clientRectangle.Width,
            clientRectangle.Height,
            PixelFormat.Format32bppArgb);

        try
        {
            using Graphics graphics = Graphics.FromImage(bitmap);

            graphics.CopyFromScreen(
                upperLeft.X,
                upperLeft.Y,
                0,
                0,
                bitmap.Size,
                CopyPixelOperation.SourceCopy);

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            return null;
        }
    }
}
