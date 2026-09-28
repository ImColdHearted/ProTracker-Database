using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Engine;
using PokemonSim.Models;

namespace PokemonSim.Observation
{
    /// <summary>
    /// §319. The version 8 observation: the same battle, seen only through
    /// what the acting side could legitimately know.
    ///
    /// WHAT CHANGED, AND WHY IT IS NOT AN ADDITION. §311's version 7 read the
    /// opponent's PokemonState directly and so encoded its exact computed
    /// stats, its ability and its held item, plus an effectiveness, a damage
    /// estimate and a "cancelled outright" flag all three of which are
    /// computed FROM those. Fifty-four of its 483 columns were information no
    /// player could have. Version 8 does not remove those columns and stop
    /// there - removing them would leave a network with no idea how hard the
    /// thing in front of it hits. It replaces each one with the EVIDENCE a
    /// player would actually have:
    ///
    ///     its Attack stat            -> the hardest hit it has been seen to
    ///                                   land, as a fraction of a bar
    ///     its Defense stat           -> the hardest hit it has been seen to
    ///                                   take
    ///     its Speed stat             -> how often it has moved first
    ///     its ability                -> the types it has been OBSERVED to
    ///                                   take nothing from
    ///     its item                   -> whether something it was holding
    ///                                   visibly went away
    ///     its four moves             -> the ones it has been seen to use,
    ///                                   and an UNKNOWN marker for the rest
    ///     its bench                  -> only the members that have stood on
    ///                                   the field
    ///
    /// UNKNOWN IS A CHANNEL, NEVER A ZERO. A move slot the opponent has not
    /// revealed sets its own "unknown" column to one and leaves the rest at
    /// zero; a revealed move with 0 base power sets "known" and leaves power
    /// at zero. Those two rows differ, which is the whole point - a network
    /// given the same row for "no information" and "information that happens
    /// to be zero" cannot learn the difference between them.
    ///
    /// EVERYTHING ABOUT THE OPPONENT COMES FROM BattleKnowledge. This encoder
    /// never touches the opposing PlayerState, and the §319 battery fails the
    /// build if it starts to. The acting side's own half is read directly,
    /// because a player does know their own team.
    ///
    /// THE LAYOUT IS DECLARED, NOT COUNTED. See FeatureMap. A column added to
    /// a block moves everything after it with no renumbering, and every column
    /// carries its meaning, its normalization and its visibility - which is
    /// what the inspector prints and what the leak checker walks.
    /// </summary>
    public static class ObservationV8
    {
        public const int TypeSlots = 18;
        public const int StatusSlots = 6;
        public const int VolatileSlots = 8;
        public const int MoveSlots = ObservationSchema.MoveSlots;
        public const int TeamSlots = ObservationSchema.TeamSlots;
        public const int HistoryTurns = BattleKnowledge.HistoryTurns;

        // ---- block names, so a typo is a missing key and not a wrong index
        public const string Self = "self";
        public const string SelfTypes = "self.types";
        public const string SelfStats = "self.stats";
        public const string SelfAbility = "self.ability";
        public const string SelfItem = "self.item";
        public const string SelfMoves = "self.moves";
        public const string SelfMoveEstimates = "self.moves.estimate";
        public const string Opponent = "opp";
        public const string OpponentTypes = "opp.types";
        public const string OpponentEvidence = "opp.evidence";
        public const string OpponentMoves = "opp.moves";
        public const string Belief = "belief";
        public const string SelfTeam = "self.team";
        public const string OpponentTeam = "opp.team";
        public const string Hazards = "hazards";
        public const string Field = "field";
        public const string History = "history";
        public const string Behaviour = "behaviour";
        public const string Risk = "risk";

        /// <summary>The fair layout - the one that ships.</summary>
        public static readonly FeatureMap Map = BuildMap();

        public static int FeatureCount => Map.Count;

