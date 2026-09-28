using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Observation;
using PokemonSim.Simulation;

namespace Foot_Tracker.Services.Simulator;

/// <summary>§171. Who plays whom in a lab run. ObserveOnly is the §168
/// behaviour - the trained network watches the other two AIs and never
/// chooses anything. The other two put the network in the driver's seat
/// against one named opponent.</summary>
public enum LabMatchup
{
    ObserveOnly,
    DeepLearningVsMonteCarlo,
    DeepLearningVsBaseline,

    /// <summary>§182. DAgger: the model plays every battle against the
    /// baseline while the Monte Carlo brain rides along saying what it
    /// would have done. The only collection mode that records the
    /// positions the MODEL steers into, which is where its mistakes
    /// actually live.</summary>
    DeepLearningTaught
}

/// <summary>What one lab run produced, for the admin console's summary.</summary>
public sealed class LabRunReport
{
    public required SimulationResult Result { get; init; }
    public int ObservationFiles { get; init; }
    public long DecisionRecords { get; init; }

    /// <summary>§180. How many of those decisions carried the acting
    /// strategy's own ranking. Zero means the corpus cannot use the
    /// trainer's --soft-targets, which is worth knowing when the run ends
    /// rather than after the training run that quietly ignores it.</summary>
    public long RankedRecords { get; init; }
    public long DroppedRecords { get; init; }
    public TimeSpan Elapsed { get; init; }

    /// <summary>§171: how much of the run the network really played -
    /// decisions it was asked for, and the subset it actually answered
    /// (the rest fell through to the baseline: Struggle turns, switches,
    /// states it declined to score).</summary>
    public int NeuralDecisions { get; init; }
    public int NeuralModelChoices { get; init; }
}

/// <summary>
/// §168, extended in §171. The admin console's Battle Lab: the §155/§156
/// bulk harness (BattleSimulator) run headlessly over FRESH random teams
/// every battle, with a §156 observer attached to every one of them.
///
/// Observe only (the §168 behaviour, still the default): half the battles
/// put the Monte Carlo brain (the boss AI) on side one, half play the
/// baseline against itself - so both of the Simulator's AIs are observed
/// fighting random opponents, both sides of every decision recorded.
/// §171 adds the other two matchups, where the trained network itself
/// plays side one against a chosen opponent; a run that names the network
/// refuses to start unless a model actually loaded. The
/// observers write the same obs-*.jsonl files, into the same
/// SimulatorObservations folder, as a recorded primary battle - only the
/// Kind says "Lab". No shadow model is attached (thousands of parallel
/// battles would race one ONNX session; the point here is data, not
/// agreement scores), nothing influences any battle, and the run honors
/// cancellation between turns.
/// </summary>
public static class LabBattleRunner
{
    public const int MinBattles = 100;

    /// <summary>§181: raised from 10,000 so one overnight run can cover a
    /// whole sleep. That is a data-volume decision as much as a time one -
    /// a lab battle writes roughly 110 to 140 KB of observations, so the
    /// ceiling is now something like eleven to fourteen gigabytes rather
    /// than one. The panel says so beside the box.
    ///
    /// It is only a ceiling. Section 179's finding still holds for the
    /// MOVE head - past about ten thousand battles it has stopped
    /// learning - and what makes a bigger number worth typing is section
    /// 180: replacements turned the switch head's corpus from 0.2 percent
    /// of decisions into something like six, which puts that head where
    /// the move head was at the start rather than at the end of its
    /// curve.</summary>
    public const int MaxBattles = 100000;

    /// <summary>§171: the sentence the console shows for each matchup.</summary>
    public static string Describe(LabMatchup matchup) => matchup switch
    {
        LabMatchup.DeepLearningVsMonteCarlo => "the deep-learning model against the Monte Carlo brain",
        LabMatchup.DeepLearningVsBaseline => "the deep-learning model against the baseline AI",
        LabMatchup.DeepLearningTaught => "the deep-learning model playing, with the Monte Carlo brain marking every decision",
        _ => "the Monte Carlo brain and the baseline AI, with the model only watching"
    };

    /// <summary>§182: does this matchup put the trained model on the
    /// field? Everything but Observe only does, and everything that does
    /// refuses to start without a model that actually loaded.</summary>
    public static bool NeedsModel(LabMatchup matchup) => matchup != LabMatchup.ObserveOnly;

    /// <summary>§182: does the Monte Carlo brain rank the decisions in
    /// this matchup? Only a corpus where it does can train with
    /// --soft-targets, which is what the panel warns about.</summary>
    public static bool HasTeacher(LabMatchup matchup) =>
        matchup == LabMatchup.ObserveOnly || matchup == LabMatchup.DeepLearningTaught;

