using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>§276. One battle against this opponent, as the detail window
/// lists it.</summary>
public sealed partial class PvpBattleRow : ObservableObject
{
    public required string When { get; init; }
    public required string Result { get; init; }
    public required bool IsWin { get; init; }
    public required bool IsLoss { get; init; }
    public required IReadOnlyList<string> Log { get; init; }

    public bool IsUnknownResult => !IsWin && !IsLoss;

    /// <summary>Whether PRO printed anything this battle that was captured.
    /// A battle recorded before §276 has nothing, and says so rather than
    /// showing an empty box.</summary>
    public bool HasLog => Log.Count > 0;

    /// <summary>§203's idiom: the row stays in place and says it is the one
    /// being read, rather than the list rearranging under the click.</summary>
    [ObservableProperty] private bool isSelected;
}

/// <summary>
/// §276. Everything this client has recorded about one PVP opponent, opened by
/// clicking their name in Previously Battled Users.
///
/// The list window is a battle LOG - one row per battle, the same name on as
/// many rows as you have fought them. This is the other view of the same data:
/// one opponent, every battle against them, and the head-to-head record.
///
/// WHAT IS HERE AND WHAT IS NOT. The record and the battle list are complete.
/// The opponent's Pokemon, their moves and their items are not yet broken out,
/// and this window does not pretend otherwise: it shows the battle's message
/// lines exactly as PRO printed them, which is where all three of those facts
/// live. Turning those lines into "their team was X, Y, Z" needs PRO's own
/// wording - above all which side a line is about, since the same box narrates
/// both players - and inventing that wording is how §239's predecessor kept
/// breaking. The lines are captured and saved from §276 onward, so the parsing
/// when it lands reads battles already recorded rather than starting from the
/// next one.
/// </summary>
public sealed partial class PvpOpponentDetailViewModel : ViewModelBase
{
    public string OpponentName { get; }

    public string Title => $"{OpponentName} - Battle History";

    public ObservableCollection<PvpBattleRow> Battles { get; } = new();

    /// <summary>"3 wins, 1 loss" - and, when some battles never had their
    /// result read, how many, so the numbers not adding up to the battle count
    /// is explained rather than looking like a bug.</summary>
    [ObservableProperty] private string recordLine = string.Empty;

    /// <summary>The lifetime count, which outlives this log's 250-battle cap -
    /// see PvpOpponentEntry.TimesBattled.</summary>
    [ObservableProperty] private string facedLine = string.Empty;

    [ObservableProperty] private PvpBattleRow? selectedBattle;

    [ObservableProperty] private bool hasSelectedBattle;

    public PvpOpponentDetailViewModel(string opponentName)
    {
        OpponentName = opponentName;

        List<PvpOpponentEntry> mine = PvpOpponentService.Opponents
            .Where(o => string.Equals(o.Name, opponentName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(o => o.BattledAtUtc)
            .ToList();

        foreach (PvpOpponentEntry entry in mine)
        {
            Battles.Add(new PvpBattleRow
            {
                When = entry.BattledAtUtc.ToLocalTime().ToString("g"),
                Result = entry.HasOutcome ? entry.Outcome : "-",
                IsWin = entry.HasOutcome && entry.Won,
                IsLoss = entry.HasOutcome && !entry.Won,
                Log = entry.BattleLog.ToList(),
            });
        }

        int wins = mine.Count(e => e.HasOutcome && e.Won);
        int losses = mine.Count(e => e.HasOutcome && !e.Won);
        int unknown = mine.Count - wins - losses;

        RecordLine = mine.Count == 0
            ? "No battles against this opponent are still in the log."
            : $"{DisplayNumber.Count(wins)} {Plural(wins, "win", "wins")}, "
              + $"{DisplayNumber.Count(losses)} {Plural(losses, "loss", "losses")}"
              + (unknown == 0 ? "." : $" - {DisplayNumber.Count(unknown)} with no result read.");

        // The battle count on the newest entry is the lifetime one as of that
        // battle, which is the highest this log can know; it survives the cap
        // that this list does not.
        int lifetime = mine.Count == 0 ? 0 : mine[0].TimesBattled;

        FacedLine = lifetime <= 0
            ? string.Empty
            : $"Faced {DisplayNumber.Count(lifetime)} {Plural(lifetime, "time", "times")} in total"
              + (lifetime > mine.Count ? $" ({DisplayNumber.Count(mine.Count)} still in the log)." : ".");

        if (Battles.Count > 0)
            SelectBattle(Battles[0]);
    }

    private static string Plural(int count, string one, string many) => count == 1 ? one : many;

    /// <summary>§276. Shows one battle's message lines. Selecting is what this
    /// window's clicking does - there is nothing here to open or change.</summary>
    [RelayCommand]
    private void SelectBattle(PvpBattleRow? row)
    {
        if (row is null)
            return;

        foreach (PvpBattleRow other in Battles)
            other.IsSelected = ReferenceEquals(other, row);

        SelectedBattle = row;
        HasSelectedBattle = true;
    }
}
