using CommunityToolkit.Mvvm.ComponentModel;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// One row in ClientSelectorWindow's list. Unlike TargetDisplayItem/
/// PvpOpponentDisplayItem (formatted once, never changed after), this one is
/// genuinely live: IsChecked is two-way bound to its RadioButton and
/// CustomName to an editable name box, so it needs real observable
/// properties rather than a plain record. See ClientSelectorViewModel
/// (LoadClients builds these, Confirm reads them back) and
/// ClientNamesService (where CustomName is ultimately persisted).
///
/// §105: every slot is selectable now - IsEnabled is gone. A slot is a data
/// PROFILE (its own session, catch logs, PVP log, boss cooldowns, appearance
/// and stat preferences), and picking one with no PRO window running is a
/// legitimate thing to want: it loads that profile and simply has nothing to
/// capture until a window is there. What used to be the enabled/disabled
/// distinction is now purely informational - see StatusText.
/// </summary>
public sealed partial class ClientSlotItem : ViewModelBase
{
    public int ClientNumber { get; }

    /// <summary>Which PRO window (if any) this slot currently maps to -
    /// "Client 2 - PID 18420", or "Client 2 - no PRO window".</summary>
    [ObservableProperty] private string foundText = string.Empty;

    /// <summary>The second, quieter line: whether another still-running
    /// tracker window holds this profile, and whether this is the profile
    /// this window is already on. Empty for the ordinary free-slot case, so
    /// the list stays quiet until there is something worth saying.</summary>
    [ObservableProperty] private string statusText = string.Empty;

    [ObservableProperty] private bool hasStatus;

    /// <summary>True when another live tracker window holds this profile's
    /// lock - drives the confirm-before-taking-over prompt in
    /// ClientSelectorViewModel.Confirm.</summary>
    public bool IsHeldByAnotherWindow { get; set; }

    public int? HeldByProcessId { get; set; }

    [ObservableProperty] private bool isChecked;
    [ObservableProperty] private string customName = string.Empty;

    partial void OnStatusTextChanged(string value) =>
        HasStatus = !string.IsNullOrWhiteSpace(value);

    public ClientSlotItem(int clientNumber)
    {
        ClientNumber = clientNumber;
    }
}