    /// <summary>
    /// §182. The risk appetites an Observe only run cycles through, one
    /// per battle. Collecting the whole dial in a single run is what makes
    /// the risk feature worth having: a model trained on all five learns
    /// the mapping from "the number in feature 202" to "how that teacher
    /// plays", and the panel's difficulty setting then selects a
    /// personality at play time rather than a file.
    ///
    /// A DAgger run does NOT cycle. Its student is one shared
    /// NeuralStrategy across parallel battles, so its risk input has to
    /// hold still for the whole run - varying it per battle would be a
    /// data race, and a quiet one.
    /// </summary>
    public static readonly double[] RiskLevels = { 0.0, 0.25, 0.5, 0.75, 1.0 };

    public static double RiskForBattle(int index) =>
        RiskLevels[Math.Abs(index) % RiskLevels.Length];

    /// <summary>Runs the whole lab on worker threads. The progress
    /// callback receives (completed, total) and is already marshalled to
    /// no particular thread - the caller posts to its UI itself.</summary>
    public static async Task<LabRunReport> RunAsync(
        ISpeciesSource speciesSource,
        int battles,
        Action<int, int>? progress,
        CancellationToken cancellationToken,
        LabMatchup matchup = LabMatchup.ObserveOnly,
        IShadowEvaluator? evaluator = null,
        double risk = 0,
        TierFilter? tiers = null)
    {
        battles = Math.Clamp(battles, MinBattles, MaxBattles);

        // §327: the corpus is made of these battles, so the tier pool is
        // as much a property of the training data as the schema is.
        tiers ??= TierFilter.Standard;
        risk = Math.Clamp(risk, 0.0, 1.0);

        // §171: the network can only play when a model actually loaded.
        // Refusing here rather than quietly substituting random moves is
        // the whole point - a run labelled "deep learning" must BE one.
        if (NeedsModel(matchup) && evaluator?.Status.Available != true)
        {
            throw new InvalidOperationException(
                "The deep-learning model is not available, so it cannot play: " +
                (evaluator?.Status.Description ?? "no evaluator was supplied") +
                ". Install a trained model first, or choose Observe only.");
        }

        var started = DateTime.UtcNow;

        // §181: observers used to be collected here and disposed in one
        // pass after the whole run, which meant a hundred thousand battles
        // kept a hundred thousand of them alive - each holding a bounded
        // channel, a parked writer task and a StringBuilder still sitting
        // on the capacity of the last batch it formatted. At ten thousand
        // that was untidy; at a hundred thousand it is the run's memory
        // ceiling. An observer's work is finished the moment its battle
        // is, so it is tallied and disposed then, and only its four
        // numbers outlive it.
        var live = new ConcurrentDictionary<int, BattleObserver>();
        var flushing = new ConcurrentBag<Task>();

        long recorded = 0, dropped = 0, ranked = 0;
        int files = 0;
        int completed = 0;

        void Retire(BattleObserver observer)
        {
            // The counters are already final - they are incremented as
            // records are queued, not as they are written - so reading
            // them costs nothing and does not have to wait for the disk.
            Interlocked.Add(ref recorded, observer.RecordedCount);
            Interlocked.Add(ref dropped, observer.DroppedCount);
            Interlocked.Add(ref ranked, observer.RankedCount);
            Interlocked.Increment(ref files);

            // The flush is started and awaited at the end, never blocked
            // on here. This runs on a Parallel.For worker, which is a pool
            // thread, and the writer's own continuations need pool threads
            // too: blocking every worker on a flush would leave the pool
            // to rescue itself one injected thread at a time, which over a
            // hundred thousand battles is the difference between a night
            // and a week. Disposal still releases the observer's channel
            // and its writer promptly, which is the point.
            flushing.Add(observer.DisposeAsync().AsTask());
        }

        // One instance for the whole run: it holds no per-battle decision
        // state, only counters, so sharing it is safe and gives an honest
        // total of how often the network really answered.
        NeuralStrategy? neural = NeedsModel(matchup)
            // §182: one setting for the whole run - see RiskLevels for why
            // a DAgger run cannot cycle it the way Observe only does.
            ? new NeuralStrategy(evaluator!) { RiskAppetite = (float)risk }
            : null;

        SimulationResult result = await Task.Run(() =>
        {
            // Warm every catalog the workers will read concurrently.
            MoveDex.EnsureLoaded();
            _ = speciesSource.AllSpeciesNames.Count;

            int baseSeed = Environment.TickCount & 0x7fffffff;

            // §182: a brain built for one battle, at the risk appetite
            // that battle is being collected at. Observe only walks the
            // whole dial so one run covers every difficulty; every other
            // matchup holds still at the run's setting.
            MonteCarloStrategy Brain(int i) => new MonteCarloStrategy(
                new MonteCarloConfig
                {
                    RiskAppetite = matchup == LabMatchup.ObserveOnly ? RiskForBattle(i) : risk
                },
                seed: baseSeed ^ (0x51ED + i))
            {
                CancellationToken = cancellationToken
            };

            return BattleSimulator.Run(
                stateFactory: i => BuildLabBattle(speciesSource, baseSeed, i, matchup, tiers),
                simulations: battles,
                baseSeed: baseSeed,
                cancellationToken: cancellationToken,
                // §171: side one is the network when one was chosen, else
                // the §168 alternation of Monte Carlo and the baseline.
                // §182 adds the taught case, where it is both: the network
                // plays and the brain marks its work.
                player1Strategy: matchup == LabMatchup.DeepLearningTaught
                    ? i => new TeachingStrategy(neural!, Brain(i))
                    : neural != null
                        ? _ => neural
                        : i => i % 2 == 0
                            ? Brain(i)
                            : new RandomMoveStrategy(),
                // Side two: the chosen sparring partner, else the baseline.
                player2Strategy: matchup == LabMatchup.DeepLearningVsMonteCarlo
                    ? i => Brain(i)
                    : null,
                observerFactory: i =>
                {
                    var observer = new BattleObserver(ObservationStore.Root, "Lab", baseSeed + i);
                    live[i] = observer;
                    return observer;
                },
                onBattleCompleted: (i, _) =>
                {
                    if (live.TryRemove(i, out BattleObserver? observer))
                        Retire(observer);

                    progress?.Invoke(Interlocked.Increment(ref completed), battles);
                });
        }, CancellationToken.None).ConfigureAwait(false);

        // Anything still here belongs to a battle that threw before its
        // completion callback ran. Rare, but it must not leak a file.
        foreach (int index in live.Keys.ToList())
        {
            if (live.TryRemove(index, out BattleObserver? observer))
                Retire(observer);
        }

        // Every battle's records are on disk before the report is handed
        // back, so the count the panel reads next is the whole run.
        await Task.WhenAll(flushing).ConfigureAwait(false);

        return new LabRunReport
        {
            Result = result,
            ObservationFiles = files,
            DecisionRecords = recorded,
            RankedRecords = ranked,
            DroppedRecords = dropped,
            Elapsed = DateTime.UtcNow - started,
            NeuralDecisions = neural?.DecisionCount ?? 0,
            NeuralModelChoices = neural?.ModelChoiceCount ?? 0
        };
    }

