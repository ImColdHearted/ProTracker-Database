using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Foot_Tracker.Models;

namespace Foot_Tracker.Controls;

/// <summary>
/// §410. The encounter-method glyph the Maps window draws everywhere a
/// method is named - on a found box on the picture, in the location
/// table, in the legend: a disc in the method's colour (§407's grass,
/// water and ice) with a white sign on it - three blades, a wave, a hook.
///
/// Drawn, not loaded: the sprite library has no method pictures (its
/// Items folder holds held items only), and a glyph drawn at any size
/// stays crisp at every zoom, which a 16-pixel bitmap would not. The one
/// routine, <see cref="Draw"/>, is what the control renders in a table
/// cell and what <see cref="MapBoxLayer"/> calls for a box, so the two
/// can never drift apart.
/// </summary>
public sealed class MethodIcon : Control
{
    public static readonly StyledProperty<EncounterMethod> MethodProperty =
        AvaloniaProperty.Register<MethodIcon, EncounterMethod>(nameof(Method));

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<MethodIcon, double>(nameof(IconSize), 16);

    private static readonly Dictionary<EncounterMethod, IBrush> Discs = new();

    private static readonly IBrush Sign = new SolidColorBrush(Colors.White);

    private static readonly IBrush Rim = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0));

    static MethodIcon()
    {
        AffectsRender<MethodIcon>(MethodProperty, IconSizeProperty);
        AffectsMeasure<MethodIcon>(IconSizeProperty);
    }

    public EncounterMethod Method
    {
        get => GetValue(MethodProperty);
        set => SetValue(MethodProperty, value);
    }

    /// <summary>The disc's diameter in screen pixels.</summary>
    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(IconSize, IconSize);

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double size = IconSize;

        if (size <= 0)
            return;

        Draw(context, Method, new Point(size / 2, size / 2), size);
    }

    /// <summary>The glyph, centred on a point, in a disc of the given
    /// diameter - the same at 12 pixels in a table and 18 on the picture.</summary>
    public static void Draw(DrawingContext context, EncounterMethod method, Point centre, double size)
    {
        double r = size / 2;

        if (!Discs.TryGetValue(method, out IBrush? disc))
        {
            disc = new SolidColorBrush(EncounterMethods.Colour(method));
            Discs[method] = disc;
        }

        context.DrawEllipse(disc, new Pen(Rim, 1), centre, r, r);

        var pen = new Pen(Sign, Math.Max(1.2, size / 9), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var geometry = new StreamGeometry();

        using (StreamGeometryContext g = geometry.Open())
        {
            double s = size;
            double cx = centre.X;
            double cy = centre.Y;

            switch (method)
            {
                case EncounterMethod.Grass:
                    // Three blades from one base.
                    g.BeginFigure(new Point(cx - 0.22 * s, cy + 0.28 * s), false);
                    g.LineTo(new Point(cx - 0.30 * s, cy - 0.12 * s));
                    g.EndFigure(false);
                    g.BeginFigure(new Point(cx, cy + 0.28 * s), false);
                    g.LineTo(new Point(cx, cy - 0.30 * s));
                    g.EndFigure(false);
                    g.BeginFigure(new Point(cx + 0.22 * s, cy + 0.28 * s), false);
                    g.LineTo(new Point(cx + 0.30 * s, cy - 0.12 * s));
                    g.EndFigure(false);
                    break;

                case EncounterMethod.Surf:
                    // Two crests, one above the other.
                    g.BeginFigure(new Point(cx - 0.32 * s, cy - 0.06 * s), false);
                    g.QuadraticBezierTo(new Point(cx - 0.16 * s, cy - 0.30 * s), new Point(cx, cy - 0.06 * s));
                    g.QuadraticBezierTo(new Point(cx + 0.16 * s, cy + 0.18 * s), new Point(cx + 0.32 * s, cy - 0.06 * s));
                    g.EndFigure(false);
                    g.BeginFigure(new Point(cx - 0.32 * s, cy + 0.20 * s), false);
                    g.QuadraticBezierTo(new Point(cx - 0.16 * s, cy - 0.04 * s), new Point(cx, cy + 0.20 * s));
                    g.QuadraticBezierTo(new Point(cx + 0.16 * s, cy + 0.44 * s), new Point(cx + 0.32 * s, cy + 0.20 * s));
                    g.EndFigure(false);
                    break;

                default:
                    // A hook: the line down, the bend back up.
                    g.BeginFigure(new Point(cx + 0.10 * s, cy - 0.34 * s), false);
                    g.LineTo(new Point(cx + 0.10 * s, cy + 0.06 * s));
                    g.ArcTo(new Point(cx - 0.20 * s, cy + 0.06 * s), new Size(0.15 * s, 0.15 * s), 0, false, SweepDirection.Clockwise);
                    g.LineTo(new Point(cx - 0.20 * s, cy - 0.06 * s));
                    g.EndFigure(false);
                    break;
            }
        }

        context.DrawGeometry(null, pen, geometry);
    }
}
