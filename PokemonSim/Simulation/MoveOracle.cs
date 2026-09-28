using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Simulation
{
    /// <summary>
    /// §306. What a move actually does, right now, in this battle.
    ///
    /// It lives in the simulator rather than in the tracker that asked for
    /// it, for two reasons. The knowledge is the simulator's - §158, §303 and
    /// §304 wrote these formulas and they belong beside them - and a thing in
    /// here can be tested by the simulator's own suite, where a thing in the
    /// tracker's Services folder could only be tested by running the
    /// tracker.
    ///
    /// THE PROBLEM IT WAS BUILT FOR. The tracker's Damage Calculator reads
    /// its own move file
    /// (SharedPokemonLibrary/Data/Moves/moves.json), where a move's effect is
    /// a sentence for a reader rather than a rule. That left it with three
    /// separate wrongs: thirteen damaging moves it REFUSED outright because
    /// that file carries no power for them (Low Kick, Gyro Ball, Reversal,
    /// Return and the rest); thirty-six it priced at their LISTED power,
    /// which never varies, so Acrobatics was only right while holding an item
    /// and Triple Kick showed its first hit's ten; and thirteen more whose
    /// answer is a fixed damage rather than a roll, refused for the same
    /// reason as the first group.
    ///
    /// The simulator can answer all sixty-two. §158, §303 and §304 built the
    /// formulas; the tracker already references that project. So this asks it.
    ///
    /// HOW IT ASKS. Not by calling the formulas - they are written as effects
    /// that rescale a damage figure, which is the shape the resolver needs -
    /// but by running them the way the resolver does and watching what comes
    /// out. The move is resolved twice against the same battle, once with a
    /// probe damage and once with twice that, and the two answers say which
    /// kind of move this is:
    ///
    ///   both answers the same        -> a FIXED damage (Seismic Toss, Super
    ///                                   Fang, the OHKOs): the probe did not
    ///                                   matter, so the move ignores it
    ///   the second is twice the first -> a POWER: the effect scaled what it
    ///                                   was given, and the factor is the
    ///                                   real power over the listed one
    ///   unchanged                     -> the listed power, and nothing to say
    ///
    /// Measuring the effects rather than asking them a parallel question is
    /// the point: what this reports is what the engine will actually do,
    /// including any formula added after this file was written. Nothing in
    /// the simulator had to change to be asked.
    ///
    /// ONLY THE PHASES THAT DECIDE POWER run - BeforeMove and BeforeDamage.
    /// Recoil, drain and the volatiles live in the later phases and are not
    /// touched, so probing cannot set anything on fire. The battle it probes
    /// against is built here and thrown away.
    /// </summary>
    public static class MoveOracle
    {
        /// <summary>Large enough that a formula's integer division keeps five
        /// digits of the answer, small enough that nothing overflows when an
        /// effect multiplies it by a base power.</summary>
        private const int Probe = 100_000;

        public enum AnswerKind
        {
            /// <summary>The simulator's move file has no such move, so it has
            /// nothing to say. The calculator falls back to its own data.</summary>
            Unknown,

            /// <summary>Nothing changes the power - use the listed one.</summary>
            Listed,

            /// <summary>The power right now, worked out from the battle.</summary>
            Power,

            /// <summary>The move deals exactly this much, and no damage roll
            /// applies.</summary>
            FixedDamage,

            /// <summary>The move does nothing at all against this target -
            /// an immunity, or a condition the move needs and does not have.</summary>
            NoEffect,
        }

        public sealed class Answer
        {
            public AnswerKind Kind { get; init; } = AnswerKind.Unknown;

            /// <summary>Base power, for Listed and Power.</summary>
            public int Power { get; init; }

            /// <summary>Exact damage, for FixedDamage.</summary>
            public int Damage { get; init; }

            /// <summary>The move's type after the simulator has had its say -
            /// Weather Ball's changes with the weather.</summary>
            public string Type { get; init; } = "";

            /// <summary>One sentence for the calculator's banner, or empty
            /// when there is nothing worth saying.</summary>
            public string Note { get; init; } = "";

            /// <summary>How many times the move hits. The calculator's own
            /// move file does not carry this at all, so a five-hit Icicle
            /// Spear looked like a 25-power tickle; the simulator's does.</summary>
            public int MinHits { get; init; } = 1;
            public int MaxHits { get; init; } = 1;

            public static readonly Answer NotFound = new() { Kind = AnswerKind.Unknown };
        }

        /// <summary>One side of the calculation, in the terms the calculator
        /// already holds. Everything here is something the window can show or
        /// be told; nothing is invented.</summary>
        public sealed class Side
        {
            public string Species { get; set; } = "";
            public IReadOnlyList<string> Types { get; set; } = Array.Empty<string>();
            public int Level { get; set; } = 100;

            public int MaxHp { get; set; } = 100;
            public int CurrentHp { get; set; } = 100;

            public int Attack { get; set; } = 100;
            public int Defense { get; set; } = 100;
            public int SpAttack { get; set; } = 100;
            public int SpDefense { get; set; } = 100;
            public int Speed { get; set; } = 100;

            public int AttackStage { get; set; }
            public int DefenseStage { get; set; }
            public int SpAttackStage { get; set; }
            public int SpDefenseStage { get; set; }
            public int SpeedStage { get; set; }

            public string Status { get; set; } = "None";
            public string Ability { get; set; } = "None";
            public string Item { get; set; } = "None";

            /// <summary>Return and Frustration, and nothing else.</summary>
            public int Happiness { get; set; } = 255;

            /// <summary>Assurance asks it of the target, Avalanche and Revenge
            /// of the user.</summary>
            public bool HurtThisTurn { get; set; }

            /// <summary>Rage Fist.</summary>
            public int TimesAttacked { get; set; }

            /// <summary>Fury Cutter, Echoed Voice and Rollout - this use
            /// included, so one is the first.</summary>
            public int ConsecutiveUses { get; set; } = 1;

            /// <summary>Stomping Tantrum and Temper Flare.</summary>
            public bool LastMoveFailed { get; set; }

            /// <summary>Rollout again - Defense Curl doubles it.</summary>
            public bool DefenseCurled { get; set; }

            /// <summary>Charge: the next Electric move is twice as strong.</summary>
            public bool Charged { get; set; }
        }

        public sealed class Context
        {
            public Side Attacker { get; set; } = new();
            public Side Defender { get; set; } = new();

            public string Weather { get; set; } = "None";
            public string Terrain { get; set; } = "None";

            public bool Gravity { get; set; }

            /// <summary>Payback, Bolt Beak, Fishious Rend and Pursuit.</summary>
            public bool AttackerMovesFirst { get; set; } = true;

            /// <summary>Pursuit doubles into a target that is leaving.</summary>
            public bool DefenderSwitchingOut { get; set; }

            /// <summary>Triple Kick and Triple Axel - which of the three.</summary>
            public int HitNumber { get; set; } = 1;
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// §306a. Whether the simulator has a damaging move by this name at
        /// all - a cheap question, asked without resolving anything.
        ///
        /// It exists because the tracker's calculator builds its move list
        /// from its OWN file, which carries no power for the twenty-six moves
        /// this class was written to answer. A list filtered on that file's
        /// power drops every one of them before the learnset is even
        /// consulted, so the calculator learned how to answer Low Kick and
        /// then never offered it. This is the question that list should be
        /// asking instead.
        /// </summary>
        public static bool IsDamaging(string? moveName)
        {
            if (string.IsNullOrWhiteSpace(moveName))
                return false;

            try
            {
                MoveDex.EnsureLoaded();
            }
            catch
            {
                return false;
            }

            return MoveDex.TryGet(moveName, out MoveState move)
                && move.Category != MoveCategory.Status
                && move.Power > 0;
        }

        /// <summary>
        /// §307. What the two sides' abilities do to this hit, as a factor on
        /// the damage: 1.0 for no change, 0.0 for an immunity, 3.0 for Guts
        /// powering a burned attacker through its own penalty.
        ///
        /// HOW IT IS MEASURED, and why that is the whole design. The move is
        /// resolved twice against two throwaway battles that differ in exactly
        /// one thing - whether the two Pokemon have their abilities - and the
        /// answer is the ratio of the damage. Everything else is identical,
        /// including the seeded rng, so every other modifier cancels and what
        /// is left is the abilities and nothing but.
        ///
        /// That cancelling is what makes the awkward cases come out right
        /// without being special-cased. Guts is the one to look at: the
        /// baseline run is a burned attacker taking the usual halving, and
        /// the ability run is a burned attacker that ignores the halving AND
        /// gets half again, so the ratio is three - which is exactly what a
        /// caller that applies its own burn rule needs multiplying in. No
        /// list of "abilities that also cancel a burn" exists anywhere,
        /// because the measurement already knows.
        ///
        /// It is measured off the RESOLVER rather than off the damage
        /// calculator, because a good half of the engine's ability rules live
        /// in the resolver's hit loop - Tinted Lens, Filter, Sniper - and a
        /// probe that only ran the damage calculator would report 1.0 for
        /// every one of them.
        ///
        /// An ability the engine has no rule for is not an error; it comes
        /// back 1.0, and PokemonDex.AbilityIsSimulated is how a caller tells
        /// that apart from an ability that genuinely does nothing here.
        /// </summary>
        public static double AbilityFactor(string? moveName, Context context)
        {
            if (string.IsNullOrWhiteSpace(moveName))
                return 1.0;

            try
            {
                MoveDex.EnsureLoaded();
                PokemonDex.EnsureLoaded();
            }
            catch
            {
                return 1.0;
            }

            if (!MoveDex.TryGet(moveName, out MoveState _))
                return 1.0;

            bool anyAbility =
                !Nothing(context.Attacker.Ability) || !Nothing(context.Defender.Ability);

            if (!anyAbility)
                return 1.0;

            // Both runs of a PAIR share a seed, so for almost every ability
            // the roll cancels exactly and the ratio is a clean 2.0 or 0.5.
            // The exception is an ability that changes how many times the rng
            // is drawn from - Skill Link forcing a multi-hit to its maximum
            // rather than rolling for it - which desynchronises everything
            // after that draw. Several pairs, summed, is what makes those
            // come out as the number they are instead of as whichever roll
            // the first seed happened to give.
            long with = 0;
            long without = 0;

            for (int seed = 1; seed <= ProbeSeeds; seed++)
            {
                with += ResolveDamage(moveName!, context, abilities: true, items: true, seed);
                without += ResolveDamage(moveName!, context, abilities: false, items: true, seed);
            }

            // Nothing landed even without the ability - the move is immune by
            // type, or does no damage at all. There is no ratio to take, and
            // the caller's own type chart already says so.
            if (without <= 0)
                return 1.0;

            return (double)with / without;
        }


        /// <summary>
        /// §309. What the two sides' HELD ITEMS do to this hit, as a factor
        /// on the damage - measured the same way §307 measures abilities, and
        /// for the same reason: the calculator carried four item rules of its
        /// own (Life Orb, Expert Belt, Muscle Band, Wise Glasses) while the
        /// engine carried those four and a hundred more, and two places
        /// applying one rule is how a number gets applied twice.
        ///
        /// WHY IT IS MEASURED WITHOUT THE ABILITIES. AbilityFactor is
        /// with-abilities over without-abilities, both runs holding their
        /// items. This is with-items over without-items, both runs stripped
        /// of abilities. Multiply the two and the middle term cancels
        /// exactly:
        ///
        ///     both        noAbility        both
        ///     --------- x ---------  =  ---------
        ///     noAbility   neither        neither
        ///
        /// so the pair of factors the calculator applies is the engine's own
        /// answer for "everything these two Pokemon are carrying and are",
        /// not an assumption that an ability and an item never meet. They do
        /// meet - Sheer Force and Life Orb is the famous one, where the
        /// ability cancels the item's recoil - and a decomposition that
        /// assumed independence would have to special-case it.
        ///
        /// What it does NOT catch, and correctly: Life Orb's recoil, Rocky
        /// Helmet and Weakness Policy all land after the hit rather than on
        /// it, and Focus Sash and Sitrus never fire because the probe's
        /// target has room for any hit to land in full. Those belong to the
        /// KO count, and DamageCalculatorViewModel counts them there.
        /// </summary>
        public static double ItemFactor(string? moveName, Context context)
        {
            if (string.IsNullOrWhiteSpace(moveName))
                return 1.0;

            try
            {
                MoveDex.EnsureLoaded();
                PokemonDex.EnsureLoaded();
            }
            catch
            {
                return 1.0;
            }

            if (!MoveDex.TryGet(moveName, out MoveState _))
                return 1.0;

            bool anyItem = !Nothing(context.Attacker.Item) || !Nothing(context.Defender.Item);

            if (!anyItem)
                return 1.0;

            long with = 0;
            long without = 0;

            for (int seed = 1; seed <= ProbeSeeds; seed++)
            {
                with += ResolveDamage(moveName!, context, abilities: false, items: true, seed);
                without += ResolveDamage(moveName!, context, abilities: false, items: false, seed);
            }

            if (without <= 0)
                return 1.0;

            return (double)with / without;
        }

        /// <summary>How many with/without pairs AbilityFactor averages over.</summary>
        private const int ProbeSeeds = 8;

        /// <summary>§307. Plays the move out once and answers how much the
        /// target lost. The move cannot miss and nothing may faint, so the
        /// number is the hit itself rather than a story about a turn.
        ///
        /// §309 gave it a second switch. Both sides' abilities and both
        /// sides' held items can be taken away independently, which is what
        /// lets two factors be measured off one probe and multiplied together
        /// without assuming they do not interact - see ItemFactor.</summary>
        static int ResolveDamage(string moveName, Context context, bool abilities, bool items, int seed)
        {
            MoveState move = MoveDex.Get(moveName);

            // A probe is not asking whether the move lands.
            move.Accuracy = 0;

            PokemonState attacker = Build(context.Attacker, move);
            PokemonState defender = Build(context.Defender, new MoveState
            {
                Name = "Wait",
                Type = PokemonType.Normal,
                Category = MoveCategory.Physical,
                Power = 50,
                MaxPP = 10,
                CurrentPP = 10,
            });

            if (!abilities)
            {
                attacker.AbilityId = null;
                defender.AbilityId = null;
            }

            // §309. Both sides, because an item defends as well as attacks -
            // Eviolite and the resist berries are the whole reason the
            // window's defending dropdown had never done anything.
            if (!items)
            {
                attacker.HeldItemId = null;
                defender.HeldItemId = null;
            }

            // Room for any hit to land in full. A defender that fainted would
            // stop a multi-hit early and make the two runs incomparable.
            const int Roomy = 100_000_000;

            defender.MaxHP = Roomy;
            defender.CurrentHP = Roomy;
            attacker.MaxHP = Roomy;
            attacker.CurrentHP = Roomy;

            BattleState state = Assemble(context, attacker, defender);

            state.Rng = new BattleRng(seed);

            // Engine.Abilities, not Abilities: this file sits in
            // PokemonSim.Simulation, so there is no enclosing PokemonSim.Engine
            // for a bare "Abilities" to be found under, and `using
            // PokemonSim.Engine;` imports that namespace's TYPES rather than
            // making its child namespaces reachable by their short name. The
            // effect files get away with the short form because they live
            // inside PokemonSim.Engine themselves.
            Engine.Abilities.AbilityFactory.Restore(attacker, state);
            Engine.Abilities.AbilityFactory.Restore(defender, state);

            Engine.MoveResolver.Resolve(state, attacker, defender, move);

            return Roomy - defender.CurrentHP;
        }

        public static Answer Ask(string? moveName, Context context)
        {
            if (string.IsNullOrWhiteSpace(moveName))
                return Answer.NotFound;

            try
            {
                MoveDex.EnsureLoaded();
                PokemonDex.EnsureLoaded();
            }
            catch
            {
                return Answer.NotFound;
            }

            if (!MoveDex.TryGet(moveName, out MoveState probeMove))
                return Answer.NotFound;

            // Two runs against two fresh battles. Fresh, because a formula may
            // change the battle as it answers - Wake-Up Slap wakes the target
            // up - and the second run has to ask the same question as the
            // first, not the question the first one left behind.
            (int first, MoveState resolved) = RunOnce(moveName!, context, Probe);
            (int second, _) = RunOnce(moveName!, context, Probe * 2);

            string type = resolved.Type.ToString();

            if (first <= 0 && second <= 0)
            {
                return new Answer
                {
                    Kind = AnswerKind.NoEffect,
                    Type = type,
                    MinHits = resolved.MinHits,
                    MaxHits = resolved.MaxHits,
                    Note = "The simulator says this move does nothing here.",
                };
            }

            // The probe did not matter, so the move is not scaling it - it is
            // naming a damage of its own.
            if (first == second)
            {
                return new Answer
                {
                    Kind = AnswerKind.FixedDamage,
                    Damage = first,
                    Type = type,
                    MinHits = resolved.MinHits,
                    MaxHits = resolved.MaxHits,
                    Note = FixedNote(moveName!),
                };
            }

            int listed = Math.Max(1, resolved.Power);

            if (first == Probe)
            {
                return new Answer
                {
                    Kind = AnswerKind.Listed,
                    Power = listed,
                    Type = type,
                    MinHits = resolved.MinHits,
                    MaxHits = resolved.MaxHits,
                };
            }

            // Rounded, not truncated. The probe comes back as an integer, so
            // the factor it encodes is already a little short of the real
            // one: Reversal at 200 power off a listed 70 divides to 285714,
            // and multiplying that back out lands on 199.99, which truncation
            // would report as 199. Half a probe closes it.
            long power = ((long)listed * first + Probe / 2) / Probe;

            return new Answer
            {
                Kind = AnswerKind.Power,
                Power = (int)Math.Clamp(power, 1, 2000),
                Type = type,
                MinHits = resolved.MinHits,
                MaxHits = resolved.MaxHits,
                Note = PowerNote(moveName!, listed, (int)power),
            };
        }

        // ------------------------------------------------------------------

        static (int Damage, MoveState Move) RunOnce(string moveName, Context context, int probe)
        {
            MoveState move = MoveDex.Get(moveName);

            PokemonState attacker = Build(context.Attacker, move);
            PokemonState defender = Build(context.Defender, new MoveState
            {
                Name = "Wait",
                Type = PokemonType.Normal,
                Category = MoveCategory.Physical,
                Power = 50,
                MaxPP = 10,
                CurrentPP = 10,
            });

            BattleState state = Assemble(context, attacker, defender);

            int damage = probe;
            bool cancelled = false;

            // Only the two phases that can decide a power. Recoil, drain and
            // every volatile live later and are deliberately never reached.
            foreach (MovePhase phase in new[] { MovePhase.BeforeMove, MovePhase.BeforeDamage })
            {
                foreach (string name in move.Effects ?? (IEnumerable<string>)Array.Empty<string>())
                {
                    IMoveEffect? effect = MoveEffectRegistry.Get(name);

                    if (effect == null || effect.Phase != phase)
                        continue;

                    effect.Apply(state, attacker, defender, move, ref damage, ref cancelled);

                    if (cancelled)
                        return (0, move);
                }
            }

            return (Math.Max(0, damage), move);
        }

        static BattleState Assemble(Context context, PokemonState attacker, PokemonState defender)
        {
            var state = new BattleState
            {
                Player1 = new PlayerState
                {
                    Name = "Attacker",
                    Team = new List<PokemonState> { attacker },
                    ActivePokemon = attacker,
                },
                Player2 = new PlayerState
                {
                    Name = "Defender",
                    Team = new List<PokemonState> { defender },
                    ActivePokemon = defender,
                },
                Rng = new BattleRng(1),
                Log = new BattleLog { Silent = true },
                Initialized = true,
                TurnNumber = 1,
            };

            state.Environment.Weather = ParseWeather(context.Weather);
            state.Environment.WeatherTurns = state.Environment.Weather == WeatherType.None ? 0 : 5;

            state.Environment.Terrain = ParseTerrain(context.Terrain);
            state.Environment.TerrainTurns = state.Environment.Terrain == TerrainType.None ? 0 : 5;

            state.GravityTurns = context.Gravity ? 5 : 0;

            // §304's two forward-looking moves read the turn's declarations.
            // The calculator's "moves first" toggle is the same question, and
            // this is how it reaches them: a target that has already acted is
            // one the user is moving after.
            state.CurrentHitNumber = Math.Max(1, context.HitNumber);

            defender.ActedThisTurn = !context.AttackerMovesFirst;

            if (context.DefenderSwitchingOut)
            {
                state.DeclaredP2 = new Actions.BattleAction
                {
                    Type = BattleActionType.Switch,
                    User = defender,
                    Owner = state.Player2,
                };

                defender.ActedThisTurn = false;
            }

            return state;
        }

        static PokemonState Build(Side side, MoveState move)
        {
            var types = new List<PokemonType>();

            foreach (string name in side.Types)
            {
                if (Enum.TryParse(name, ignoreCase: true, out PokemonType parsed))
                    types.Add(parsed);
            }

            if (types.Count == 0)
                types.Add(PokemonType.Normal);

            int maxHp = Math.Max(1, side.MaxHp);

            return new PokemonState
            {
                Species = side.Species,
                Types = types,
                Level = Math.Clamp(side.Level, 1, 100),
                MaxHP = maxHp,
                CurrentHP = Math.Clamp(side.CurrentHp, 1, maxHp),
                Stats = new Stats
                {
                    HP = maxHp,
                    Attack = Math.Max(1, side.Attack),
                    Defense = Math.Max(1, side.Defense),
                    SpAttack = Math.Max(1, side.SpAttack),
                    SpDefense = Math.Max(1, side.SpDefense),
                    Speed = Math.Max(1, side.Speed),
                },
                AttackStage = side.AttackStage,
                DefenseStage = side.DefenseStage,
                SpAttackStage = side.SpAttackStage,
                SpDefenseStage = side.SpDefenseStage,
                SpeedStage = side.SpeedStage,
                Status = ParseStatus(side.Status),
                SleepTurns = ParseStatus(side.Status) == StatusCondition.Sleep ? 2 : 0,
                AbilityId = Nothing(side.Ability) ? null : side.Ability,
                HeldItemId = Nothing(side.Item) ? null : side.Item,
                Happiness = Math.Clamp(side.Happiness, 0, 255),
                TimesAttacked = Math.Max(0, side.TimesAttacked),
                ConsecutiveMoveUses = Math.Max(1, side.ConsecutiveUses),
                ConsecutiveMoveName = move.Name,
                MoveFailedLastTurn = side.LastMoveFailed,
                DefenseCurled = side.DefenseCurled,
                ChargeActive = side.Charged,
                LastPhysicalDamageTaken = side.HurtThisTurn ? 1 : 0,
                Moves = new List<MoveState> { move },
            };
        }

        static bool Nothing(string? value) =>
            string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "None", StringComparison.OrdinalIgnoreCase);

        static WeatherType ParseWeather(string? weather) => weather switch
        {
            "Sun" => WeatherType.Sun,
            "Rain" => WeatherType.Rain,
            "Sandstorm" => WeatherType.Sandstorm,
            "Hail (Snow)" => WeatherType.Hail,
            _ => WeatherType.None,
        };

        static TerrainType ParseTerrain(string? terrain) => terrain switch
        {
            "Electric" => TerrainType.Electric,
            "Grassy" => TerrainType.Grassy,
            "Misty" => TerrainType.Misty,
            "Psychic" => TerrainType.Psychic,
            _ => TerrainType.None,
        };

        static StatusCondition ParseStatus(string? status) => status switch
        {
            "Burned" => StatusCondition.Burn,
            "Poisoned" => StatusCondition.Poison,
            "Badly Poisoned" => StatusCondition.Toxic,
            "Paralyzed" => StatusCondition.Paralysis,
            "Asleep" => StatusCondition.Sleep,
            "Frozen" => StatusCondition.Freeze,
            _ => StatusCondition.None,
        };

        // ------------------------------------------------------------------

        static string PowerNote(string move, int listed, int now) =>
            now > listed
                ? $"{move} is {now} power here, not its listed {listed}."
                : $"{move} is only {now} power here, against its listed {listed}.";

        static string FixedNote(string move) =>
            string.Equals(move, "Psywave", StringComparison.OrdinalIgnoreCase)
                ? "Psywave's damage is random - this is one roll of it, not a range."
                : $"{move} deals a set amount, so there is no damage roll.";
    }
}
