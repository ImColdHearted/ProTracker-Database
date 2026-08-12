using Foot_Tracker.Models;
using System.Drawing;

namespace Foot_Tracker.Services;

public static class RegionCaptureService
{
    public static Bitmap Crop(
        Bitmap source,
        CaptureRegion region)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(region);

        Rectangle cropRectangle = ToPixelRectangle(
            source.Size,
            region);

        Bitmap croppedImage = new(
            cropRectangle.Width,
            cropRectangle.Height);

        using Graphics graphics = Graphics.FromImage(croppedImage);

        graphics.DrawImage(
            source,
            new Rectangle(
                0,
                0,
                croppedImage.Width,
                croppedImage.Height),
            cropRectangle,
            GraphicsUnit.Pixel);

        return croppedImage;
    }

    public static Rectangle ToPixelRectangle(
        Size sourceSize,
        CaptureRegion region)
    {
        int x = (int)Math.Round(sourceSize.Width * region.X);
        int y = (int)Math.Round(sourceSize.Height * region.Y);
        int width =
            (int)Math.Round(sourceSize.Width * region.Width);
        int height =
            (int)Math.Round(sourceSize.Height * region.Height);

        x = Math.Clamp(x, 0, sourceSize.Width - 1);
        y = Math.Clamp(y, 0, sourceSize.Height - 1);

        width = Math.Clamp(
            width,
            1,
            sourceSize.Width - x);

        height = Math.Clamp(
            height,
            1,
            sourceSize.Height - y);

        return new Rectangle(x, y, width, height);
    }
}