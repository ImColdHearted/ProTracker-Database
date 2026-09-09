using System;
using System.Collections.Generic;
using PokemonSim.Actions;
using PokemonSim.Models;

namespace PokemonSim.Engine
{
    /// <summary>
    /// Section 161. Z-Moves: hold the type's Z-Crystal, pick a damaging
    /// move of that type, and once per battle per side it fires as the
    /// type's Z-Move - the games' base-power table, a hit that cannot miss,
    /// no secondary effects, and a quarter of its damage leaking through
    /// Protect. The Z-Move is synthesized the way Struggle is (MaxPP 0, so
    /// the resolver's PP block skips it); the engine spends one PP of the
    /// base move instead and keeps the base move as the user's "last move"
    /// so Encore and Torment keep meaning something. Status moves keep
    /// their plain form - the games' status Z-effects are out of scope and
    /// the picker never offers them.
    /// </summary>
    public static class ZMoves
    {
        sealed class ZEntry
        {
            public required string CrystalId;
            public required string CrystalName;
            public required string MoveName;
        }

        static readonly Dictionary<PokemonType, ZEntry> byType = new()
        {
            [PokemonType.Normal] = new ZEntry { CrystalId = "normaliumz", CrystalName = "Normalium Z", MoveName = "Breakneck Blitz" },
            [PokemonType.Fire] = new ZEntry { CrystalId = "firiumz", CrystalName = "Firium Z", MoveName = "Inferno Overdrive" },
            [PokemonType.Water] = new ZEntry { CrystalId = "wateriumz", CrystalName = "Waterium Z", MoveName = "Hydro Vortex" },
            [PokemonType.Electric] = new ZEntry { CrystalId = "electriumz", CrystalName = "Electrium Z", MoveName = "Gigavolt Havoc" },
            [PokemonType.Grass] = new ZEntry { CrystalId = "grassiumz", CrystalName = "Grassium Z", MoveName = "Bloom Doom" },
            [PokemonType.Ice] = new ZEntry { CrystalId = "iciumz", CrystalName = "Icium Z", MoveName = "Subzero Slammer" },
            [PokemonType.Fighting] = new ZEntry { CrystalId = "fightiniumz", CrystalName = "Fightinium Z", MoveName = "All-Out Pummeling" },
            [PokemonType.Poison] = new ZEntry { CrystalId = "poisoniumz", CrystalName = "Poisonium Z", MoveName = "Acid Downpour" },
            [PokemonType.Ground] = new ZEntry { CrystalId = "groundiumz", CrystalName = "Groundium Z", MoveName = "Tectonic Rage" },
            [PokemonType.Flying] = new ZEntry { CrystalId = "flyiniumz", CrystalName = "Flyinium Z", MoveName = "Supersonic Skystrike" },
            [PokemonType.Psychic] = new ZEntry { CrystalId = "psychiumz", CrystalName = "Psychium Z", MoveName = "Shattered Psyche" },
            [PokemonType.Bug] = new ZEntry { CrystalId = "buginiumz", CrystalName = "Buginium Z", MoveName = "Savage Spin-Out" },
            [PokemonType.Rock] = new ZEntry { CrystalId = "rockiumz", CrystalName = "Rockium Z", MoveName = "Continental Crush" },
            [PokemonType.Ghost] = new ZEntry { CrystalId = "ghostiumz", CrystalName = "Ghostium Z", MoveName = "Never-Ending Nightmare" },
            [PokemonType.Dragon] = new ZEntry { CrystalId = "dragoniumz", CrystalName = "Dragonium Z", MoveName = "Devastating Drake" },
            [PokemonType.Dark] = new ZEntry { CrystalId = "darkiniumz", CrystalName = "Darkinium Z", MoveName = "Black Hole Eclipse" },
            [PokemonType.Steel] = new ZEntry { CrystalId = "steeliumz", CrystalName = "Steelium Z", MoveName = "Corkscrew Crash" },
            [PokemonType.Fairy] = new ZEntry { CrystalId = "fairiumz", CrystalName = "Fairium Z", MoveName = "Twinkle Tackle" }
        };

        /// <summary>Crystal display name for a type ("Normalium Z") - what
        /// the team builder attaches.</summary>
        public static string CrystalNameFor(PokemonType type) => byType[type].CrystalName;

        /// <summary>The Z-Move's name for a type ("Breakneck Blitz").</summary>
        public static string MoveNameFor(PokemonType type) => byType[type].MoveName;

