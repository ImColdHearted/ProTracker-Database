using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PokemonSim.Actions;
using PokemonSim.Models;

namespace PokemonSim.Observation
{
    /// <summary>
    /// §319. The observation, in words.
    ///
    /// The user's reason for wanting this is the right one: there is no other
    /// way to be sure the network is being given what you think it is, and no
    /// other way to SEE that hidden information is not leaking. A leak is
    /// invisible in a vector of 1206 floats and obvious in a paragraph that
    /// says "Known moves: Earthquake, Stealth Rock, Unknown, Unknown" when you
    /// know perfectly well the thing is carrying Roost.
    ///
    /// Everything here is rendered from the SAME sources the encoder reads -
    /// the acting side's own state and BattleKnowledge - so a dump that looks
    /// right is evidence the vector is right. It deliberately does not read
    /// the battle for the opponent, for exactly the reason the encoder does
    /// not: an inspector that quietly knew more than the encoder would be
    /// worse than no inspector, because it would show a leak that is not
    /// there and hide one that is.
    /// </summary>
    public static class ObservationInspector
    {
        /// <summary>The position as the acting side knows it.</summary>
        public static string Describe(BattleState state, PlayerState actor,
                                      IReadOnlyList<BattleAction>? legalActions = null)
        {
            var text = new StringBuilder();

            PokemonState self = actor.ActivePokemon;
            SideKnowledge them = state.Knowledge.Against(state, actor);
            SideKnowledge us = state.Knowledge.For(state, actor);
            bool actorIsP1 = ReferenceEquals(actor, state.Player1);

            text.AppendLine($"TURN {state.TurnNumber}   ({actor.Name}'s view)");
            text.AppendLine();

            // ------------------------------------------------------- self
            text.AppendLine("SELF");
            text.AppendLine($"  {self.Species}  Lv.{self.Level}");
            text.AppendLine($"  HP: {Percent(self.CurrentHP, self.MaxHP)}");
            text.AppendLine($"  Types: {string.Join(" / ", self.Types)}");
            text.AppendLine($"  Status: {self.Status}");
            string ownAbility = string.IsNullOrWhiteSpace(self.AbilityId) ? "none" : self.AbilityId!;
            string ownItem = string.IsNullOrWhiteSpace(self.HeldItemId) ? "none" : self.HeldItemId!;

            text.AppendLine($"  Ability: {ownAbility}");
            text.AppendLine($"  Item: {ownItem}");
            text.AppendLine($"  Stages: {Stages(self)}");

            string volatiles = Volatiles(self);

            if (volatiles.Length > 0)
                text.AppendLine($"  Volatile: {volatiles}");

            text.AppendLine("  Moves:");

            var slots = ObserverEncoder.SlotMoves(self);
            float[] legal = legalActions == null
                ? new float[ObservationSchema.MoveSlots]
                : ObserverEncoder.LegalMoveMask(legalActions);

            for (int i = 0; i < ObservationSchema.MoveSlots; i++)
            {
                MoveState? move = slots[i];

                if (move == null)
                {
                    text.AppendLine("    - (empty)");
                    continue;
                }

                string mark = legal[i] > 0 ? " " : " (not offered)";

                text.AppendLine($"    - {move.Name}  {move.Type} {move.Category} "
                                + $"{move.Power}bp  {move.CurrentPP}/{move.MaxPP}pp{mark}");
            }

            // --------------------------------------------------- opponent
            ObservedPokemon active = them.Slots.FirstOrDefault(s => s.Active) ?? new ObservedPokemon();

            text.AppendLine();
            text.AppendLine("OPPONENT");

            if (!active.Seen)
            {
                text.AppendLine("  (nothing has been seen yet)");
            }
            else
            {
                text.AppendLine($"  {active.Species}  Lv.{active.Level}");
                text.AppendLine($"  HP: {(int)Math.Round(active.HpFraction * 100)}%");
                text.AppendLine($"  Types: {string.Join(" / ", active.Types)}");
                text.AppendLine($"  Status: {active.Status}");
                text.AppendLine($"  Ability: {active.AbilityName ?? "UNKNOWN"}");
                text.AppendLine($"  Item: {ItemLine(active)}");
                text.AppendLine($"  Stages: {Stages(active)}");

                text.AppendLine("  Known moves:");

                for (int i = 0; i < ObservationSchema.MoveSlots; i++)
                {
                    if (i < active.MovesSeen.Count)
                    {
                        string name = active.MovesSeen[i];
                        int uses = active.MoveUses.TryGetValue(name, out int n) ? n : 0;

                        text.AppendLine($"    - {name}  (used {uses}x)");
                    }
                    else
                    {
                        text.AppendLine("    - UNKNOWN");
                    }
                }

                text.AppendLine("  Evidence:");
                text.AppendLine($"    hardest hit taken: {Pct(active.WorstHitTaken)}"
                                + $"   hardest dealt: {Pct(active.BestHitDealt)}");
                text.AppendLine($"    moved first {active.TimesMovedFirst}x, second {active.TimesMovedSecond}x");
                text.AppendLine($"    turns out: {active.TurnsOnField}, of which boosting: {active.TurnsBoosting}");

                text.AppendLine($"    observed immune to: "
                                + (active.ObservedImmunities.Count == 0
                                    ? "(nothing yet)"
                                    : string.Join(", ", active.ObservedImmunities)));
            }

            // ----------------------------------------------- their team
            text.AppendLine();
            text.AppendLine($"THEIR TEAM  ({them.SeenCount} seen, {them.FaintedCount} down, "
                            + $"{them.UnseenCount} never shown)");

            for (int i = 0; i < them.Slots.Count; i++)
            {
                ObservedPokemon slot = them.Slots[i];

                if (!slot.Seen)
                {
                    text.AppendLine($"  {i + 1}. UNKNOWN");
                    continue;
                }

                string where = slot.Active ? "out" : slot.Fainted ? "fainted" : "benched";

                text.AppendLine($"  {i + 1}. {slot.Species}  {(int)Math.Round(slot.HpFraction * 100)}%  "
                                + $"{where}  {slot.MovesSeen.Count}/4 moves seen");
            }

            // -------------------------------------------------- behaviour
            text.AppendLine();
            text.AppendLine("THEIR BEHAVIOUR (counted, never concluded)");
            text.AppendLine($"  switches: {them.Switches}   last on turn {them.LastSwitchTurn}");
            text.AppendLine($"  moves: {them.MovesUsed}   attacks: {them.AttacksUsed}   "
                            + $"status: {them.StatusMovesUsed}   boosts: {them.BoostMovesUsed}   "
                            + $"protects: {them.ProtectsUsed}");
            text.AppendLine($"  current same-move run: {active.LastMoveStreak}");

            // ---------------------------------------------------- history
            text.AppendLine();
            text.AppendLine($"HISTORY (last {state.Knowledge.History.Count} of {BattleKnowledge.HistoryTurns})");

            foreach (TurnEvent row in state.Knowledge.History)
            {
                string mine = Action(actorIsP1 ? row.P1Kind : row.P2Kind, actorIsP1 ? row.P1Move : row.P2Move);
                string theirs = Action(actorIsP1 ? row.P2Kind : row.P1Kind, actorIsP1 ? row.P2Move : row.P1Move);

                float myDamage = actorIsP1 ? row.P1DamageDealt : row.P2DamageDealt;
                float theirDamage = actorIsP1 ? row.P2DamageDealt : row.P1DamageDealt;

                text.AppendLine($"  Turn {row.Turn,3}:  me -> {mine,-22} ({Pct(myDamage)})"
                                + $"   them -> {theirs,-22} ({Pct(theirDamage)})");
            }

            return text.ToString();
        }

