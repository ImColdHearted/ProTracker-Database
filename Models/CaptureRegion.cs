namespace Foot_Tracker.Models;

public sealed class CaptureRegion
{
    public string Name { get; set; } = string.Empty;

    // Values from 0.0 to 1.0, relative to the captured game window.
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}