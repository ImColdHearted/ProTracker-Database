using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Foot_Tracker.Controls;

/// <summary>
/// §384. A picture frame drawn around a panel: a PNG of a decorative border
/// (transparent in the middle) drawn in nine slices, so the corners keep
/// their shape and only the edges stretch - the way CSS border-image and
/// every game UI toolkit draw a frame.
///
/// <see cref="Inset"/> is the corner size in the picture's own pixels, and
/// the same number of pixels on screen: the four corners are copied at
/// their natural size, the four edges are stretched along their length to
/// fill the gap, and the centre is left undrawn so the panel shows through.
/// On a panel too small for two corners the inset shrinks to fit, so a
/// frame never overlaps itself.
///
/// Placed OVER a panel (last in a Panel, IsHitTestVisible false), covering
/// the same bounds. It draws nothing with no Source, so a theme without a
/// frame costs the window nothing.
/// </summary>
public sealed class NineSliceFrame : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<NineSliceFrame, IImage?>(nameof(Source));

    public static readonly StyledProperty<double> InsetProperty =
        AvaloniaProperty.Register<NineSliceFrame, double>(nameof(Inset), 24);

    static NineSliceFrame()
    {
        AffectsRender<NineSliceFrame>(SourceProperty, InsetProperty);
    }

    /// <summary>The frame picture; null draws nothing.</summary>
    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>The corner size, in the picture's pixels and on screen.</summary>
    public double Inset
    {
        get => GetValue(InsetProperty);
        set => SetValue(InsetProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        IImage? source = Source;

        if (source is null)
            return;

        Size picture = source.Size;
        double width = Bounds.Width;
        double height = Bounds.Height;

        if (picture.Width <= 0 || picture.Height <= 0 || width <= 0 || height <= 0)
            return;

        // The corner in the picture: never more than half of it, or the
        // slices would cross. On screen: never more than half the panel.
        double sourceInset = Math.Max(0, Math.Min(Inset, Math.Min(picture.Width, picture.Height) / 2));
        double screenInset = Math.Min(sourceInset, Math.Min(width, height) / 2);

        if (sourceInset <= 0 || screenInset <= 0)
            return;

        double[] sx = { 0, sourceInset, picture.Width - sourceInset, picture.Width };
        double[] sy = { 0, sourceInset, picture.Height - sourceInset, picture.Height };
        double[] dx = { 0, screenInset, width - screenInset, width };
        double[] dy = { 0, screenInset, height - screenInset, height };

        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                // The centre stays open: it is the panel, not the frame.
                if (row == 1 && column == 1)
                    continue;

                var from = new Rect(sx[column], sy[row], sx[column + 1] - sx[column], sy[row + 1] - sy[row]);
                var to = new Rect(dx[column], dy[row], dx[column + 1] - dx[column], dy[row + 1] - dy[row]);

                if (from.Width <= 0 || from.Height <= 0 || to.Width <= 0 || to.Height <= 0)
                    continue;

                context.DrawImage(source, from, to);
            }
        }
    }
}
