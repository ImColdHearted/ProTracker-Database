using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Foot_Tracker.Models;

namespace Foot_Tracker.Controls;

/// <summary>
/// Section 217. The battle scene's effect layer: one transparent control
/// sitting over the two Pokemon, drawing whatever the current request says.
///
/// It draws with Avalonia's own DrawingContext rather than leasing a Skia
/// surface. The shapes here are circles, strokes and polygons, all of which
/// DrawingContext does natively, and staying on the managed side means no
/// lease API to break the next time Avalonia moves. Every geometry is built
/// through StreamGeometry.Parse, which is the one geometry API this project
/// already uses (MainWindowViewModel's glyphs), with invariant formatting so
/// a comma decimal separator cannot silently produce an unparseable path.
///
/// The layer owns the clock. The view model hands over a request and awaits
/// its Completed task, so a turn cannot outrun its own pictures, and the
/// layer marks it finished exactly once whatever happens.
///
/// Everything is drawn in the FIELD's coordinates - the same 937x755 space
/// the sprites are positioned in - so the enclosing Viewbox scales the
/// effects and the Pokemon together.
/// </summary>
public sealed class BattleEffectLayer : Control
{
    // ---- properties ----

    public static readonly StyledProperty<BattleEffectRequest?> RequestProperty =
        AvaloniaProperty.Register<BattleEffectLayer, BattleEffectRequest?>(nameof(Request));

    /// <summary>The animation to play. Setting it starts one; setting it to
    /// null stops and clears.</summary>
    public BattleEffectRequest? Request
    {
        get => GetValue(RequestProperty);
        set => SetValue(RequestProperty, value);
    }

    public static readonly StyledProperty<ITransform?> PlayerTransformProperty =
        AvaloniaProperty.Register<BattleEffectLayer, ITransform?>(nameof(PlayerTransform));

    /// <summary>The nudge the player's sprite should carry this frame. The
    /// window binds the sprite's RenderTransform to it, which is how a hit
    /// shakes the thing that was hit without the layer owning the sprite.</summary>
    public ITransform? PlayerTransform
    {
        get => GetValue(PlayerTransformProperty);
        set => SetValue(PlayerTransformProperty, value);
    }

    public static readonly StyledProperty<ITransform?> OpponentTransformProperty =
        AvaloniaProperty.Register<BattleEffectLayer, ITransform?>(nameof(OpponentTransform));

    public ITransform? OpponentTransform
    {
        get => GetValue(OpponentTransformProperty);
        set => SetValue(OpponentTransformProperty, value);
    }

    // ---- state ----

    private const int BoltSegments = 7;

    private readonly DispatcherTimer timer;
    private BattleEffectRequest? playing;
    private DateTime startedAt;
    private double lengthSeconds = 1;

    /// <summary>Scatter offsets, drawn once when a playback starts. Drawing
    /// them per frame makes the particles boil instead of travel.</summary>
    private readonly List<Point[]> scatter = new();

    /// <summary>Lateral jitter for Bolt, same reasoning.</summary>
    private readonly List<double[][]> boltJitter = new();

    private static readonly Dictionary<string, Bitmap?> OverrideCache =
        new(StringComparer.OrdinalIgnoreCase);

