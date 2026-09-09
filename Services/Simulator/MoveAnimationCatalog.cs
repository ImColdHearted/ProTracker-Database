using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Foot_Tracker.Models;
using PokemonSim.Models;

namespace Foot_Tracker.Services.Simulator;

/// <summary>
/// Section 217. Which animation a move gets, and what it is made of.
///
/// The design decision worth recording: there is no per-move animation
/// table. Roughly nine hundred moves resolve onto twenty four motions, and
/// the motion is chosen from what the engine already knows about the move -
/// its flags, its category, its effects, its multi-hit count - with the
/// move's NAME as a tie breaker, because move names in this series are
/// unusually descriptive. Anything ending in Punch punches. Anything with
/// Fang in it bites.
///
/// Where the choice is wrong it is wrong harmlessly: a move gets a
/// plausible motion in the right colour instead of the perfect one. Nothing
/// here can affect a battle. The classifier is pure and total - every move
/// leaves with a spec, and Generic is the floor.
///
/// The flags this reads (IsPunch, IsPulse, IsSpread, IsContact) come from
/// PokemonSim's own MoveFlags, which are hand maintained name lists. When
/// the real flag data lands from the MIT server repository, Classify gets
/// sharper without changing shape: bullet becomes Projectile, sound becomes
/// SoundRings, slicing becomes Slash, and the name heuristics below become
/// the fallback rather than the main road.
/// </summary>
public static class MoveAnimationCatalog
{
    // ---- palette ----

    /// <summary>Two colours per type. Chosen here rather than taken from
    /// anywhere: a saturated body colour that reads against both the light
    /// and dark halves of the field art, and a pale partner for rings,
    /// sparks and the second half of a burst.</summary>
    static readonly Dictionary<PokemonType, EffectPalette> Palettes = new()
    {
        [PokemonType.Normal]   = Pair("#C8C2AE", "#8E887A"),
        [PokemonType.Fire]     = Pair("#F0762B", "#FFD24A"),
        [PokemonType.Water]    = Pair("#3C86E0", "#9ED2FF"),
        [PokemonType.Electric] = Pair("#F2C438", "#FFF6B0"),
        [PokemonType.Grass]    = Pair("#56B94A", "#B7E86A"),
        [PokemonType.Ice]      = Pair("#6FD4DC", "#DFF7FA"),
        [PokemonType.Fighting] = Pair("#C0392B", "#F07A5A"),
        [PokemonType.Poison]   = Pair("#9B4FA8", "#D98FE0"),
        [PokemonType.Ground]   = Pair("#C79A4E", "#E8D3A0"),
        [PokemonType.Flying]   = Pair("#8FA9E8", "#DCE6FF"),
        [PokemonType.Psychic]  = Pair("#E85A8C", "#FFB3CE"),
        [PokemonType.Bug]      = Pair("#93B23A", "#D2E27E"),
        [PokemonType.Rock]     = Pair("#A99364", "#D8C79A"),
        [PokemonType.Ghost]    = Pair("#6A5FA8", "#B3A8E8"),
        [PokemonType.Dragon]   = Pair("#5B4BC4", "#9E8CFF"),
        [PokemonType.Dark]     = Pair("#55494A", "#9C8A8C"),
        [PokemonType.Steel]    = Pair("#9FAFBE", "#E0EAF2"),
        [PokemonType.Fairy]    = Pair("#EE8FC0", "#FFD6EC"),
    };

    /// <summary>Struggle has no type, and neither does a status move whose
    /// element is beside the point.</summary>
    static readonly EffectPalette Neutral = Pair("#BFC7D2", "#EAF0F7");

    static EffectPalette Pair(string primary, string secondary) =>
        new(Color.Parse(primary), Color.Parse(secondary));

    public static EffectPalette PaletteFor(MoveState move)
    {
        if (move.Typeless)
            return Neutral;

        return Palettes.TryGetValue(move.Type, out EffectPalette palette)
            ? palette
            : Neutral;
    }

