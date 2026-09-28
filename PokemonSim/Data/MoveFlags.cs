using System;
using System.Collections.Generic;
using PokemonSim.Models;

namespace PokemonSim.Data
{
    /// <summary>
    /// Section 158. Contact / pulse / punch / spread classification for
    /// every move in DataFiles/moves.json. These are mechanics knowledge,
    /// not per-game numbers, so they live here as code instead of bloating
    /// the data file: physical moves make contact unless listed, special
    /// moves do not unless listed, status moves never do. MoveDex calls
    /// Annotate on every template it loads; Tough Claws, Iron Fist, Mega
    /// Launcher, King's Shield, the contact punishers and Wide Guard read
    /// the results.
    /// </summary>
    public static class MoveFlags
    {
        static readonly HashSet<string> NonContactPhysical = new(StringComparer.OrdinalIgnoreCase)
        {
            "Attack Order", "Bide", "Blazing Torque", "Bone Rush", "Bulldoze",
            "Bullet Seed", "Combat Torque", "Diamond Storm", "Dragon Darts",
            "Drum Beating", "Earthquake", "Explosion", "Feint", "Fissure",
            "Fling", "Glacial Lance", "Gunk Shot", "Ice Shard", "Icicle Crash",
            "Icicle Spear", "Metal Burst", "Mountain Gale", "Order Up",
            "Petal Blizzard", "Pin Missile", "Pin Missle", "Poison Sting",
            "Poltergeist", "Present", "Psycho Cut", "Pyro Ball", "Razor Leaf",
            "Rock Blast", "Rock Slide", "Rock Throw", "Rock Tomb",
            "Sacred Fire", "Salt Cure", "Sand Tomb", "Scale Shot",
            "Secret Power", "Seed Bomb", "Self-Destruct", "Smack Down",
            "Stone Edge", "Wicked Torque", "Aqua Cutter"
        };

        static readonly HashSet<string> ContactSpecial = new(StringComparer.OrdinalIgnoreCase)
        {
            "Grass Knot", "Draining Kiss", "Infestation", "Petal Dance"
        };

        static readonly HashSet<string> PulseMoves = new(StringComparer.OrdinalIgnoreCase)
        {
            "Water Pulse", "Dark Pulse", "Dragon Pulse", "Aura Sphere", "Heal Pulse"
        };

        static readonly HashSet<string> PunchMoves = new(StringComparer.OrdinalIgnoreCase)
        {
            "Bullet Punch", "Comet Punch", "Drain Punch", "Dynamic Punch",
            "Fire Punch", "Focus Punch", "Ice Punch", "Jet Punch",
            "Mach Punch", "Meteor Mash", "Shadow Punch", "Sky Uppercut",
            "Thunder Punch"
        };

        static readonly HashSet<string> SpreadMoves = new(StringComparer.OrdinalIgnoreCase)
        {
            "Earthquake", "Bulldoze", "Rock Slide", "Heat Wave", "Blizzard",
            "Surf", "Muddy Water", "Discharge", "Lava Plume", "Sludge Wave",
            "Boomburst", "Hyper Voice", "Snarl", "Icy Wind", "Ice Wind",
            "Dazzling Gleam", "Eruption", "Water Spout", "Electroweb",
            "Glaciate", "Incinerate", "Swift", "Petal Blizzard",
            "Parabolic Charge", "Struggle Bug", "Expanding Force",
            "Misty Explosion", "Explosion", "Self-Destruct", "Diamond Storm",
            "Astral Barrage", "Fiery Wrath", "Springtide Storm",
            "Wildbolt Storm", "Clanging Scales", "Air Cutter", "Twister",
            "Acid", "Mortal Spin", "Burning Jealousy", "Brutal Swing",
            "Make It Rain", "Glacial Lance"
        };

        /// <summary>§304. The flatteners: moves that squash a Minimized
        /// target for double damage and cannot miss it. Showdown carries
        /// this as a `minimize` move flag; it is mechanics knowledge, so it
        /// lives here with the rest of them rather than in the data file.</summary>
        static readonly HashSet<string> FlatteningMoves = new(StringComparer.OrdinalIgnoreCase)
        {
            "Astonish", "Body Slam", "Dragon Rush", "Flying Press",
            "Heat Crash", "Heavy Slam", "Malicious Moonsault", "Steamroller",
            "Stomp", "Supercell Slam"
        };

        public static void Annotate(MoveState move)
        {
            move.IsFlattening = FlatteningMoves.Contains(move.Name);

            move.IsContact = move.Category == MoveCategory.Physical
                ? !NonContactPhysical.Contains(move.Name)
                : move.Category == MoveCategory.Special && ContactSpecial.Contains(move.Name);

            move.IsPulse = PulseMoves.Contains(move.Name);
            move.IsPunch = PunchMoves.Contains(move.Name);
            move.IsSpread = SpreadMoves.Contains(move.Name);
        }

        // The battery cross-checks these against the data file, so a typo
        // in a set is caught instead of silently classifying nothing.
        public static IEnumerable<string> AllListedNames()
        {
            foreach (var set in new[] { NonContactPhysical, ContactSpecial, PulseMoves, PunchMoves, SpreadMoves })
                foreach (string name in set)
                    yield return name;
        }
    }
}