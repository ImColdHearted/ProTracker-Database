using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PokemonSim.Evaluation;
using PokemonSim.Observation;
using PokemonSim.Simulation;
using PokemonSim.Shadow;
using Serilog;

namespace Foot_Tracker.Services.Simulator;

/// <summary>
/// §325. The app's side of §324's harness: it resolves the things the
/// harness refuses to know about - where a model file lives, which species
/// catalog to build teams from, where results go - and turns a MatchReport
/// into something a person reads.
///
/// The split is deliberate and it is the §156 seam again. PokemonSim may
/// not reference the onnx runtime, so WinRateHarness takes an evaluator
/// FACTORY; this is the only place that factory is a real
/// OnnxShadowEvaluator. Everything about measuring - the pairing, the seat
/// swap, the scoring, the interval - stays in the simulator where the tests
/// can reach it without an app.
/// </summary>
public static class EvaluationRunner
{
    /// <summary>Few enough that a Monte Carlo match finishes in an evening;
    /// §324's own arithmetic says 384 battles resolve a 5-point difference,
    /// which is the number this floor is chosen around.</summary>
    public const int MinBattles = 20;

    public const int MaxBattles = 20000;

    /// <summary>Where a saved result lands. Beside the observation corpus,
    /// because that is where this machine's AI data already lives and a
    /// second convention would be one more thing to explain.</summary>
    public static string ResultsFolder =>
        Path.Combine(ObservationStore.Root, "evaluation-results");

    /// <summary>
    /// §326. A brain the panel can put in EITHER seat.
    ///
    /// §325 offered this only as a list of opponents, with the installed
    /// model permanently in seat one. That was a panel restriction and
    /// never a harness one, and the first evening of real results showed
    /// what it cost: the model lost to the baseline 33.3% and to the brain
    /// 6.0%, and the obvious next question - what does the BRAIN score
    /// against the baseline, so how much of that gap is the model and how
    /// much is the measurement - could not be asked at all.
    /// </summary>
    public enum BrainChoice
    {
        InstalledModel,
        MonteCarlo,
        Baseline,
        AnotherModel
    }

    public static string Describe(BrainChoice choice) => choice switch
    {
        BrainChoice.MonteCarlo => "the Monte Carlo brain - the strongest thing here, and the slowest",
        BrainChoice.Baseline => "the baseline AI - random legal moves, the floor a model must clear",
        BrainChoice.AnotherModel => "another model file - the comparison self-play will need",
        _ => "the installed model"
    };

    /// <summary>
    /// One side. <paramref name="seatLabel"/> only separates two contenders
    /// that would otherwise be identical, which is what a mirror is - and a
    /// mirror is still detected on the CONFIGURATION, so a different label
    /// does not hide one.
    /// </summary>
    public static Contender BuildSide(
        BrainChoice choice, string? modelPath, float risk, string seatLabel)
    {
        switch (choice)
        {
            case BrainChoice.MonteCarlo:
                return Contender.MonteCarlo(risk);

            case BrainChoice.Baseline:
                return Contender.Baseline("Baseline AI" + seatLabel);

            case BrainChoice.AnotherModel:
                return Contender.Model(modelPath, null, risk);

            default:
                return Contender.Model(
                    ObservationStore.ResolveModelPath(), "Installed model" + seatLabel, risk);
        }
    }

    /// <summary>A file each, so "v12 vs v11" is a thing the panel can
    /// actually express.</summary>
    public static MatchConfiguration BuildMatch(
        BrainChoice one,
        BrainChoice two,
        int battles,
        int? seed,
        string? oneModelPath,
        string? twoModelPath,
        float risk,
        TierFilter? tiers = null)
    {
        return new MatchConfiguration
        {
            One = BuildSide(one, oneModelPath, risk, ""),
            Two = BuildSide(two, twoModelPath, risk, " (seat two)"),
            Battles = Math.Clamp(battles, MinBattles, MaxBattles),
            Seed = seed,
            SwapSeats = true,
            Tiers = tiers ?? TierFilter.Standard
        };
    }