    // ---- effect name groups ----
    //
    // These are the strings MoveEffectRegistry actually registers. Read out
    // of the registry rather than remembered, because a name that is nearly
    // right classifies nothing and says nothing about it.

    /// <summary>Most moves carry no effects list at all, so they share one
    /// empty rather than allocating. Typed as the interface deliberately:
    /// the null-coalescing operator has no common type for a List and an
    /// array, so "move.Effects ?? Array.Empty&lt;string&gt;()" does not
    /// compile however the result is being assigned.</summary>
    static readonly IReadOnlyList<string> NoEffects = Array.Empty<string>();

    static readonly HashSet<string> BarrierEffects = new(StringComparer.Ordinal)
    {
        "Reflect", "LightScreen", "AuroraVeil", "Safeguard", "Mist", "Protect",
        "KingsShield", "BanefulBunker", "QuickGuard", "WideGuard", "Endure",
        "Substitute", "MagnetRise", "Ingrain",
    };

    static readonly HashSet<string> HazardEffects = new(StringComparer.Ordinal)
    {
        "Spikes", "ToxicSpikes", "StealthRock", "StickyWeb",
    };

    static readonly HashSet<string> DrainEffects = new(StringComparer.Ordinal)
    {
        "Drain", "Drain75", "LeechSeed", "StrengthSap", "PainSplit",
    };

    static readonly HashSet<string> HealEffects = new(StringComparer.Ordinal)
    {
        "Heal", "HealQuarter", "Rest", "AquaRing", "HealingWish",
        "RefreshCure", "HealBell", "Stockpile",
    };

    static readonly HashSet<string> ChargeEffects = new(StringComparer.Ordinal)
    {
        "TwoTurn", "MeteorCharge",
    };

    static readonly HashSet<string> ConfusionEffects = new(StringComparer.Ordinal)
    {
        "Confuse10", "Confuse20", "Confuse30", "Confuse100",
        "InflictConfusion", "Yawn",
    };

    // ---- name tokens ----
    //
    // Word matches whole words, for tokens short enough to appear inside an
    // unrelated name. Sub matches anywhere, for tokens distinctive enough
    // that it cannot go wrong. Both are case insensitive.

    static bool Word(string name, params string[] tokens)
    {
        string[] words = name.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string word in words)
            foreach (string token in tokens)
                if (string.Equals(word, token, StringComparison.OrdinalIgnoreCase))
                    return true;