        /// <summary>The raw vector beside its own schema - every column that
        /// is not zero, with the name and meaning the layout declares for it.
        /// What to read when a number looks wrong.</summary>
        public static string DescribeVector(float[] features, bool includeZeroes = false)
        {
            var text = new StringBuilder();

            FeatureMap map = ObservationV8.Map;

            text.AppendLine($"{features.Length} features (layout declares {map.Count})");
            text.AppendLine();

            for (int i = 0; i < features.Length && i < map.Count; i++)
            {
                if (!includeZeroes && Math.Abs(features[i]) < 1e-6f)
                    continue;

                FeatureSpec spec = map.Features[i];

                text.AppendLine($"{i,5}  {features[i],8:0.###}  {spec.Block}.{spec.Name}"
                                + $"  [{spec.Visibility}]  {spec.Meaning}");
            }

            return text.ToString();
        }

        /// <summary>The layout on its own, with no battle - the reference for
        /// what column 704 is.</summary>
        public static string DescribeSchema() => ObservationV8.Map.Describe();

        // ------------------------------------------------------- helpers

        private static string Action(ObservedActionKind kind, string? move) => kind switch
        {
            ObservedActionKind.Move => move ?? "move",
            ObservedActionKind.Switch => "SWITCH",
            ObservedActionKind.Replacement => "sent in",
            _ => "-",
        };

        private static string ItemLine(ObservedPokemon them)
        {
            if (them.ItemName != null)
                return them.ItemName;

            return them.ItemSpent ? "UNKNOWN (one was spent)" : "UNKNOWN";
        }

        private static string Percent(int part, int whole) =>
            whole <= 0 ? "0%" : $"{(int)Math.Round(100.0 * part / whole)}%";

        private static string Pct(float fraction) =>
            $"{(int)Math.Round(fraction * 100)}%";

        private static string Stages(PokemonState mon) =>
            Stages(new[]
            {
                mon.AttackStage, mon.DefenseStage, mon.SpAttackStage,
                mon.SpDefenseStage, mon.SpeedStage, mon.AccuracyStage, mon.EvasionStage,
            });

        private static string Stages(ObservedPokemon mon) => Stages(mon.Stages);

        private static string Stages(int[] stages)
        {
            string[] names = { "Atk", "Def", "SpA", "SpD", "Spe", "Acc", "Eva" };

            var shown = new List<string>();

            for (int i = 0; i < stages.Length && i < names.Length; i++)
            {
                if (stages[i] != 0)
                    shown.Add($"{names[i]} {stages[i]:+#;-#;0}");
            }

            return shown.Count == 0 ? "none" : string.Join(", ", shown);
        }

        private static string Volatiles(PokemonState mon)
        {
            var shown = new List<string>();

            if (mon.ConfusionTurns > 0) shown.Add("confused");
            if (mon.LeechSeeded) shown.Add("seeded");
            if (mon.SubstituteHP > 0) shown.Add("substitute");
            if (mon.TauntTurns > 0) shown.Add("taunted");
            if (mon.EncoreTurns > 0) shown.Add("encored");
            if (mon.DisabledTurns > 0) shown.Add("disabled");
            if (mon.Trap != null) shown.Add("trapped");
            if (mon.Charging) shown.Add("charging");

            return string.Join(", ", shown);
        }
    }
}