    /// <summary>One lab battle: two fresh random teams (a new pair every
    /// battle), named for who is playing which side. A rare unbuildable
    /// team re-rolls with a shifted seed rather than sinking the run.</summary>
    static BattleState BuildLabBattle(
        ISpeciesSource speciesSource, int baseSeed, int index, LabMatchup matchup, TierFilter tiers)
    {
        var teamA = BuildTeam(speciesSource, baseSeed ^ (index * 2 + 1), tiers);
        var teamB = BuildTeam(speciesSource, baseSeed ^ (index * 2 + 2), tiers);

        // The names ride into every observation record, so a corpus can
        // always be read back to see who was actually playing.
        string sideOne = matchup switch
        {
            LabMatchup.ObserveOnly => index % 2 == 0 ? "Monte Carlo AI" : "Baseline AI",
            LabMatchup.DeepLearningTaught => "Deep Learning AI (taught)",
            _ => "Deep Learning AI"
        };

        string sideTwo = matchup == LabMatchup.DeepLearningVsMonteCarlo
            ? "Monte Carlo AI"
            : "Baseline AI";

        return new BattleState
        {
            Player1 = new PlayerState
            {
                Name = sideOne,
                Team = teamA,
                ActivePokemon = teamA[0]
            },
            Player2 = new PlayerState
            {
                Name = sideTwo,
                Team = teamB,
                ActivePokemon = teamB[0]
            },
            Rng = new BattleRng(baseSeed + index)
        };
    }

    static List<PokemonState> BuildTeam(ISpeciesSource speciesSource, int seed, TierFilter tiers)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            TeamBuildResult team = RandomTeams.Build(
                speciesSource, TeamBuilder.MaxTeamSize, seed + attempt * 7919, tiers: tiers);

            if (team.Ok && team.Team.Count > 0)
                return team.Team;
        }

        throw new InvalidOperationException("The lab could not build a random team - is the species catalog loaded?");
    }
}