        return false;
    }

    static bool Sub(string name, params string[] tokens)
    {
        foreach (string token in tokens)
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    static bool IsSoundName(string n) =>
        Sub(n, "Screech", "Roar", "Growl", "Song", "Voice", "Howl", "Snarl",
               "Chatter", "Boomburst", "Uproar", "Buzz", "Supersonic",
               "Overdrive", "Clang", "Whistle", "Aria", "Bellow", "Echo")
        || Word(n, "Round", "Sing", "Snore");

    static bool IsPowderName(string n) => Sub(n, "Powder", "Spore", "Pollen");

    static bool IsWindName(string n) =>
        Sub(n, "Whirlwind", "Hurricane", "Twister", "Cyclone", "Tornado", "Tailwind")
        || Word(n, "Wind", "Gust");

    // ---- classification ----

    /// <summary>Section 217. The one entry point. Total by construction -
    /// every path ends at a named archetype, and Generic catches the rest.</summary>
    public static MoveAnimationSpec Classify(MoveState move)
    {
        string name = move.Name;
        IReadOnlyList<string> effects = move.Effects ?? NoEffects;

        // Global shapes first: these describe what the move DOES, and they
        // beat anything its name suggests. Order inside the group is by
        // specificity, not by preference.
        if (effects.Any(BarrierEffects.Contains))
            return Build("Barrier");

        if (move.SetWeather != WeatherType.None || move.SetTerrain != TerrainType.None)
            return Build("FieldWeather");

        if (effects.Any(HazardEffects.Contains))
            return Build("Hazard");

        if (effects.Any(DrainEffects.Contains))
            return Build("Drain");

        if (effects.Any(HealEffects.Contains))
            return Build("Heal");

        // Charge is deliberately NOT tested here. A two turn move announces
        // itself twice - once winding up, once firing - and one classifier
        // cannot tell those apart, so whichever motion it picks is wrong on
        // one of the two turns. Given the choice, the move's own character
        // wins: Solar Beam should look like a beam on both turns rather than
        // like a gathering glow on both. Charge is the fallback further down
        // for the two turn moves whose names describe no motion at all -
        // Fly, Dig, Dive, Bounce, Sky Attack, Phantom Force.
        return move.Category == MoveCategory.Status
            ? ClassifyStatus(move, name, effects)
            : ClassifyDamaging(move, name);
    }

    static MoveAnimationSpec ClassifyStatus(
        MoveState move, string name, IReadOnlyList<string> effects)
    {
        if (effects.Any(ChargeEffects.Contains))
            return Build("Charge");

        if (Sub(name, "Dance"))
            return Build("Dance");

        if (IsSoundName(name))
            return Build("SoundRings");

        if (IsPowderName(name))
            return Build("PowderCloud");

        if (IsWindName(name))
            return Build("Wind");

        if (move.InflictStatus != StatusCondition.None ||
            move.RandomStatus is { Count: > 0 } ||
            effects.Any(ConfusionEffects.Contains))
        {
            return Build("StatusInflict");
        }

        // Sign and target together: a status move that raises anything is a
        // buff on its user, one that lowers anything is aimed across the
        // field. Target is the data file's own string, defaulting to self.
        if (move.StatChanges is { Count: > 0 })
        {
            bool raises = move.StatChanges.Any(
                c => c.Stages > 0 &&
                     string.Equals(c.Target, "self", StringComparison.OrdinalIgnoreCase));

            if (raises)
                return Build("SelfBuff");

            if (move.StatChanges.Any(c => c.Stages < 0))
                return Build("Debuff");
        }

        return Build("SelfBuff");
    }

    static MoveAnimationSpec ClassifyDamaging(MoveState move, string name)
    {
        // The engine's own flags outrank the name: MoveFlags curates them,
        // and they catch Meteor Mash and Sky Uppercut, which do not say
        // punch anywhere.
        if (move.IsPunch)
            return Build("Punch");

        if (move.IsPulse)
            return Build("Pulse");

        // Most specific names first. Thunder Fang has to reach Bite before
        // Strike gets hold of the word Thunder.
        if (Sub(name, "Punch", "Uppercut") || Word(name, "Mash", "Pound"))
            return Build("Punch");

        if (Word(name, "Bite") || Sub(name, "Fang", "Crunch", "Chomp"))
            return Build("Bite");

        if (Sub(name, "Slash", "Cutter", "Blade", "Claw", "Scratch", "Guillotine")
            || Word(name, "Cut", "Sword", "Razor"))
        {
            return Build("Slash");
        }

        if (Sub(name, "Beam", "Flamethrower", "Laser") || Word(name, "Breath"))
            return Build("Beam");

        if (Sub(name, "Pulse", "Sphere"))
            return Build("Pulse");

        if (IsSoundName(name))
            return Build("SoundRings");

        if (IsPowderName(name))
            return Build("PowderCloud");

        if (IsWindName(name))
            return Build("Wind");

        if (Sub(name, "Dance"))
            return Build("Dance");

        if (Sub(name, "Thunder", "bolt", "Lightning", "Zap") || Word(name, "Spark", "Shock"))
            return Build("Strike");

        if (Word(name, "Ball", "Bomb", "Shot", "Cannon", "Dart", "Egg", "Blast", "Throw", "Barrage")
            || Sub(name, "Missile", "Bullet", "Needle", "Sting", "Pellet", "zooka"))
        {
            return Build("Projectile");
        }

        if (Word(name, "Slam", "Tackle", "Rush", "Press", "Crush", "Stomp", "Kick",
                       "Headbutt", "Horn", "Drill", "Ram", "Impact", "Bash",
                       "Strike", "Smash", "Hammer")
            || Sub(name, "Body", "Wing"))
        {
            return Build("Slam");
        }

        // Nothing in the name describes a motion. A two turn move can have
        // its winding up now, because nothing better is on offer.
        if (move.Effects != null && move.Effects.Any(ChargeEffects.Contains))
            return Build("Charge");

        // Fall back on shape.
        if (move.MaxHits > 1)
            return Build("MultiHit");

        if (move.IsSpread)
            return Build("Spread");

        if (move.IsContact)
            return Build("Slam");

        return move.Category == MoveCategory.Special
            ? Build("Projectile")
            : Build("Generic");
    }

    /// <summary>Every archetype name the classifier can return. The battery
    /// walks this against Archetypes so a typo in a Build call is a failing
    /// check rather than a move that silently animates as Generic.</summary>
    public static IReadOnlyCollection<string> ArchetypeNames => Archetypes.Keys;

    static MoveAnimationSpec Build(string archetype) =>
        Archetypes.TryGetValue(archetype, out MoveAnimationSpec? spec)
            ? spec
            : Archetypes["Generic"];

    // ---- the archetypes ----

    static EffectCue Cue(
        EffectShape shape, EffectAnchor from, EffectAnchor to,
        double start, double end,
        double sizeFrom, double sizeTo,
        double opacityFrom = 1.0, double opacityTo = 0.0,
        int count = 1, double scatter = 0, double stagger = 0,
        EffectEase ease = EffectEase.Linear, double spin = 0,
        bool secondary = false, double arc = 0) =>
        new()
        {
            Shape = shape, From = from, To = to,
            Start = start, End = end,
            SizeFrom = sizeFrom, SizeTo = sizeTo,
            OpacityFrom = opacityFrom, OpacityTo = opacityTo,
            Count = count, Scatter = scatter, Stagger = stagger,
            Ease = ease, Spin = spin, Secondary = secondary, Arc = arc,
        };

    const EffectAnchor A = EffectAnchor.Attacker;
    const EffectAnchor T = EffectAnchor.Target;
    const EffectAnchor AAbove = EffectAnchor.AttackerAbove;
    const EffectAnchor TAbove = EffectAnchor.TargetAbove;
    const EffectAnchor AGround = EffectAnchor.AttackerGround;
    const EffectAnchor TGround = EffectAnchor.TargetGround;
    const EffectAnchor F = EffectAnchor.Field;

    static MoveAnimationSpec Spec(
        string name, TargetReaction reaction, double seconds,
        bool onAttacker, params EffectCue[] cues) =>
        new()
        {
            Archetype = name,
            Cues = cues,
            Reaction = reaction,
            ReactionOnAttacker = onAttacker,
            Seconds = seconds,
        };

    static readonly Dictionary<string, MoveAnimationSpec> Archetypes = new(StringComparer.Ordinal)
    {
        ["Projectile"] = Spec("Projectile", TargetReaction.Shake, 0.85, false,
            Cue(EffectShape.Disc, A, A, 0.00, 0.28, 6, 30, 0.15, 1.0, ease: EffectEase.Out),
            Cue(EffectShape.Disc, A, T, 0.26, 0.66, 30, 26, 1.0, 1.0, ease: EffectEase.In),
            Cue(EffectShape.Ring, T, T, 0.62, 1.00, 20, 112, 0.95, 0.0, secondary: true),
            Cue(EffectShape.Star, T, T, 0.62, 0.94, 46, 14, 1.0, 0.0, count: 6, scatter: 44, stagger: 0.25)),

        ["Beam"] = Spec("Beam", TargetReaction.Flash, 0.95, false,
            Cue(EffectShape.Disc, A, A, 0.00, 0.22, 8, 34, 0.2, 0.9, ease: EffectEase.Out),
            Cue(EffectShape.Beam, A, T, 0.18, 0.86, 10, 36, 0.9, 0.0),
            Cue(EffectShape.Disc, T, T, 0.34, 1.00, 22, 94, 0.9, 0.0, secondary: true),
            Cue(EffectShape.Star, T, T, 0.40, 1.00, 40, 12, 0.95, 0.0, count: 5, scatter: 48, stagger: 0.2)),

        ["Strike"] = Spec("Strike", TargetReaction.Flash, 0.80, false,
            Cue(EffectShape.Bolt, TAbove, T, 0.00, 0.42, 30, 30, 1.0, 0.6, count: 3, scatter: 34, stagger: 0.28),
            Cue(EffectShape.Disc, T, T, 0.34, 0.90, 20, 98, 0.9, 0.0, secondary: true),
            Cue(EffectShape.Ring, T, T, 0.38, 1.00, 30, 126, 0.8, 0.0),
            Cue(EffectShape.Star, T, T, 0.36, 0.92, 44, 12, 0.9, 0.0, count: 5, scatter: 46, stagger: 0.2)),

        ["Slash"] = Spec("Slash", TargetReaction.Shake, 0.70, false,
            Cue(EffectShape.Slash, TAbove, TGround, 0.06, 0.44, 96, 96, 1.0, 0.15, arc: 0.55),
            Cue(EffectShape.Slash, TAbove, TGround, 0.26, 0.64, 96, 96, 1.0, 0.15, secondary: true, arc: -0.55),
            Cue(EffectShape.Star, T, T, 0.34, 0.88, 40, 12, 0.95, 0.0, count: 4, scatter: 38, stagger: 0.2)),

        ["Punch"] = Spec("Punch", TargetReaction.Recoil, 0.65, false,
            Cue(EffectShape.Disc, A, T, 0.00, 0.34, 34, 28, 0.5, 0.9, ease: EffectEase.In),
            Cue(EffectShape.Star, T, T, 0.30, 0.70, 88, 24, 1.0, 0.0),
            Cue(EffectShape.Ring, T, T, 0.30, 0.86, 18, 92, 0.9, 0.0, secondary: true),
            Cue(EffectShape.Shard, T, T, 0.34, 0.92, 16, 6, 0.9, 0.0, count: 5, scatter: 52, stagger: 0.2, spin: 0.6)),

        ["Bite"] = Spec("Bite", TargetReaction.Shake, 0.70, false,
            Cue(EffectShape.Slash, TAbove, T, 0.04, 0.42, 88, 88, 0.95, 0.1, arc: 0.85),
            Cue(EffectShape.Slash, TGround, T, 0.04, 0.42, 88, 88, 0.95, 0.1, secondary: true, arc: -0.85),
            Cue(EffectShape.Star, T, T, 0.36, 0.84, 44, 14, 0.95, 0.0, count: 3, scatter: 32, stagger: 0.2)),

        ["Pulse"] = Spec("Pulse", TargetReaction.Flash, 0.90, false,
            Cue(EffectShape.Ring, A, A, 0.00, 0.52, 14, 74, 1.0, 0.0, count: 3, stagger: 0.3),
            Cue(EffectShape.Disc, A, T, 0.24, 0.64, 38, 46, 0.9, 0.9, ease: EffectEase.InOut),
            Cue(EffectShape.Ring, T, T, 0.58, 1.00, 30, 128, 0.9, 0.0, secondary: true),
            Cue(EffectShape.Disc, T, T, 0.58, 0.92, 44, 74, 0.7, 0.0)),

        ["SoundRings"] = Spec("SoundRings", TargetReaction.Shake, 0.90, false,
            Cue(EffectShape.Ring, A, A, 0.00, 0.92, 30, 268, 0.85, 0.0, count: 4, stagger: 0.22),
            Cue(EffectShape.Ring, T, T, 0.50, 1.00, 20, 96, 0.6, 0.0, secondary: true)),

        ["PowderCloud"] = Spec("PowderCloud", TargetReaction.None, 1.00, false,
            Cue(EffectShape.Puff, A, T, 0.00, 0.70, 26, 46, 0.12, 0.8, count: 9, scatter: 56, stagger: 0.35),
            Cue(EffectShape.Puff, T, T, 0.55, 1.00, 44, 74, 0.8, 0.0, count: 7, scatter: 62, stagger: 0.25, secondary: true)),

        ["Wind"] = Spec("Wind", TargetReaction.Shake, 0.85, false,
            Cue(EffectShape.Wave, A, T, 0.00, 0.86, 120, 156, 0.15, 0.75, count: 5, scatter: 96, stagger: 0.18),
            Cue(EffectShape.Wave, T, T, 0.55, 1.00, 140, 190, 0.6, 0.0, count: 2, scatter: 60, stagger: 0.3, secondary: true)),

        ["Slam"] = Spec("Slam", TargetReaction.Recoil, 0.72, false,
            Cue(EffectShape.Disc, A, T, 0.00, 0.32, 46, 40, 0.35, 0.7, ease: EffectEase.In),
            Cue(EffectShape.Star, T, T, 0.30, 0.62, 104, 30, 1.0, 0.0),
            Cue(EffectShape.Ring, T, T, 0.30, 0.88, 24, 104, 0.85, 0.0),
            Cue(EffectShape.Shard, T, T, 0.32, 0.94, 18, 6, 0.9, 0.0, count: 7, scatter: 66, stagger: 0.2, spin: 0.7, secondary: true)),

        ["MultiHit"] = Spec("MultiHit", TargetReaction.Shake, 1.00, false,
            Cue(EffectShape.Disc, A, T, 0.00, 0.76, 18, 16, 0.8, 0.8, count: 4, stagger: 0.22, ease: EffectEase.In),
            Cue(EffectShape.Star, T, T, 0.20, 0.96, 56, 18, 1.0, 0.0, count: 4, scatter: 46, stagger: 0.22, secondary: true)),

        ["Spread"] = Spec("Spread", TargetReaction.Shake, 0.95, false,
            Cue(EffectShape.Wave, A, F, 0.00, 0.55, 150, 420, 0.85, 0.35),
            Cue(EffectShape.Ring, F, F, 0.20, 0.92, 120, 470, 0.7, 0.0, secondary: true),
            Cue(EffectShape.Star, F, F, 0.35, 1.00, 40, 12, 0.9, 0.0, count: 8, scatter: 210, stagger: 0.25)),

        ["SelfBuff"] = Spec("SelfBuff", TargetReaction.Rise, 0.85, true,
            Cue(EffectShape.Star, AGround, AAbove, 0.00, 0.92, 14, 30, 0.9, 0.0, count: 8, scatter: 54, stagger: 0.3, ease: EffectEase.Out),
            Cue(EffectShape.Ring, A, A, 0.10, 0.72, 30, 134, 0.7, 0.0, secondary: true)),

        ["Debuff"] = Spec("Debuff", TargetReaction.Sink, 0.85, false,
            Cue(EffectShape.Disc, TAbove, TGround, 0.00, 0.92, 12, 8, 0.85, 0.0, count: 8, scatter: 58, stagger: 0.3, ease: EffectEase.In, secondary: true),
            Cue(EffectShape.Ring, T, T, 0.15, 0.78, 114, 30, 0.6, 0.0)),

        ["StatusInflict"] = Spec("StatusInflict", TargetReaction.Flash, 0.90, false,
            Cue(EffectShape.Disc, A, T, 0.00, 0.70, 12, 14, 0.85, 0.85, count: 7, scatter: 50, stagger: 0.32),
            Cue(EffectShape.Ring, T, T, 0.50, 0.96, 24, 100, 0.7, 0.0),
            Cue(EffectShape.Puff, T, T, 0.52, 1.00, 30, 54, 0.7, 0.0, count: 5, scatter: 46, stagger: 0.2, secondary: true)),

        ["Heal"] = Spec("Heal", TargetReaction.Rise, 0.90, true,
            Cue(EffectShape.Ring, AGround, AAbove, 0.00, 0.72, 92, 60, 0.8, 0.0),
            Cue(EffectShape.Star, AGround, AAbove, 0.05, 0.96, 16, 26, 0.95, 0.0, count: 7, scatter: 48, stagger: 0.28, ease: EffectEase.Out, secondary: true)),

        ["FieldWeather"] = Spec("FieldWeather", TargetReaction.None, 1.10, false,
            Cue(EffectShape.Wave, F, F, 0.00, 0.92, 300, 480, 0.5, 0.0, count: 3, stagger: 0.25),
            Cue(EffectShape.Disc, F, F, 0.10, 1.00, 12, 10, 0.7, 0.0, count: 14, scatter: 320, stagger: 0.4, secondary: true)),

        ["Hazard"] = Spec("Hazard", TargetReaction.None, 0.90, false,
            Cue(EffectShape.Shard, A, TGround, 0.00, 0.76, 16, 12, 0.9, 0.8, count: 8, scatter: 94, stagger: 0.3, spin: 0.8),
            Cue(EffectShape.Puff, TGround, TGround, 0.55, 1.00, 26, 48, 0.6, 0.0, count: 4, scatter: 84, stagger: 0.2, secondary: true)),

        ["Charge"] = Spec("Charge", TargetReaction.Rise, 0.85, true,
            Cue(EffectShape.Disc, AAbove, A, 0.00, 0.80, 16, 8, 0.3, 1.0, count: 8, scatter: 94, stagger: 0.3, ease: EffectEase.In),
            Cue(EffectShape.Ring, A, A, 0.40, 1.00, 126, 24, 0.3, 0.9, secondary: true)),

        ["Drain"] = Spec("Drain", TargetReaction.Sink, 0.95, false,
            Cue(EffectShape.Ring, T, T, 0.00, 0.50, 104, 28, 0.7, 0.0, secondary: true),
            Cue(EffectShape.Disc, T, A, 0.10, 0.96, 14, 10, 0.9, 0.2, count: 9, scatter: 54, stagger: 0.32, ease: EffectEase.InOut)),

        ["Dance"] = Spec("Dance", TargetReaction.Rise, 0.90, true,
            Cue(EffectShape.Ring, A, A, 0.00, 0.92, 40, 152, 0.8, 0.0, count: 3, stagger: 0.25),
            Cue(EffectShape.Star, A, AAbove, 0.10, 1.00, 18, 26, 0.9, 0.0, count: 6, scatter: 72, stagger: 0.3, spin: 1.0, secondary: true)),

        ["Barrier"] = Spec("Barrier", TargetReaction.Rise, 0.80, true,
            Cue(EffectShape.Ring, A, A, 0.00, 0.55, 20, 150, 0.9, 0.55, ease: EffectEase.Out),
            Cue(EffectShape.Disc, A, A, 0.30, 1.00, 150, 156, 0.28, 0.0, secondary: true),
            Cue(EffectShape.Star, A, A, 0.35, 0.95, 22, 12, 0.8, 0.0, count: 6, scatter: 82, stagger: 0.2)),

        ["Generic"] = Spec("Generic", TargetReaction.Shake, 0.80, false,
            Cue(EffectShape.Disc, A, T, 0.00, 0.45, 26, 30, 0.7, 1.0, ease: EffectEase.In),
            Cue(EffectShape.Ring, T, T, 0.42, 0.96, 24, 114, 0.9, 0.0, secondary: true),
            Cue(EffectShape.Star, T, T, 0.45, 1.00, 44, 14, 0.95, 0.0, count: 5, scatter: 46, stagger: 0.2)),
    };
}
