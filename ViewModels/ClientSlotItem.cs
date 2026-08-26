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
/// </summary>
public sealed partial class ClientSlotItem : ViewModelBase
{
    public int ClientNumber { get; }

    [ObservableProperty] private string foundText = string.Empty;
    [ObservableProperty] private bool isEnabled;
    [ObservableProperty] private bool isChecked;
    [ObservableProperty] private string customName = string.Empty;

    public ClientSlotItem(int clientNumber)
    {
        ClientNumber = clientNumber;
    }
}