        private static FeatureMap BuildMap()
        {
            var b = new FeatureMapBuilder();

            // ============================================== the acting side
            b.Block(Self)
                .Add("hp", "own active hit points", "0..1", Visibility.Own)
                .Add("level", "own active level", "level/100", Visibility.Own)
                .Add("grounded", "own active is on the ground", "flag", Visibility.Own)
                .AddMany(7, i => $"stage{i}", "own stat stages, Atk Def SpA SpD Spe Acc Eva",
                         "stage/6", Visibility.Own)
                .AddMany(StatusSlots, i => $"status{i}", "own status, one-hot",
                         "one-hot", Visibility.Own)
                .AddMany(VolatileSlots, i => $"volatile{i}",
                         "confused seeded substitute taunt encore disabled trapped charging",
                         "flag", Visibility.Own);

            b.Block(SelfTypes)
                .AddMany(TypeSlots, i => $"type{i}", "own active types", "one-hot", Visibility.Own);

            b.Block(SelfStats)
                .AddMany(6, i => $"stat{i}", "own computed stats", "stat/600", Visibility.Own);

            b.Block(SelfAbility)
                .AddMany(9, i => $"ability{i}", "own ability shape", "flag", Visibility.Own);

            b.Block(SelfItem)
                .AddMany(9, i => $"item{i}", "own held item shape", "flag", Visibility.Own);

            b.Block(SelfMoves).Repeat(MoveSlots, "slot", (m, slot) => m
                .Add($"{slot}.legal", "the engine offered this slot", "flag", Visibility.Own)
                .Add($"{slot}.power", "base power", "power/150", Visibility.Own)
                .Add($"{slot}.accuracy", "accuracy", "0..1", Visibility.Own)
                .Add($"{slot}.pp", "remaining PP", "0..1", Visibility.Own)
                .Add($"{slot}.priority", "priority bracket", "priority/5", Visibility.Own)
                .Add($"{slot}.physical", "physical", "flag", Visibility.Own)
                .Add($"{slot}.special", "special", "flag", Visibility.Own)
                .Add($"{slot}.status", "status move", "flag", Visibility.Own)
                .Add($"{slot}.stab", "same type as the user", "flag", Visibility.Own)
                .AddMany(TypeSlots, i => $"{slot}.type{i}", "move type", "one-hot", Visibility.Own)
                .Add($"{slot}.heal", "heals or drains", "flag", Visibility.Own)
                .Add($"{slot}.boostsSelf", "raises the user's stats", "flag", Visibility.Own)
                .Add($"{slot}.lowersTarget", "lowers the target's stats", "flag", Visibility.Own)
                .Add($"{slot}.statusChance", "chance of a status", "0..1", Visibility.Own)
                .Add($"{slot}.secondary", "flinch or secondary chance", "0..1", Visibility.Own)
                .Add($"{slot}.hits", "average hit count", "hits/5", Visibility.Own)
                .Add($"{slot}.protect", "protects or endures", "flag", Visibility.Own));

            // Separated from the move block on purpose: these three are the
            // only columns about the user's own moves that depend on the
            // OPPONENT, and under §319 they are estimates from visible types
            // rather than answers from its hidden ability and item.
            b.Block(SelfMoveEstimates).Repeat(MoveSlots, "slot", (m, slot) => m
                .Add($"{slot}.effectiveness",
                     "type effectiveness against the opponent's VISIBLE types only",
                     "0..4 scaled", Visibility.Public)
                .Add($"{slot}.seenImmune",
                     "the opponent has been OBSERVED to take nothing from this type",
                     "flag", Visibility.Observed)
                .Add($"{slot}.evidenceDamage",
                     "hardest hit this side has been seen to land, as a stand-in for a damage roll",
                     "0..1", Visibility.Observed));

            // ================================================== the opponent
            b.Block(Opponent)
                .Add("hp", "opponent active hit points - the bar is visible", "0..1", Visibility.Public)
                .Add("level", "opponent active level", "level/100", Visibility.Public)
                .AddMany(7, i => $"stage{i}", "opponent stat stages - announced", "stage/6", Visibility.Public)
                .AddMany(StatusSlots, i => $"status{i}", "opponent status - announced", "one-hot", Visibility.Public)
                .AddMany(VolatileSlots, i => $"volatile{i}", "opponent volatiles - announced", "flag", Visibility.Public)
                .Add("turnsOnField", "turns this Pokemon has spent out", "turns/16", Visibility.Observed)
                .Add("seen", "this Pokemon has stood on the field", "flag", Visibility.Public)
                .Add("justArrived", "it came in this turn", "flag", Visibility.Public);

            b.Block(OpponentTypes)
                .AddMany(TypeSlots, i => $"type{i}",
                         "opponent active types - a species on the field is visible",
                         "one-hot", Visibility.Public);

            b.Block(OpponentEvidence)
                .Add("worstHitTaken", "hardest hit it has been seen to take", "0..1", Visibility.Observed)
                .Add("bestHitDealt", "hardest hit it has been seen to land", "0..1", Visibility.Observed)
                .Add("movedFirstRate", "share of turns it moved before us", "0..1", Visibility.Observed)
                .Add("speedEvidence", "whether there is any turn-order evidence at all", "flag", Visibility.Unknown)
                .Add("itemSpent", "something it held visibly went away", "flag", Visibility.Observed)
                .Add("itemNamed", "the battle named its item", "flag", Visibility.Observed)
                .Add("abilityNamed", "the battle named its ability", "flag", Visibility.Observed)
                .Add("movesSeen", "how many of its moves have been seen", "n/4", Visibility.Observed)
                .Add("boostRate", "share of its turns spent raising stats", "0..1", Visibility.Observed)
                .AddMany(TypeSlots, i => $"immune{i}",
                         "types it has been OBSERVED to take nothing from - evidence, not an ability",
                         "flag", Visibility.Observed);

            b.Block(OpponentMoves).Repeat(MoveSlots, "slot", (m, slot) => m
                .Add($"{slot}.known", "this slot has been revealed", "flag", Visibility.Observed)
                .Add($"{slot}.unknown", "this slot has NOT been revealed", "flag", Visibility.Unknown)
                .Add($"{slot}.power", "base power of the revealed move", "power/150", Visibility.Observed)
                .Add($"{slot}.accuracy", "accuracy of the revealed move", "0..1", Visibility.Observed)
                .Add($"{slot}.priority", "priority of the revealed move", "priority/5", Visibility.Observed)
                .Add($"{slot}.physical", "physical", "flag", Visibility.Observed)
                .Add($"{slot}.special", "special", "flag", Visibility.Observed)
                .Add($"{slot}.status", "status move", "flag", Visibility.Observed)
                .AddMany(TypeSlots, i => $"{slot}.type{i}", "revealed move type", "one-hot", Visibility.Observed)
                .Add($"{slot}.uses", "how often it has been used", "uses/8", Visibility.Observed)
                .Add($"{slot}.recency", "how recently it was used", "0..1", Visibility.Observed));

            b.Block(Belief)
                .Add("revealed", "revealed move slots", "n/4", Visibility.Observed)
                .Add("unrevealed", "slots still unknown - what there is to infer", "n/4", Visibility.Unknown)
                .Add("seenAttack", "it has been seen to attack", "flag", Visibility.Observed)
                .Add("seenStatus", "it has been seen to use a status move", "flag", Visibility.Observed)
                .Add("seenBoost", "it has been seen to raise its stats", "flag", Visibility.Observed)
                .Add("seenProtect", "it has been seen to protect", "flag", Visibility.Observed)
                .Add("seenSwitch", "this side has switched before", "flag", Visibility.Observed)
                .Add("switchRate", "switches per turn so far", "0..1", Visibility.Observed)
                .Add("repeatRate", "share of its moves that repeated the last one", "0..1", Visibility.Observed)
                .Add("streak", "how many times running it has used its last move", "n/8", Visibility.Observed)
                .Add("sinceSwitch", "turns since it last switched", "turns/16", Visibility.Observed)
                .Add("teamSeen", "how much of its team has been seen", "n/6", Visibility.Observed);

            // ====================================================== the teams
            b.Block(SelfTeam).Repeat(TeamSlots, "own", (m, slot) => m
                .Add($"{slot}.present", "this position exists", "flag", Visibility.Own)
                .Add($"{slot}.alive", "still standing", "flag", Visibility.Own)
                .Add($"{slot}.hp", "hit points", "0..1", Visibility.Own)
                .Add($"{slot}.active", "currently out", "flag", Visibility.Own)
                .AddMany(TypeSlots, i => $"{slot}.type{i}", "its types", "one-hot", Visibility.Own));

            b.Block(OpponentTeam).Repeat(TeamSlots, "their", (m, slot) => m
                .Add($"{slot}.present", "this position exists - a team is six", "flag", Visibility.Public)
                .Add($"{slot}.seen", "this Pokemon has stood on the field", "flag", Visibility.Observed)
                .Add($"{slot}.unseen", "this position has NEVER been seen", "flag", Visibility.Unknown)
                .Add($"{slot}.fainted", "seen to faint - a faint is always visible", "flag", Visibility.Public)
                .Add($"{slot}.hp", "hit points AS LAST SEEN, not as they are", "0..1", Visibility.Observed)
                .Add($"{slot}.active", "currently out", "flag", Visibility.Public)
                .Add($"{slot}.sinceSeen", "turns since it was last on the field", "turns/16", Visibility.Observed)
                .Add($"{slot}.movesSeen", "how many of its moves have been seen", "n/4", Visibility.Observed)
                .AddMany(TypeSlots, i => $"{slot}.type{i}",
                         "its types - known only once it has been seen", "one-hot", Visibility.Observed));

            // ====================================================== the field
            b.Block(Hazards)
                .AddMany(4, i => $"mine{i}", "rocks spikes toxic web on our side", "0..1", Visibility.Public)
                .AddMany(4, i => $"theirs{i}", "rocks spikes toxic web on theirs", "0..1", Visibility.Public);

            b.Block(Field)
                .AddMany(5, i => $"mine{i}", "reflect light-screen mist safeguard tailwind", "turns/5", Visibility.Public)
                .AddMany(5, i => $"theirs{i}", "the same, their side", "turns/5", Visibility.Public)
                .Add("trickRoom", "trick room", "turns/5", Visibility.Public)
                .Add("gravity", "gravity", "turns/5", Visibility.Public)
                .Add("weatherTurns", "weather remaining", "turns/8", Visibility.Public)
                .Add("terrainTurns", "terrain remaining", "turns/8", Visibility.Public)
                .AddMany(5, i => $"weather{i}", "weather, one-hot - never an ordinal", "one-hot", Visibility.Public)
                .AddMany(5, i => $"terrain{i}", "terrain, one-hot", "one-hot", Visibility.Public)
                .Add("myMega", "we have used our mega", "flag", Visibility.Own)
                .Add("myZ", "we have used our Z-Move", "flag", Visibility.Own)
                .Add("theirMega", "they have used theirs - it is announced", "flag", Visibility.Public)
                .Add("theirZ", "they have used theirs", "flag", Visibility.Public);

            // ==================================================== the history
            b.Block(History).Repeat(HistoryTurns, "t", (m, row) => m
                .Add($"{row}.present", "this row holds a turn", "flag", Visibility.History)
                .Add($"{row}.age", "how long ago, newest first", "0..1", Visibility.History)
                .AddMany(4, i => $"{row}.mine{i}", "what we did: none move switch replace", "one-hot", Visibility.History)
                .AddMany(4, i => $"{row}.theirs{i}", "what they did", "one-hot", Visibility.History)
                .Add($"{row}.myDamage", "damage we dealt, as a share of their bar", "0..1", Visibility.History)
                .Add($"{row}.theirDamage", "damage they dealt", "0..1", Visibility.History)
                .Add($"{row}.myHp", "our bar after the turn", "0..1", Visibility.History)
                .Add($"{row}.theirHp", "their bar after", "0..1", Visibility.History)
                .Add($"{row}.myFaint", "we lost one", "flag", Visibility.History)
                .Add($"{row}.theirFaint", "they lost one", "flag", Visibility.History)
                .Add($"{row}.myStages", "our stage sum", "sum/12", Visibility.History)
                .Add($"{row}.theirStages", "their stage sum", "sum/12", Visibility.History)
                .Add($"{row}.myBoost", "our stage sum rose this turn", "flag", Visibility.History)
                .Add($"{row}.theirBoost", "theirs rose - setup, as an event", "flag", Visibility.History)
                .Add($"{row}.myRepeat", "we repeated our previous move", "flag", Visibility.History)
                .Add($"{row}.theirRepeat", "they repeated theirs", "flag", Visibility.History)
                .Add($"{row}.myStatus", "we were under a status", "flag", Visibility.History)
                .Add($"{row}.theirStatus", "they were", "flag", Visibility.History)
                .Add($"{row}.theirSwap", "their active changed", "flag", Visibility.History)
                .Add($"{row}.weather", "weather changed", "flag", Visibility.History)
                .Add($"{row}.terrain", "terrain changed", "flag", Visibility.History)
                .Add($"{row}.hazards", "hazards changed", "flag", Visibility.History));

            b.Block(Behaviour)
                .Add("theirSwitchRate", "their switches per turn", "0..1", Visibility.History)
                .Add("theirRecentSwitches", "their switches in the last five turns", "n/5", Visibility.History)
                .Add("theirSinceSwitch", "turns since their last switch", "turns/16", Visibility.History)
                .Add("theirRepeatRate", "share of their turns that repeated a move", "0..1", Visibility.History)
                .Add("theirStreak", "their current same-move run", "n/8", Visibility.History)
                .Add("theirAttackRate", "share of their actions that were attacks", "0..1", Visibility.History)
                .Add("theirStatusRate", "share that were status moves", "0..1", Visibility.History)
                .Add("theirBoostRate", "share that raised their stats", "0..1", Visibility.History)
                .Add("theirProtectRate", "share that were protects", "0..1", Visibility.History)
                .Add("theirMeanDamage", "their mean damage per turn", "0..1", Visibility.History)
                .Add("theirRecentDamage", "their mean over the last three turns", "0..1", Visibility.History)
                .Add("theirDamageTrend", "recent minus overall - are they speeding up", "-1..1", Visibility.History)
                .Add("theirHpTrend", "their bar's movement over the window", "-1..1", Visibility.History)
                .Add("theirSwapAfterDamage", "share of their switches that followed a hit", "0..1", Visibility.History)
                .Add("mySwitchRate", "our own switches per turn", "0..1", Visibility.History)
                .Add("myRepeatRate", "our own repetition - what they can read off us", "0..1", Visibility.History)
                .Add("myStreak", "our own same-move run", "n/8", Visibility.History)
                .Add("myAttackRate", "our own attack share", "0..1", Visibility.History)
                .Add("myMeanDamage", "our mean damage per turn", "0..1", Visibility.History)
                .Add("myRecentDamage", "our mean over the last three", "0..1", Visibility.History)
                .Add("myHpTrend", "our bar's movement", "-1..1", Visibility.History)
                .Add("turn", "how far into the battle we are", "turn/50", Visibility.Public)
                .Add("historyDepth", "how many turns of history exist", "0..1", Visibility.History)
                .Add("earlyBattle", "fewer than three turns have been seen", "flag", Visibility.History);

            b.Block(Risk)
                .Add("risk", "§182 teacher dial - not about the battle", "0..1", Visibility.Meta);

            return b.Build();
        }
    }
}
