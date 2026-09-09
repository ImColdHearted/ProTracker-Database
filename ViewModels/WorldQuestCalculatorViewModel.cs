using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// One catch submitted to the World Quest - either its six IVs (the quest
/// credits the sum of all six) or, for the quick-add path, just the total the
/// player already read off the in-game submission message. Invokes
/// notifyParent (the parent view-model's RefreshSummary) whenever an IV box
/// edit changes this row's total, so the summary below the list stays live
/// without the parent having to subscribe to every row's PropertyChanged.
/// Not a nested class because the XAML DataTemplate needs to name it in
/// x:DataType (compiled bindings) - same reason Services/Announcement and the
/// other row-item types are top-level.
/// </summary>
public sealed partial class WqSubmissionRow : ObservableObject
{
    private readonly Action notifyParent;

    /// <summary>A normal row: six editable IV boxes, total computed here.</summary>
    public WqSubmissionRow(Action notifyParent)
    {
        this.notifyParent = notifyParent;
        IsDirect = false;
        totalDisplay = "Total: 0";
    }

    /// <summary>A quick-add row: the total was entered directly, no IV boxes.</summary>
    public WqSubmissionRow(Action notifyParent, int directTotal)
    {
        this.notifyParent = notifyParent;
        IsDirect = true;
        Total = Math.Clamp(directTotal, 0, 186);
        totalDisplay = $"Total: {Total}";
    }

    /// <summary>True for quick-add rows - the template hides the IV boxes and
    /// shows a small "entered as a total" note instead.</summary>
    public bool IsDirect { get; }

    /// <summary>This submission's contribution (0-186, six IVs of 0-31 each).</summary>
    public int Total { get; private set; }

    [ObservableProperty] private string hpText = "";
    [ObservableProperty] private string attackText = "";
    [ObservableProperty] private string defenseText = "";
    [ObservableProperty] private string spAttackText = "";
    [ObservableProperty] private string spDefenseText = "";
    [ObservableProperty] private string speedText = "";

    [ObservableProperty] private string totalDisplay;

    partial void OnHpTextChanged(string value) => Recompute();
    partial void OnAttackTextChanged(string value) => Recompute();
    partial void OnDefenseTextChanged(string value) => Recompute();
    partial void OnSpAttackTextChanged(string value) => Recompute();
    partial void OnSpDefenseTextChanged(string value) => Recompute();
    partial void OnSpeedTextChanged(string value) => Recompute();

    private static int ParseIv(string? text)
    {
        return int.TryParse((text ?? "").Trim(), out int value)
            ? Math.Clamp(value, 0, 31)
            : 0;
    }

    private void Recompute()
    {
        if (IsDirect)
            return;

        Total = ParseIv(HpText) + ParseIv(AttackText) + ParseIv(DefenseText)
              + ParseIv(SpAttackText) + ParseIv(SpDefenseText) + ParseIv(SpeedText);
        TotalDisplay = $"Total: {Total}";
        notifyParent();
    }
}

/// <summary>
/// The Calculators -> World Quest window: a personal contribution tracker for
/// PRO's World Quest events. How the event works (checked against the PRO
/// wiki's World Quest page): everyone on the server submits caught Pokémon of
/// the featured species, each submission credits the sum of its six IVs
/// toward a community goal (150,000 in recent runs, editable here in case a
/// run changes it), and a player whose own contribution reaches 0.5% of the
/// goal earns a Mysterious Ticket - 3% earns a second one. This window adds
/// up the player's own submissions and shows the distance to each tier, plus
/// a rough how-many-more-catches estimate from their running average. Nothing
/// here persists on purpose - a World Quest lasts a weekend, and the in-game
/// total is the authoritative one; this is a live scratchpad beside it.
/// </summary>
public sealed partial class WorldQuestCalculatorViewModel : ViewModelBase
{
    public ObservableCollection<WqSubmissionRow> Submissions { get; } = new();

    [ObservableProperty] private string goalText = "150000";
    [ObservableProperty] private string quickTotalText = "";

    [ObservableProperty] private string countLine = "";
    [ObservableProperty] private string shareLine = "";
    [ObservableProperty] private string averageLine = "";
    [ObservableProperty] private string ticketOneLine = "";
    [ObservableProperty] private string ticketTwoLine = "";
    [ObservableProperty] private string statusMessage = "";

    public WorldQuestCalculatorViewModel()
    {
        RefreshSummary();
    }

    partial void OnGoalTextChanged(string value) => RefreshSummary();

    [RelayCommand]
    private void AddSubmission()
    {
        Submissions.Add(new WqSubmissionRow(RefreshSummary));
        RefreshSummary();
    }

    [RelayCommand]
    private void AddQuickTotal()
    {
        if (!int.TryParse(QuickTotalText.Trim(), out int total) || total < 0 || total > 186)
        {
            StatusMessage = "Enter the submission's IV total as a number from 0 to 186 (six IVs of up to 31), e.g. 112.";
            return;
        }

        StatusMessage = "";
        Submissions.Add(new WqSubmissionRow(RefreshSummary, total));
        QuickTotalText = "";
        RefreshSummary();
    }

    [RelayCommand]
    private void RemoveSubmission(WqSubmissionRow? row)
    {
        if (row == null)
            return;

        Submissions.Remove(row);
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        if (!int.TryParse(GoalText.Replace(",", "").Trim(), out int goal) || goal <= 0)
        {
            StatusMessage = "Enter the World Quest goal as a plain number, e.g. 150000.";
            CountLine = "";
            ShareLine = "";
            AverageLine = "";
            TicketOneLine = "";
            TicketTwoLine = "";
            return;
        }

        StatusMessage = "";

        int count = Submissions.Count;
        int total = Submissions.Sum(row => row.Total);

        // ceil, not floor: "at least 0.5%" means the first whole IV total AT or
        // above the percentage, so a fractional threshold always rounds up.
        int ticketOneAt = (int)Math.Ceiling(goal * 0.005);
        int ticketTwoAt = (int)Math.Ceiling(goal * 0.03);

        CountLine = count == 1 ? "1 submission" : $"{count} submissions";
        ShareLine = $"Contributed: {total} IVs ({100.0 * total / goal:0.###}% of the {goal:#,0} goal)";
        AverageLine = count > 0
            ? $"Average per submission: {(double)total / count:0.#} IVs"
            : "Average per submission: -";
        TicketOneLine = BuildTierLine("1st Mysterious Ticket (0.5%)", ticketOneAt, total, count);
        TicketTwoLine = BuildTierLine("2nd Mysterious Ticket (3%)", ticketTwoAt, total, count);
    }

    private static string BuildTierLine(string label, int threshold, int total, int count)
    {
        if (total >= threshold)
            return $"{label}: reached! (needed {threshold})";

        int remaining = threshold - total;
        string line = $"{label}: {remaining} more IVs needed (tier starts at {threshold})";

        if (count > 0 && total > 0)
        {
            double average = (double)total / count;
            int catches = (int)Math.Ceiling(remaining / average);
            line += $" - about {catches} more {(catches == 1 ? "catch" : "catches")} at your average";
        }

        return line;
    }
}