    /// <summary>§326. The names the panel shows, in the order its lists use
    /// them - so the index a combo box reports and the brain it means
    /// cannot drift apart.</summary>
    public static readonly string[] BrainNames =
    {
        "Installed model",
        "Monte Carlo brain",
        "Baseline AI",
        "Another model file"
    };

    public static BrainChoice ChoiceAt(int index) =>
        index >= 0 && index < BrainNames.Length
            ? (BrainChoice)index
            : BrainChoice.InstalledModel;

    public static bool NeedsFile(int index) => ChoiceAt(index) == BrainChoice.AnotherModel;

    /// <summary>The evaluator factory §324 asks for. The only place in the
    /// evaluation path that knows onnx exists.</summary>
    static IShadowEvaluator Build(string? modelPath) =>
        new OnnxShadowEvaluator(modelPath);

    public static string? Refuse(MatchConfiguration match)
    {
        // One evaluator per side, built and thrown away purely to answer the
        // question - a few milliseconds, and it is the difference between a
        // refusal and a whole run of random play reported as a model.
        var built = new List<IShadowEvaluator>();

        try
        {
            return WinRateHarness.Refuse(match, side =>
            {
                IShadowEvaluator evaluator = Build(side.ModelPath);
                built.Add(evaluator);
                return evaluator;
            });
        }
        finally
        {
            foreach (IShadowEvaluator evaluator in built)
                (evaluator as IDisposable)?.Dispose();
        }
    }

