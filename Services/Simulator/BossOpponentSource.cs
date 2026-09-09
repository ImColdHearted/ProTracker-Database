using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Foot_Tracker.Models;
using PokemonSim.Simulation;

namespace Foot_Tracker.Services.Simulator;

/// <summary>
/// §155. One selectable Simulator opponent: a boss at one difficulty - and,
/// for the dual-boss files (Jessie &amp; James, Medusa &amp; Eldir, the
/// Gamers, Shary &amp; Shaui), one NAMED combatant of it, because those
/// files carry a separate team per combatant and each is its own fight.
/// </summary>
public sealed class SimulatorBossFight
{
    public required string BossId { get; init; }
    public required string BossName { get; init; }
    public string? NpcName { get; init; }
    public required string DifficultyKey { get; init; }
    public required string DifficultyLabel { get; init; }
    public required string Location { get; init; }
    public string Requirement { get; init; } = string.Empty;
    public string PortraitRelativePath { get; init; } = string.Empty;
    public required List<BossPokemonData> Team { get; init; }

    /// <summary>"Jessie (Jessie &amp; James) - Hard" / "Brock - Easy".</summary>
    public string DisplayTitle =>
        NpcName == null
            ? $"{BossName} - {DifficultyLabel}"
            : $"{NpcName} ({BossName}) - {DifficultyLabel}";

    public string TeamSummary =>
        Team.Count == 0 ? "(no team data)" : string.Join(", ", Team.Select(p => p.Name));
}

/// <summary>
/// §155. The Simulator's window into the tracker's existing Boss Database -
/// the same DataFiles/Bosses/*.json files, the same BossData model, the
/// same BossRepository loader BossDetailWindow uses, so there is no second
/// hand-maintained boss list anywhere. Reading is all this class ever does:
/// nothing here can touch Boss Cooldowns, rewards, or any hunting data,
/// and a malformed boss file costs exactly that one file (with its problem
/// recorded for display) rather than the whole list.
/// </summary>
public static class BossOpponentSource
{
    static readonly string[] DifficultyOrder = { "easy", "medium", "hard" };

    /// <summary>Every distinct fight in the Boss Database, in file order
    /// then easy-to-hard, plus the per-file problems that kept anything
    /// out of the list.</summary>
    public static (List<SimulatorBossFight> Fights, List<string> Problems) LoadAll()
    {
        var fights = new List<SimulatorBossFight>();
        var problems = new List<string>();

        string folder = Path.Combine(AppContext.BaseDirectory, "DataFiles", "Bosses");

        if (!Directory.Exists(folder))
        {
            problems.Add("The Boss Database folder (DataFiles/Bosses) was not found.");
            return (fights, problems);
        }

        foreach (string file in Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string bossId = Path.GetFileNameWithoutExtension(file);

            BossData boss;

            try
            {
                boss = BossRepository.Load(bossId);
            }
            catch (Exception ex)
            {
                problems.Add($"{bossId}: could not be read - {FirstLine(ex.Message)}");
                continue;
            }

            string bossName = string.IsNullOrWhiteSpace(boss.Name)
                ? EnumFormatHelper.ToDisplayName(bossId)
                : boss.Name;

            foreach (string difficultyKey in DifficultyOrder)
            {
                if (!boss.Difficulties.TryGetValue(difficultyKey, out BossDifficultyData? difficulty) || difficulty == null)
                    continue;

                if (!string.IsNullOrWhiteSpace(difficulty.UnavailableMessage))
                    continue;   // PRO doesn't have this difficulty for this boss

                foreach ((string? npcName, List<BossPokemonData> team) in difficulty.GetNpcTeams())
                {
                    if (team.Count == 0)
                        continue;

                    fights.Add(new SimulatorBossFight
                    {
                        BossId = bossId,
                        BossName = bossName,
                        NpcName = npcName,
                        DifficultyKey = difficultyKey,
                        DifficultyLabel = char.ToUpperInvariant(difficultyKey[0]) + difficultyKey.Substring(1),
                        Location = boss.Location,
                        Requirement = string.IsNullOrWhiteSpace(boss.Requirements) ? boss.Requirement : boss.Requirements,
                        PortraitRelativePath = PortraitFor(boss, npcName),
                        Team = team
                    });
                }
            }
        }

        if (fights.Count == 0 && problems.Count == 0)
            problems.Add("The Boss Database contains no playable teams.");

        return (fights, problems);
    }

    /// <summary>The engine-side plans for one fight. Levels are absent from
    /// the boss files, so none is set here - OpponentTeams applies its
    /// documented default and says so. §164: every slot carries PRO's
    /// stat standard for the fight's difficulty - Easy is 31 IVs with no
    /// EVs, Medium 31 IVs and 252 EVs per stat, Hard 31 IVs and 400 EVs
    /// per stat (yes, past the games' cap - PRO superbosses really do
    /// that, and the engine's factory accepts it).</summary>
    public static List<OpponentSlotPlan> ToPlans(SimulatorBossFight fight)
    {
        (int[] ivs, int[] evs) = DifficultySpread(fight.DifficultyKey);

        return fight.Team.Select(p => new OpponentSlotPlan
        {
            SpeciesName = p.Name,
            Level = null,
            NatureName = p.Nature,
            AbilityName = p.Ability,
            ItemName = p.Item,
            MoveNames = p.Moves.ToList(),
            Ivs = ivs.ToArray(),
            Evs = evs.ToArray(),
            // §200: the boss files have carried dexNumber on every Pokemon
            // since they were written, and until now it only ever reached
            // the boss card's roster picture. The battle drew whatever the
            // NAME resolved to, which is the wrong form for every boss
            // fielding a regional variant.
            DexNumber = p.DexNumber > 0 ? p.DexNumber : 0
        }).ToList();
    }

    /// <summary>§164. PRO's boss stat standards by difficulty.</summary>
    public static (int[] Ivs, int[] Evs) DifficultySpread(string difficultyKey)
    {
        int ev = difficultyKey.ToLowerInvariant() switch
        {
            "easy" => 0,
            "medium" => 252,
            "hard" => 400,
            _ => 0
        };

        return (
            new[] { 31, 31, 31, 31, 31, 31 },
            new[] { ev, ev, ev, ev, ev, ev });
    }

    /// <summary>A named combatant's own portrait when the file maps one
    /// (§92's npcPictures), else the boss file's NPCPicture.</summary>
    static string PortraitFor(BossData boss, string? npcName)
    {
        if (npcName != null &&
            boss.NpcPictures.TryGetValue(npcName, out string? mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped;
        }

        return boss.NpcPicture;
    }

    static string FirstLine(string text)
    {
        int cut = text.IndexOfAny(new[] { '\r', '\n' });
        return cut < 0 ? text : text.Substring(0, cut);
    }
}