    public BattleEffectLayer()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;

        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += OnTick;
    }

    private void OnTick(object? sender, EventArgs e) => Step();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Read the property back rather than unpacking the change args: one
        // fewer API whose shape has moved between Avalonia versions.
        if (change.Property == RequestProperty)
            Begin(Request);
    }

    // ---- playback ----

    private void Begin(BattleEffectRequest? request)
    {
        // Whatever was on screen is abandoned, but never left unfinished - a
        // view model awaiting the old request would wait for ever.
        playing?.Finish();

        timer.Stop();
        scatter.Clear();
        boltJitter.Clear();

        playing = request;
        PlayerTransform = null;
        OpponentTransform = null;

        if (request == null)
        {
            InvalidateVisual();
            return;
        }

        lengthSeconds = Math.Max(
            0.05, request.Spec.Seconds * Math.Max(0.05, request.SpeedFactor));

        var rng = new Random(request.Seed);

        foreach (EffectCue cue in request.Spec.Cues)
        {
            int count = Math.Max(1, cue.Count);
            var offsets = new Point[count];
            var jitter = new double[count][];

            for (int i = 0; i < count; i++)
            {
                double angle = rng.NextDouble() * Math.PI * 2;
                double radius = cue.Scatter * Math.Sqrt(rng.NextDouble());

                offsets[i] = new Point(Math.Cos(angle) * radius, Math.Sin(angle) * radius);

                var segments = new double[BoltSegments + 1];

                for (int s = 1; s < BoltSegments; s++)
                    segments[s] = (rng.NextDouble() - 0.5) * 2;

                jitter[i] = segments;
            }

            scatter.Add(offsets);
            boltJitter.Add(jitter);
        }

        startedAt = DateTime.UtcNow;
        timer.Start();
        InvalidateVisual();
    }

    private void Step()
    {
        if (playing == null)
        {
            timer.Stop();
            return;
        }

        double elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;

        if (elapsed >= lengthSeconds)
        {
            BattleEffectRequest finished = playing;

            playing = null;
            timer.Stop();
            PlayerTransform = null;
            OpponentTransform = null;

            InvalidateVisual();
            finished.Finish();
            return;
        }

        UpdateReaction(playing, elapsed / lengthSeconds);
        InvalidateVisual();
    }

    /// <summary>Section 217. The struck sprite's own movement. Everything
    /// here is expressible as a transform, which is why Flash is a scale
    /// pulse rather than a brightness change - one mechanism, bound once in
    /// the window, instead of a second animated property per sprite.</summary>
    private void UpdateReaction(BattleEffectRequest request, double t)
    {
        MoveAnimationSpec spec = request.Spec;

        if (spec.Reaction == TargetReaction.None)
        {
            PlayerTransform = null;
            OpponentTransform = null;
            return;
        }

        bool onAttacker = spec.ReactionOnAttacker;

        double from = onAttacker ? 0.05 : 0.35;
        double to = onAttacker ? 0.75 : 0.85;

        double p = (t - from) / (to - from);

        ITransform? transform = p is <= 0 or >= 1 ? null : Reaction(spec, request, p);

        bool onPlayer = onAttacker == request.AttackerIsPlayer;

        PlayerTransform = onPlayer ? transform : null;
        OpponentTransform = onPlayer ? null : transform;
    }

    private static ITransform Reaction(
        MoveAnimationSpec spec, BattleEffectRequest request, double p)
    {
        double bump = Math.Sin(p * Math.PI);

        switch (spec.Reaction)
        {
            case TargetReaction.Shake:
                return new TranslateTransform(Math.Sin(p * Math.PI * 8) * 7 * (1 - p), 0);

            case TargetReaction.Recoil:
            {
                double dx = request.Target.X - request.Attacker.X;
                double dy = request.Target.Y - request.Attacker.Y;
                double len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));

                return new TranslateTransform(dx / len * 18 * bump, dy / len * 18 * bump);
            }

            case TargetReaction.Flash:
                return new ScaleTransform(1 + 0.09 * bump, 1 + 0.09 * bump);

            case TargetReaction.Sink:
                return new TranslateTransform(0, 12 * bump);

            case TargetReaction.Rise:
                return new TranslateTransform(0, -12 * bump);

            default:
                return new TranslateTransform(0, 0);
        }
    }

    // ---- drawing ----

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        BattleEffectRequest? request = playing;

        if (request == null)
            return;

        double t = Math.Clamp(
            (DateTime.UtcNow - startedAt).TotalSeconds / lengthSeconds, 0, 1);

        IReadOnlyList<EffectCue> cues = request.Spec.Cues;

        for (int c = 0; c < cues.Count; c++)
        {
            EffectCue cue = cues[c];

            int count = Math.Max(1, cue.Count);
            double window = Math.Max(0.0001, cue.End - cue.Start);
            double delay = cue.Stagger * window / count;

            for (int i = 0; i < count; i++)
            {
                double start = cue.Start + delay * i;
                double end = cue.End + delay * i;

                if (t < start || t > end)
                    continue;

                double raw = (t - start) / Math.Max(0.0001, end - start);
                double p = Ease(raw, cue.Ease);

                Point offset = scatter[c][i];
                Point a = Anchor(request, cue.From) + offset;
                Point b = Anchor(request, cue.To) + offset;

                var at = new Point(a.X + (b.X - a.X) * p, a.Y + (b.Y - a.Y) * p);

                double size = cue.SizeFrom + (cue.SizeTo - cue.SizeFrom) * raw;
                double opacity = Math.Clamp(
                    cue.OpacityFrom + (cue.OpacityTo - cue.OpacityFrom) * raw, 0, 1);

                if (opacity <= 0.004 || size <= 0.5)
                    continue;

                Color colour = cue.Secondary
                    ? request.Palette.Secondary
                    : request.Palette.Primary;

                Bitmap? sprite = ResolveOverride(request.Spec.Archetype, cue.Shape);

                if (sprite != null)
                {
                    context.DrawRectangle(
                        new ImageBrush(sprite) { Stretch = Stretch.Uniform, Opacity = opacity },
                        null,
                        new Rect(at.X - size / 2, at.Y - size / 2, size, size));

                    continue;
                }

                Draw(context, cue, colour, opacity, at, a, b, size, raw, boltJitter[c][i]);
            }
        }
    }

    /// <summary>Alpha is multiplied into the colour rather than pushed onto
    /// the context. One fewer DrawingContext call to depend on, and the soft
    /// shapes need two different alphas at once anyway.</summary>
    private static Color Fade(Color colour, double alpha) =>
        Color.FromArgb(
            (byte)Math.Clamp(alpha * 255, 0, 255), colour.R, colour.G, colour.B);

    private static void Draw(
        DrawingContext context, EffectCue cue, Color colour, double opacity,
        Point at, Point a, Point b, double size, double raw, double[] jitter)
    {
        var brush = new SolidColorBrush(Fade(colour, opacity));

        switch (cue.Shape)
        {
            case EffectShape.Disc:
                Soft(context, colour, opacity, at, size);
                break;

            case EffectShape.Puff:
                Soft(context, colour, opacity, at, size * 1.5, core: 0.30);
                break;

            case EffectShape.Ring:
                context.DrawEllipse(
                    null, new Pen(brush, Math.Max(2, size * 0.07)), at, size / 2, size / 2);
                break;

            case EffectShape.Shard:
                context.DrawGeometry(brush, null, Shard(at, size, cue.Spin * raw));
                break;

            case EffectShape.Star:
                context.DrawGeometry(brush, null, Star(at, size));
                break;

            case EffectShape.Bolt:
                context.DrawGeometry(
                    null, new Pen(brush, Math.Max(2, size * 0.16)), Bolt(a, b, size, jitter));
                break;

            case EffectShape.Beam:
                context.DrawGeometry(brush, null, Beam(a, b, size, raw));
                break;

            case EffectShape.Slash:
                context.DrawGeometry(
                    null,
                    new Pen(brush, Math.Max(2, size * 0.12)),
                    Bow(a, b, cue.Arc));
                break;

            case EffectShape.Wave:
                context.DrawGeometry(
                    null,
                    new Pen(brush, Math.Max(2, size * 0.05)),
                    Bow(new Point(at.X - size / 2, at.Y),
                        new Point(at.X + size / 2, at.Y),
                        cue.Arc == 0 ? 0.22 : cue.Arc));
                break;
        }
    }

    /// <summary>A filled circle with a wider, fainter halo. Two ellipses
    /// read as a glow at a fraction of the cost of a real blur, and no
    /// gradient brush is allocated per particle per frame.</summary>
    private static void Soft(
        DrawingContext context, Color colour, double opacity,
        Point at, double size, double core = 0.55)
    {
        context.DrawEllipse(
            new SolidColorBrush(Fade(colour, opacity * 0.32)), null,
            at, size * 0.72, size * 0.72);

        context.DrawEllipse(
            new SolidColorBrush(Fade(colour, opacity * (core + 0.30))), null,
            at, size * 0.40, size * 0.40);
    }

    // ---- geometry ----
    //
    // Built as SVG path strings and handed to StreamGeometry.Parse. That is
    // the geometry entry point this project already uses, and it keeps the
    // shapes readable. N() forces invariant formatting: on a locale that
    // writes 1,5 for one and a half, an interpolated double would produce a
    // path the parser cannot read.

    private static string N(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static Geometry Shard(Point at, double size, double turns)
    {
        double angle = turns * Math.PI * 2;
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);

        double hw = size / 2;
        double hh = size * 0.22;

        string Corner(double x, double y) =>
            N(at.X + x * cos - y * sin) + "," + N(at.Y + x * sin + y * cos);

        return StreamGeometry.Parse(
            "M " + Corner(-hw, -hh) +
            " L " + Corner(hw, -hh) +
            " L " + Corner(hw, hh) +
            " L " + Corner(-hw, hh) + " Z");
    }

    /// <summary>Two crossed slivers - a four point sparkle. Cheaper and
    /// crisper than a polygon star, and it reads at any size.</summary>
    private static Geometry Star(Point at, double size)
    {
        double arm = size / 2;
        double waist = Math.Max(1, size * 0.11);

        string P(double x, double y) => N(at.X + x) + "," + N(at.Y + y);

        return StreamGeometry.Parse(
            "M " + P(0, -arm) + " L " + P(waist, 0) + " L " + P(0, arm) + " L " + P(-waist, 0) + " Z " +
            "M " + P(-arm, 0) + " L " + P(0, -waist) + " L " + P(arm, 0) + " L " + P(0, waist) + " Z");
    }

    private static Geometry Bolt(Point a, Point b, double size, double[] jitter)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));

        double nx = -dy / len;
        double ny = dx / len;

        var path = new System.Text.StringBuilder("M " + N(a.X) + "," + N(a.Y));

        for (int s = 1; s <= BoltSegments; s++)
        {
            double f = (double)s / BoltSegments;
            double spread = jitter[Math.Min(s, jitter.Length - 1)] * size * 0.9;

            path.Append(" L ")
                .Append(N(a.X + dx * f + nx * spread))
                .Append(',')
                .Append(N(a.Y + dy * f + ny * spread));
        }

        return StreamGeometry.Parse(path.ToString());
    }

    private static Geometry Beam(Point a, Point b, double size, double raw)
    {
        // The beam extends over the first stretch of its window and then
        // holds, so a long cue reads as a sustained beam rather than a very
        // slow one.
        double reach = Math.Clamp(raw / 0.45, 0, 1);

        var tip = new Point(a.X + (b.X - a.X) * reach, a.Y + (b.Y - a.Y) * reach);

        double dx = tip.X - a.X;
        double dy = tip.Y - a.Y;
        double len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));

        double nx = -dy / len * size * 0.5;
        double ny = dx / len * size * 0.5;

        return StreamGeometry.Parse(
            "M " + N(a.X + nx * 0.45) + "," + N(a.Y + ny * 0.45) +
            " L " + N(tip.X + nx) + "," + N(tip.Y + ny) +
            " L " + N(tip.X - nx) + "," + N(tip.Y - ny) +
            " L " + N(a.X - nx * 0.45) + "," + N(a.Y - ny * 0.45) + " Z");
    }

    /// <summary>A bowed stroke from a to b. The bow is a fraction of the
    /// span pushed perpendicular, which is what turns a straight line into a
    /// slash or a gust.</summary>
    private static Geometry Bow(Point a, Point b, double bow)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));

        double cx = (a.X + b.X) / 2 - dy * bow;
        double cy = (a.Y + b.Y) / 2 + dx * bow;

        return StreamGeometry.Parse(
            "M " + N(a.X) + "," + N(a.Y) +
            " Q " + N(cx) + "," + N(cy) + " " + N(b.X) + "," + N(b.Y));
    }

    // ---- anchors ----

    private static Point Anchor(BattleEffectRequest request, EffectAnchor anchor)
    {
        Rect attacker = request.AttackerBox;
        Rect target = request.TargetBox;

        return anchor switch
        {
            EffectAnchor.Attacker => request.Attacker,
            EffectAnchor.Target => request.Target,

            EffectAnchor.AttackerAbove => new Point(
                request.Attacker.X,
                attacker.Height > 0 ? attacker.Top - 40 : request.Attacker.Y - 110),

            EffectAnchor.TargetAbove => new Point(
                request.Target.X,
                target.Height > 0 ? target.Top - 40 : request.Target.Y - 110),

            EffectAnchor.AttackerGround => new Point(
                request.Attacker.X,
                attacker.Height > 0 ? attacker.Bottom : request.Attacker.Y + 70),

            EffectAnchor.TargetGround => new Point(
                request.Target.X,
                target.Height > 0 ? target.Bottom : request.Target.Y + 70),

            EffectAnchor.Field => new Point(
                (request.Attacker.X + request.Target.X) / 2,
                (request.Attacker.Y + request.Target.Y) / 2),

            _ => request.Target,
        };
    }

    private static double Ease(double p, EffectEase ease) => ease switch
    {
        EffectEase.In => p * p,
        EffectEase.Out => 1 - (1 - p) * (1 - p),
        EffectEase.InOut => p < 0.5 ? 2 * p * p : 1 - 2 * (1 - p) * (1 - p),
        EffectEase.Overshoot => 1 + 1.7 * Math.Pow(p - 1, 3) + 0.7 * Math.Pow(p - 1, 2),
        _ => p,
    };

    // ---- the PNG override hook ----

    /// <summary>
    /// Section 217. Drop a PNG beside the executable and it replaces the
    /// drawn shape, no rebuild:
    ///
    ///   Assets/BattleEffects/archetypes/&lt;Archetype&gt;.png
    ///   Assets/BattleEffects/shapes/&lt;Shape&gt;.png
    ///
    /// The archetype file wins. A missing folder is the normal case and
    /// costs one dictionary hit after the first look, because the misses are
    /// cached too.
    /// </summary>
    private static Bitmap? ResolveOverride(string archetype, EffectShape shape)
    {
        string key = archetype + "/" + shape;

        lock (OverrideCache)
        {
            if (OverrideCache.TryGetValue(key, out Bitmap? cached))
                return cached;

            Bitmap? found = Load("archetypes", archetype) ?? Load("shapes", shape.ToString());

            OverrideCache[key] = found;
            return found;
        }
    }

    private static Bitmap? Load(string folder, string name)
    {
        try
        {
            string path = Path.Combine(
                AppContext.BaseDirectory, "Assets", "BattleEffects", folder, name + ".png");

            return File.Exists(path) ? new Bitmap(path) : null;
        }
        catch (Exception)
        {
            // An unreadable override is not worth failing a battle over.
            return null;
        }
    }
}