    public static Task<MatchReport> RunAsync(
        MatchConfiguration match,
        Action<int, int>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => WinRateHarness.Run(
                match,
                new TrackerSpeciesSource(),
                Build,
                cancellationToken,
                progress),
            CancellationToken.None);
    }

    /// <summary>
    /// §325. One contender against several opponents in turn.
    ///
    /// It exists because a single number is not a measurement of a model.
    /// "Beats the baseline 91%, loses to Monte Carlo 43%, beats v11 57%,
    /// and is level with itself" is a description; any one of those alone
    /// can be read almost any way you like.
    /// </summary>
    public static async Task<List<MatchReport>> BenchmarkAsync(
        IReadOnlyList<MatchConfiguration> matches,
        Action<int, int, string>? progress,
        CancellationToken cancellationToken)
    {
        var reports = new List<MatchReport>();

        for (int i = 0; i < matches.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            MatchConfiguration match = matches[i];

            progress?.Invoke(i, matches.Count, match.Two.Name);

            reports.Add(await RunAsync(match, null, cancellationToken).ConfigureAwait(false));
        }

        return reports;
    }

    // ---- reading it back -------------------------------------------

    static string Percent(double fraction) =>
        (fraction * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// §325. The result in words.
    ///
    /// A mirror is labelled before anything else on the line, because the
    /// one way this whole system gets misused is somebody quoting a mirror's
    /// 50% as a strength number.
    /// </summary>
    public static string Summarise(MatchReport report)
    {
        var text = new StringBuilder();

        if (report.IsMirror)
        {
            (double mirrorLow, double mirrorHigh) = report.Interval;

            text.Append("HARNESS SANITY CHECK - both sides are the same brain, so this says ");
            text.Append("nothing about strength. ");
            text.Append(report.Battles);
            text.Append(" battles: ");
            text.Append(report.One.Wins);
            text.Append(" - ");
            text.Append(report.Two.Wins);
            text.Append(report.Draws > 0 ? $" - {report.Draws} draw(s)" : "");
            text.Append(", so ");
            text.Append(Percent(report.One.WinRate));
            text.Append(" (95% ");
            text.Append(Percent(mirrorLow));
            text.Append(" - ");
            text.Append(Percent(mirrorHigh));
            text.Append("). ");

            // §328: the one number in this system that must come out even.
            // Before §328 a mirror could land five points off and the report
            // would say only that the SEATS were level - which they were,
            // because the brain was beating itself in both of them.
            // §331: the estimate stopped being the test.
            //
            // §330 made both halves of a pairing the same battle with the
            // labels swapped, so identical brains split every pair and the
            // win rate is 50.0% by CONSTRUCTION. Four mirrors in a row said
            // exactly that, which proved the pairing symmetric and proved
            // nothing else. What can still fail is the SPREAD: a pairing
            // that did not split means the two halves diverged.
            if (report.MirrorIsSkewed)
            {
                text.Append("BUT ");
                text.Append(report.UnsplitPairs);
                text.Append(" of ");
                text.Append(report.PairScores.Count);
                text.Append(" pairings did NOT split one-all, and with the same brain on both ");
                text.Append("seats every one of them must. Both halves of a pairing are the ");
                text.Append("same battle with the two players' labels swapped, so each side has ");
                text.Append("to win exactly one. A pairing that went 2-0 means the halves ");
                text.Append("diverged - something reached one of them and not the other. ");
                text.Append("Nothing else measured here can be trusted until it is explained; ");
                text.Append("put this seed back in to replay it. ");
            }
            else if (report.IntervalIsPaired && report.BothSidesDeterministic)
            {
                text.Append("All ");
                text.Append(report.PairScores.Count);
                text.Append(" pairings split one-all, which is what identical brains must do ");
                text.Append("and is the real check here - the 50.0% above follows from it ");
                text.Append("rather than testing anything. ");
            }
            else if (report.IntervalIsPaired)
            {
                // §332: this brain stops searching on a wall clock, so its
                // two halves are free to diverge and the split test does not
                // apply. Said out loud, because a reader who has seen the
                // strict version needs to know why this one is different -
                // and because the unsplit count is still worth seeing.
                text.Append(report.UnsplitPairs);
                text.Append(" of ");
                text.Append(report.PairScores.Count);
                text.Append(" pairings did not split one-all, which here is expected rather ");
                text.Append("than wrong: this brain stops searching on a clock, so how far it ");
                text.Append("gets depends on how busy the machine was and its two halves are ");
                text.Append("free to differ. The strict check is only applied to brains that ");
                text.Append("answer the same way twice, so this mirror is judged on the ");
                text.Append("interval instead. ");
            }
            else
            {
                text.Append("The interval contains 50%, which is the answer a healthy harness ");
                text.Append("gives. ");
            }

            // §331: with the win rate fixed by construction, this is the one
            // number in a mirror that still measures something - and it
            // measures it cleanly, because both halves are the same battle.
            text.Append("Seat one won ");
            text.Append(Percent(report.SeatOneWinShare));
            text.Append(" of the pairings, which is the only thing a mirror can still tell you: ");
            text.Append("near 50% means this engine does not favour moving first. Teams from ");
            text.Append(report.Tiers);
            text.Append(".");

            AppendSeed(text, report);
            AppendBehaviour(text, report);

            return AppendHealth(text, report).ToString();
        }

        (double low, double high) = report.Interval;

        text.Append(report.One.Name);
        text.Append(" vs ");
        text.Append(report.Two.Name);
        text.Append(": ");
        text.Append(report.One.Wins);
        text.Append(" - ");
        text.Append(report.Two.Wins);
        text.Append(report.Draws > 0 ? $" - {report.Draws} draw(s)" : "");
        text.Append(" over ");
        text.Append(report.Battles);
        text.Append(" battles. Win rate ");
        text.Append(Percent(report.One.WinRate));
        text.Append(" (95% ");
        text.Append(Percent(low));
        text.Append(" - ");
        text.Append(Percent(high));
        text.Append(report.IntervalIsPaired
            ? ", over " + report.PairScores.Count + " team pairings). "
            : ", over battles - this run was not paired). ");

        // The sentence that stops a coin flip being read as a result.
        if (low <= 0.5 && 0.5 <= high)
        {
            int needed = MatchReport.BattlesToResolve(
                Math.Max(1.0, Math.Abs(report.LeadPoints)));

            text.Append("THAT INTERVAL CONTAINS 50%, so this run does not show either side is ");
            text.Append("stronger. About ");
            text.Append(needed);
            text.Append(" battles would settle a difference this size. ");
        }
        else if (report.One.WinRate > 0.5)
        {
            text.Append(report.One.Name);
            text.Append(" is ahead by ");
            text.Append(report.LeadPoints.ToString("0.0", CultureInfo.InvariantCulture));
            text.Append(" points. ");
        }
        else
        {
            text.Append(report.Two.Name);
            text.Append(" is ahead by ");
            text.Append((-report.LeadPoints).ToString("0.0", CultureInfo.InvariantCulture));
            text.Append(" points. ");
        }

        text.Append("Average ");
        text.Append(report.AverageTurns.ToString("0.0", CultureInfo.InvariantCulture));
        text.Append(" turns, winner keeps ");
        text.Append(report.AverageSurvivorsForWinner.ToString("0.0", CultureInfo.InvariantCulture));
        text.Append(" Pokemon. Seat advantage ");
        text.Append(report.One.SeatAdvantagePoints.ToString("0.0", CultureInfo.InvariantCulture));
        text.Append(" points (seat one ");
        text.Append(Percent(report.One.AsPlayerOneRate));
        text.Append(", seat two ");
        text.Append(Percent(report.One.AsPlayerTwoRate));
        text.Append("). Teams from ");
        text.Append(report.Tiers);
        text.Append(".");

        AppendSeed(text, report);

        AppendBehaviour(text, report);

        return AppendHealth(text, report).ToString();
    }

    /// <summary>
    /// §329. The seed, and what it is FOR.
    ///
    /// §324 put the seed on every report and §325 explained it in a
    /// paragraph beside the battle box. Neither was enough: the first
    /// question anyone asked after four runs was what the seeds even were,
    /// having already run four different ones without noticing - because
    /// the box was blank, which is what draws a fresh one.
    ///
    /// A number printed with no verb is a number nobody uses. This says
    /// what to do with it, and it says it at the end of the result rather
    /// than in help text above the button, because that is where a reader
    /// is when the result is the surprising one.
    /// </summary>
    static void AppendSeed(StringBuilder text, MatchReport report)
    {
        text.Append(" Seed ");
        text.Append(report.Seed);
        text.Append(" - leave the Seed box blank for a fresh sample, or put this number in it to ");

        // §332: §329 promised an exact replay. That is true of a match
        // between brains that answer the same way twice, and false of any
        // match involving the Monte Carlo brain, which stops searching on a
        // clock. A promise that is only sometimes true is worse than the
        // smaller one that always is.
        if (report.BothSidesDeterministic)
        {
            text.Append("replay these exact ");
            text.Append(report.Battles);
            text.Append(" battles.");
        }
        else
        {
            text.Append("draw the same ");
            text.Append(report.Battles);
            text.Append(" teams again - though not the same battles, because the Monte Carlo ");
            text.Append("brain searches on a clock and plays a little differently each time.");
        }
    }

    /// <summary>
    /// §326. What each side DID, not just how it fared.
    ///
    /// One number, chosen because it is the one that explains a model
    /// losing to random. §154's baseline never volunteers a switch, so
    /// every switch the other side makes is a turn it spends attacking and
    /// you do not. If a model is switching a third of its turns away
    /// against an opponent that switches none, the win rate above is not a
    /// mystery and no amount of more training will fix it.
    /// </summary>
    static void AppendBehaviour(StringBuilder text, MatchReport report)
    {
        text.Append(" Switched on ");
        text.Append(Percent(report.One.SwitchShare));
        text.Append(" of turns vs ");
        text.Append(Percent(report.Two.SwitchShare));
        text.Append(" (");
        text.Append(report.One.Replacements);
        text.Append(" and ");
        text.Append(report.Two.Replacements);
        text.Append(" forced replacements).");

        AppendChoices(text, report);
    }

    /// <summary>
    /// §335. What each side did with the turns it spent attacking.
    ///
    /// Written as a comparison rather than as a list of figures, because
    /// every one of these numbers is meaningless on its own. "9% of its
    /// attacks could not touch the target" is alarming or unremarkable
    /// entirely according to what the other brain scored on the same 400
    /// battles with the same teams, and the pairing means it really is the
    /// same teams.
    ///
    /// Silent when neither side attacked - a match that was cancelled before
    /// it started has nothing to say here and should not print zeros that
    /// read like findings.
    /// </summary>
    static void AppendChoices(StringBuilder text, MatchReport report)
    {
        if (report.One.DamagingChoices == 0 && report.Two.DamagingChoices == 0)
            return;

        text.Append(" Of the attacks each side chose, ");
        text.Append(Percent(report.One.NoEffectShare));
        text.Append(" vs ");
        text.Append(Percent(report.Two.NoEffectShare));
        text.Append(" could not touch the target by type chart at all, and ");
        text.Append(Percent(report.One.WeakerShare));
        text.Append(" vs ");
        text.Append(Percent(report.Two.WeakerShare));
        text.Append(" had a more effective move available and did not take it ");
        text.Append("(type chart only - abilities that grant an immunity are not counted, ");
        text.Append("so both figures understate). Favourite move slot took ");
        text.Append(Percent(report.One.TopSlotShare));
        text.Append(" vs ");
        text.Append(Percent(report.Two.TopSlotShare));
        text.Append(" of picks, where 25% is an even spread over four moves: ");
        AppendSlots(text, report.One);
        text.Append(" vs ");
        AppendSlots(text, report.Two);
        text.Append('.');

        // §337: the share of moves that do no damage at all.
        //
        // §335 shipped without this and the first two results needed it
        // immediately. The model's favourite slot is slot four on one
        // matchup and slot one on the other, and in competitive sets slot
        // four is very often the status move - so "it picks slot four 45% of
        // the time" is either a brain spamming Swords Dance or a brain
        // spamming an attack, and the slot number alone cannot tell you
        // which. The figure was already computed on SideResult; only the
        // printing was missing.
        text.Append(" Status moves were ");
        text.Append(Percent(report.One.StatusShare));
        text.Append(" vs ");
        text.Append(Percent(report.Two.StatusShare));
        text.Append(" of the moves chosen");

        // §341: and how many of those did nothing. The status share on its
        // own says how often a brain declined to attack; it cannot say
        // whether declining was a plan. This can, for the cases where the
        // answer is certain.
        if (report.One.StatusChoices > 0 || report.Two.StatusChoices > 0)
        {
            text.Append(", of which ");
            text.Append(Percent(report.One.WastedStatusShare));
            text.Append(" vs ");
            text.Append(Percent(report.Two.WastedStatusShare));
            text.Append(" had nothing left to change - the boost was already at +6, ");
            text.Append("the target already had a status, or the weather was already set");
        }

        text.Append('.');
    }

    static void AppendSlots(StringBuilder text, SideResult side)
    {
        for (int slot = 0; slot < side.SlotChoices.Count; slot++)
        {
            if (slot > 0)
                text.Append('/');

            text.Append(side.SlotChoices[slot]);
        }
    }

    /// <summary>
    /// §325. §322's question, asked of a measurement.
    ///
    /// A model that answered nothing makes every number above it a
    /// description of random play, so this is appended to the summary itself
    /// rather than logged where nobody would look for it.
    /// </summary>
    static StringBuilder AppendHealth(StringBuilder text, MatchReport report)
    {
        foreach (SideResult side in new[] { report.One, report.Two })
        {
            if (side.Decisions == 0)
                continue;

            if (side.OwnChoices == 0)
            {
                text.Append(" THIS RESULT IS NOT ABOUT ");
                text.Append(side.Name.ToUpperInvariant());
                text.Append(": it answered none of its ");
                text.Append(side.Decisions);
                text.Append(" decisions, so it played random moves throughout ");
                text.Append("(MIGRATION_GUIDE.md section 322).");
            }
            else if (side.AnsweredShare < 0.5)
            {
                text.Append(' ');
                text.Append(side.Name);
                text.Append(" answered only ");
                text.Append(Percent(side.AnsweredShare));
                text.Append(" of its decisions; the rest were random, so read this result carefully.");
            }
        }

        return text;
    }

    /// <summary>§325. One line per matchup, for reading a whole benchmark at
    /// a glance.</summary>
    public static string SummariseSuite(string contender, IReadOnlyList<MatchReport> reports)
    {
        var text = new StringBuilder();

        text.AppendLine("MODEL EVALUATION: " + contender);

        foreach (MatchReport report in reports)
        {
            text.AppendLine();
            text.Append("  vs ");
            text.Append(report.Two.Name);
            text.Append("  ");

            if (report.IsMirror)
            {
                text.Append("seat difference ");
                text.Append(Math.Abs(report.One.SeatAdvantagePoints)
                    .ToString("0.0", CultureInfo.InvariantCulture));
                text.Append(" points (sanity check, not strength)");
            }
            else
            {
                (double low, double high) = report.Interval;

                text.Append("win rate ");
                text.Append(Percent(report.One.WinRate));
                text.Append(" (");
                text.Append(Percent(low));
                text.Append(" - ");
                text.Append(Percent(high));
                text.Append(")");

                if (low <= 0.5 && 0.5 <= high)
                    text.Append(" - inconclusive");
            }
        }

        return text.ToString();
    }

    // ---- keeping it ------------------------------------------------

    /// <summary>
    /// §325. The result as JSON, so model progression can be graphed later
    /// without anyone having to re-run history.
    ///
    /// Written per match and named for the matchup and the time, because the
    /// point of keeping these is the SEQUENCE - one file that gets
    /// overwritten would answer "how is it doing" and never "is it getting
    /// better", which is the only question that matters.
    /// </summary>
    public static string? Save(MatchReport report)
    {
        try
        {
            Directory.CreateDirectory(ResultsFolder);

            string name = Safe(report.One.Name) + "_vs_" + Safe(report.Two.Name)
                + "_" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json";

            string path = Path.Combine(ResultsFolder, name);

            File.WriteAllText(path, ToJson(report));

            return path;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Evaluation: saving the result failed.");
            return null;
        }
    }

    public static string ToJson(MatchReport report)
    {
        (double low, double high) = report.Interval;

        var payload = new Dictionary<string, object?>
        {
            ["recordedUtc"] = DateTime.UtcNow.ToString("O"),
            ["seed"] = report.Seed,
            ["battles"] = report.Battles,
            ["draws"] = report.Draws,
            ["cancelled"] = report.Cancelled,
            ["seatsSwapped"] = report.SeatsSwapped,
            ["isMirror"] = report.IsMirror,
            ["averageTurns"] = report.AverageTurns,
            ["averageSurvivorsForWinner"] = report.AverageSurvivorsForWinner,
            ["elapsedSeconds"] = report.Elapsed.TotalSeconds,
            ["leadPoints"] = report.LeadPoints,
            ["winRateLow"] = low,
            ["winRateHigh"] = high,
            ["intervalOverPairs"] = report.IntervalIsPaired,
            ["pairs"] = report.PairScores.Count,
            ["unsplitPairs"] = report.UnsplitPairs,
            ["bothSidesDeterministic"] = report.BothSidesDeterministic,
            ["seatOneWinShare"] = report.SeatOneWinShare,
            ["observationFeatures"] = ObservationSchema.FeatureCount,
            ["schema"] = ObservationSchema.Version,
            ["tiers"] = report.Tiers,
            ["mechanics"] = ObservationSchema.MechanicsVersion,
            ["one"] = SideJson(report.One, report.OneModel),
            ["two"] = SideJson(report.Two, report.TwoModel)
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true
    };

    static Dictionary<string, object?> SideJson(SideResult side, string model) =>
        new Dictionary<string, object?>
        {
            ["name"] = side.Name,
            ["model"] = model,
            ["wins"] = side.Wins,
            ["losses"] = side.Losses,
            ["draws"] = side.Draws,
            ["score"] = side.Score,
            ["winRate"] = side.WinRate,
            ["asPlayerOneWins"] = side.AsPlayerOneWins,
            ["asPlayerOneBattles"] = side.AsPlayerOneBattles,
            ["asPlayerTwoWins"] = side.AsPlayerTwoWins,
            ["asPlayerTwoBattles"] = side.AsPlayerTwoBattles,
            ["seatAdvantagePoints"] = side.SeatAdvantagePoints,
            ["decisions"] = side.Decisions,
            ["ownChoices"] = side.OwnChoices,
            ["answeredShare"] = side.AnsweredShare
        };

    static string Safe(string name)
    {
        var clean = new StringBuilder();

        foreach (char c in name)
            clean.Append(char.IsLetterOrDigit(c) ? c : '-');

        return clean.ToString().Trim('-');
    }
}
