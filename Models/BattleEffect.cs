using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;

namespace Foot_Tracker.Models;

/// <summary>
/// Section 217. The vocabulary the battle animations are written in.
///
/// This is deliberately a small set of primitives rather than one routine
/// per move. Nine hundred moves do not need nine hundred animations; they
/// need about twenty motions, tinted by type and aimed at the right end of
/// the field. An archetype is a list of cues, a cue is one shape travelling
/// between two anchors over a slice of the animation's life, and everything
/// else - colour, distance, how many particles - is filled in when the move
/// is classified. See MoveAnimationCatalog.
///
/// None of this is ported from anywhere. Pokemon Showdown's move animations
/// live in its CLIENT repository, which is AGPLv3 and would have taken the
/// whole tracker with it; its server repository, which is MIT, has no
/// animations in it at all. So the motions below are our own.
/// </summary>
public enum EffectShape
{
    /// <summary>A soft filled circle. The workhorse - energy, impacts, motes.</summary>
    Disc,

    /// <summary>An outlined circle that reads well expanding.</summary>
    Ring,

    /// <summary>A small rotated rectangle. Debris, ice, rock, metal.</summary>
    Shard,

    /// <summary>A jagged polyline. Electricity.</summary>
    Bolt,

    /// <summary>A quad tapering from one anchor to the other.</summary>
    Beam,

    /// <summary>A curved stroke, drawn across the target.</summary>
    Slash,

    /// <summary>A soft blob with a wide feathered edge. Smoke, powder, dust.</summary>
    Puff,

    /// <summary>A four point sparkle. Impacts and buffs.</summary>
    Star,

    /// <summary>A wide flattened arc that sweeps the field.</summary>
    Wave,
}

/// <summary>Where on the field a cue starts or ends. The layer turns these
/// into real points using the sprites' measured boxes, so a Wailord and a
/// Joltik are hit in the middle of themselves rather than at a fixed
/// height.</summary>
public enum EffectAnchor
{
    Attacker,
    Target,
    AttackerAbove,
    TargetAbove,
    AttackerGround,
    TargetGround,
    Field,
}

/// <summary>How a cue's progress maps onto its motion.</summary>
public enum EffectEase
{
    Linear,
    In,
    Out,
    InOut,
    Overshoot,
}

/// <summary>What the struck sprite does while the cue plays.</summary>
public enum TargetReaction
{
    None,
    Shake,
    Flash,
    Recoil,
    Sink,
    Rise,
}

/// <summary>Section 217. One shape, one journey, one slice of time.
/// Start and End are fractions of the whole animation, so an archetype's
/// cues overlap and stagger without any of them knowing the real duration -
/// which is what lets the speed setting scale the lot by one multiplier.</summary>
public sealed record EffectCue
{
    public EffectShape Shape { get; init; } = EffectShape.Disc;

    public EffectAnchor From { get; init; } = EffectAnchor.Attacker;
    public EffectAnchor To { get; init; } = EffectAnchor.Target;

    /// <summary>0 to 1 of the animation's length.</summary>
    public double Start { get; init; }

    public double End { get; init; } = 1.0;

    public double SizeFrom { get; init; } = 18;
    public double SizeTo { get; init; } = 18;

    public double OpacityFrom { get; init; } = 1.0;
    public double OpacityTo { get; init; }

    /// <summary>Pixels of stable random offset, in field coordinates. Drawn
    /// once per playback rather than per frame, or the particles boil.</summary>
    public double Scatter { get; init; }

    public int Count { get; init; } = 1;

    /// <summary>Fraction of the cue's window each successive particle waits
    /// before it starts. A stagger of 0 fires them together.</summary>
    public double Stagger { get; init; }

    public EffectEase Ease { get; init; } = EffectEase.Linear;

    /// <summary>Whole turns of rotation over the cue's life. Shards and
    /// stars use it; discs do not care.</summary>
    public double Spin { get; init; }

    /// <summary>Paint with the palette's second colour instead of its
    /// first.</summary>
    public bool Secondary { get; init; }

    /// <summary>Draw the shape as an arc across the path rather than along
    /// it. Slashes and waves want this; projectiles do not.</summary>
    public double Arc { get; init; }
}

/// <summary>Section 217. A resolved animation: which archetype was chosen,
/// what it is made of, and how long it runs before the speed multiplier.</summary>
public sealed record MoveAnimationSpec
{
    public required string Archetype { get; init; }
    public required IReadOnlyList<EffectCue> Cues { get; init; }

    public TargetReaction Reaction { get; init; } = TargetReaction.None;

    /// <summary>The reaction plays on the attacker instead of the target -
    /// self buffs, heals, recoil.</summary>
    public bool ReactionOnAttacker { get; init; }

    public double Seconds { get; init; } = 0.85;
}

/// <summary>Section 217. The two colours a move is painted in, taken from
/// its type. Status moves that belong to no element borrow a neutral pair.</summary>
public readonly record struct EffectPalette(Color Primary, Color Secondary);

/// <summary>Section 217. One animation the view model wants played. The
/// view model awaits Completed; the layer sets it when the last cue has
/// faded, or immediately if animations are switched off. It is a
/// TaskCompletionSource rather than an event so a turn cannot run ahead of
/// its own pictures.</summary>
public sealed class BattleEffectRequest
{
    public required MoveAnimationSpec Spec { get; init; }
    public required EffectPalette Palette { get; init; }

    /// <summary>Field coordinates - the 937x755 space the scene is drawn
    /// in, not screen pixels. The Viewbox scales both alike.</summary>
    public required Point Attacker { get; init; }

    public required Point Target { get; init; }

    /// <summary>The attacker's and target's measured boxes, so a cue aimed
    /// at "above the target" clears a tall sprite's head.</summary>
    public Rect AttackerBox { get; init; }

    public Rect TargetBox { get; init; }

    /// <summary>Which side is acting. The layer needs it to nudge the
    /// right sprite: a reaction is "on the attacker" or not, and only this
    /// says which sprite that is.</summary>
    public required bool AttackerIsPlayer { get; init; }

    /// <summary>Multiplies the spec's own length. Larger is slower.</summary>
    public double SpeedFactor { get; init; } = 1.0;

    /// <summary>Seeds the scatter, so the same move looks the same twice
    /// and a screenshot can be reasoned about.</summary>
    public int Seed { get; init; }

    public TaskCompletionSource Completed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Marks the request finished exactly once. The layer calls it
    /// on the happy path, and the view model calls it on a timeout, so a
    /// turn can never be wedged by a picture that failed to draw.</summary>
    public void Finish() => Completed.TrySetResult();
}
