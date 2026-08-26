using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Backs RemoveEventWindow - the Team Magma-only "delete a post" list, the
/// other half of AdminActionsWindow alongside CreateEventWindow. Nothing here
/// checks the login itself, same reasoning as CreateEventViewModel: by the
/// time this ViewModel exists, MainWindow.AdminLoginButton_Click has already
/// confirmed it, and AdminActionsWindow is the only thing that ever opens
/// RemoveEventWindow.
///
/// Reuses EventsViewModel's GuildEventCardItem (and its shared LoadAll())
/// for the card list rather than defining its own near-identical type - this
/// window needs to show exactly the same information EventsWindow does, just
/// with a Delete button added, so there was nothing to change about how a
/// card is built.
/// </summary>
public sealed partial class RemoveEventViewModel : ViewModelBase
{
    public ObservableCollection<GuildEventCardItem> Board { get; } = new();

    [ObservableProperty] private bool hasNoEvents;

    [ObservableProperty] private string statusMessage =
        "Pick an event below and press Delete to remove it from the board - this can't be undone.";

    /// <summary>Set by RemoveEventWindow.axaml.cs to show a Yes/No confirm
    /// before actually deleting - same ConfirmDialogWindow.ShowAsync pattern
    /// MainWindowViewModel.ConfirmAsync already uses elsewhere. Required, not
    /// just preferred: DeleteEvent refuses to delete anything if this hook
    /// isn't wired, rather than silently skipping the confirmation.</summary>
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    public RemoveEventViewModel()
    {
        RefreshBoard();
    }

    [RelayCommand]
    private void Refresh() => RefreshBoard();

    [RelayCommand]
    private async Task DeleteEvent(GuildEventCardItem item)
    {
        bool confirmed = ConfirmAsync is not null
            && await ConfirmAsync($"Delete \"{item.Title}\"? This can't be undone.");

        if (!confirmed)
            return;

        bool removed = GuildEventService.Delete(item.Id);

        StatusMessage = removed
            ? $"Deleted \"{item.Title}\"."
            : $"\"{item.Title}\" was already removed - maybe from another Remove Event window.";

        RefreshBoard();
    }

    private void RefreshBoard()
    {
        Board.Clear();

        foreach (GuildEventCardItem item in GuildEventCardItem.LoadAll())
            Board.Add(item);

        HasNoEvents = Board.Count == 0;
    }
}
