using Foot_Tracker.Models;
using SkiaSharp;

namespace Foot_Tracker.Services;

/// <summary>
/// Ported from WinForms System.Drawing (Bitmap/Graphics) to SkiaSharp, which Avalonia already
/// depends on internally and which works identically on Windows, Linux, and macOS.
/// </summary>
public static class RegionCaptureService
{
    public static SKBitmap Crop(
        SKBitmap source,
        CaptureRegion region)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(region);

        SKRectI cropRectangle = ToPixelRectangle(
            new SKSizeI(source.Width, source.Height),
            region);

        var cropped = new SKBitmap();
        source.ExtractSubset(cropped, cropRectangle);

        return cropped;
    }

    public static SKRectI ToPixelRectangle(
        SKSizeI sourceSize,
        CaptureRegion region)
    {
        int x = (int)Math.Round(sourceSize.Width * region.X);
        int y = (int)Math.Round(sourceSize.Height * region.Y);
        int width = (int)Math.Round(sourceSize.Width * region.Width);
        int height = (int)Math.Round(sourceSize.Height * region.Height);

        x = Math.Clamp(x, 0, sourceSize.Width - 1);
        y = Math.Clamp(y, 0, sourceSize.Height - 1);

        width = Math.Clamp(width, 1, sourceSize.Width - x);
        height = Math.Clamp(height, 1, sourceSize.Height - y);

        return new SKRectI(x, y, x + width, y + height);
    }
}