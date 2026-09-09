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

    /// <summary>§143/§153. Wired by RemoveEventWindow to AdminTokenWindow -
    /// see CreateEventViewModel.RequestAdminSignIn.</summary>
    public Func<Task<bool>>? RequestAdminSignIn { get; set; }

    public RemoveEventViewModel()
    {
        RefreshBoard();

        if (EventsSyncService.IsOnline)
            _ = RefreshFromServerAsync();
    }

    [RelayCommand]
    private Task Refresh() => RefreshFromServerAsync();

    /// <summary>§143. Same pull as the Events board's own refresh, so the
    /// delete list is the server's list.</summary>
    private async Task RefreshFromServerAsync()
    {
        if (!EventsSyncService.IsOnline)
        {
            RefreshBoard();
            return;
        }

        try
        {
            GuildEventService.ReplaceAll(await EventsSyncService.FetchEventsAsync());
            RefreshBoard();
        }
        catch (EventsSyncException ex)
        {
            RefreshBoard();
            StatusMessage = $"Could not update the list ({ex.Message}) - showing the last copy saved on this machine.";
        }
    }

    [RelayCommand]
    private async Task DeleteEvent(GuildEventCardItem item)
    {
        bool confirmed = ConfirmAsync is not null
            && await ConfirmAsync($"Delete \"{item.Title}\"? This can't be undone.");

        if (!confirmed)
            return;

        if (EventsSyncService.IsOnline)
        {
            // §143: the server first - a post everyone can see has to go
            // from the place everyone reads it. Only then the local copy.
            if (!await EnsureAdminSignInAsync())
            {
                StatusMessage = "Not deleted - changing the shared board needs an admin sign-in.";
                return;
            }

            bool onServer;

            try
            {
                onServer = await EventsSyncService.DeleteEventAsync(item.Id);
            }
            catch (EventsSyncException ex)
            {
                if (ex.Unauthorized)
                    EventsSyncService.ForgetAdminCredentials();

                StatusMessage = ex.Unauthorized
                    ? "Not deleted - the events server rejected the sign-in. Try again to sign in."
                    : $"Not deleted - {ex.Message}.";
                return;
            }

            GuildEventService.Delete(item.Id);

            StatusMessage = onServer
                ? $"Deleted \"{item.Title}\" from the shared board."
                : $"\"{item.Title}\" was already gone from the shared board - removed from this machine too.";

            RefreshBoard();
            return;
        }

        bool removed = GuildEventService.Delete(item.Id);

        StatusMessage = removed
            ? $"Deleted \"{item.Title}\"."
            : $"\"{item.Title}\" was already removed - maybe from another Remove Event window.";

        RefreshBoard();
    }

    private async Task<bool> EnsureAdminSignInAsync()
    {
        if (EventsSyncService.HasAdminCredentials)
            return true;

        if (RequestAdminSignIn is null)
            return false;

        return await RequestAdminSignIn();
    }

    private void RefreshBoard()
    {
        Board.Clear();

        foreach (GuildEventCardItem item in GuildEventCardItem.LoadAll())
            Board.Add(item);

        HasNoEvents = Board.Count == 0;
    }
}