        /// <summary>The games' Z-Move base-power table, keyed on the base
        /// move's power.</summary>
        public static int ZPower(int basePower)
        {
            if (basePower <= 55) return 100;
            if (basePower <= 65) return 120;
            if (basePower <= 75) return 140;
            if (basePower <= 85) return 160;
            if (basePower <= 95) return 175;
            if (basePower <= 100) return 180;
            if (basePower <= 110) return 185;
            if (basePower <= 125) return 190;
            if (basePower <= 130) return 195;
            return 200;
        }

        /// <summary>Whether this base move fires as a Z-Move right now:
        /// the user holds the matching crystal, the move really deals
        /// typed damage, and this side has not used its Z-Power yet.
        /// Encore and a charge lock rule it out the same way they pin the
        /// move menu.</summary>
        public static bool CanUse(BattleState state, PlayerState side, PokemonState user, MoveState baseMove)
        {
            if (side.UsedZMove || user.Fainted || user.Charging || user.EncoreTurns > 0)
                return false;

            if (baseMove.Category == MoveCategory.Status || baseMove.Power <= 0 ||
                baseMove.Typeless || baseMove.MaxPP <= 0)
            {
                return false;
            }

            return HoldsCrystalFor(user, baseMove.Type);
        }

        static bool HoldsCrystalFor(PokemonState user, PokemonType type)
        {
            return user.HeldItemId != null &&
                Items.HeldItems.Normalize(user.HeldItemId) == byType[type].CrystalId;
        }

        /// <summary>Whether the Z-Power toggle should show at all: the
        /// crystal is held and at least one usable move matches it.</summary>
        public static bool AvailableFor(BattleState state, PlayerState side, PokemonState user)
        {
            foreach (MoveState move in user.Moves)
            {
                if (move.CurrentPP > 0 && CanUse(state, side, user, move))
                    return true;
            }

            return false;
        }

        /// <summary>The synthesized Z-Move - the Struggle pattern: not in
        /// any data file, MaxPP 0 so the resolver never spends PP on it,
        /// accuracy 0 so it cannot miss, no effects list so no secondaries
        /// run. It keeps the base move's category and priority.</summary>
        public static MoveState Synthesize(MoveState baseMove)
        {
            return new MoveState
            {
                Name = MoveNameFor(baseMove.Type),
                Type = baseMove.Type,
                Category = baseMove.Category,
                Power = ZPower(baseMove.Power),
                Accuracy = 0,
                MaxPP = 0,
                CurrentPP = 0,
                Priority = baseMove.Priority,
                IsZMove = true
            };
        }

        /// <summary>Section 161's computer-opponent trigger: from this
        /// side's legal actions, the Z-eligible move whose Z-Move looks
        /// lethal against the defender - or null to play normally. The
        /// estimate runs one damage sample on a clone with its own fixed
        /// rng, so the visible battle's rng position never moves.</summary>
        public static BattleAction? TryPickLethalZ(
            BattleState state,
            PlayerState side,
            PokemonState attacker,
            PokemonState defender,
            IReadOnlyList<BattleAction> legalActions)
        {
            if (side.UsedZMove || defender.Fainted)
                return null;

            BattleAction? best = null;
            int bestEstimate = -1;

            foreach (BattleAction action in legalActions)
            {
                if (action.Type != BattleActionType.Move || action.Move == null)
                    continue;

                if (!CanUse(state, side, attacker, action.Move))
                    continue;

                int estimate = EstimateZDamage(state, side, action.Move);

                if (estimate >= defender.CurrentHP && estimate > bestEstimate)
                {
                    best = action;
                    bestEstimate = estimate;
                }
            }

            return best;
        }

        static int EstimateZDamage(BattleState state, PlayerState side, MoveState baseMove)
        {
            try
            {
                BattleState clone = state.Clone();
                clone.Rng = new BattleRng(987161);

                PlayerState cloneSide = ReferenceEquals(side, state.Player1)
                    ? clone.Player1
                    : clone.Player2;

                PokemonState cloneAttacker = cloneSide.ActivePokemon;
                PokemonState cloneDefender = clone.GetOpponentOf(cloneSide).ActivePokemon;

                DamageResult result = DamageCalculator.CalculateDamage(
                    clone, cloneAttacker, cloneDefender, Synthesize(baseMove));

                return result.Effectiveness <= 0 ? 0 : result.Damage;
            }
            catch
            {
                return 0;
            }
        }
    }
}