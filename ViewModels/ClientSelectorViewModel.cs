using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;
using Foot_Tracker.Tracking.Capture;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Backs ClientSelectorWindow - lets the player pick which running PRO
/// client this tracker window should follow, and give each client slot a
/// custom name so more than one is easy to tell apart. Bumped from a
/// hardcoded Client1/Client2 pair to a dynamic list of MaxClients slots -
/// see MIGRATION_GUIDE.md for why that turned out to be a UI-only change:
/// SessionPersistenceService, BossCooldownService, AppearanceRepository,
/// UiPreferencesService, and PvpOpponentService were all already generic
/// over any client number (none of them were actually limited to 2) - the
/// hardcoded pair only ever lived here and in the old two-RadioButton XAML.
/// </summary>
public sealed partial class ClientSelectorViewModel : ViewModelBase
{
    public const int MaxClients = 4;

    private readonly IWindowCaptureService _captureService = WindowCaptureServiceFactory.Instance;
    private List<ClientWindowInfo> _availableClients = new();

    public ObservableCollection<ClientSlotItem> Slots { get; } = new();

    [ObservableProperty] private string? statusMessage;

    public int SelectedClientNumber { get; private set; }

    /// <summary>Raised once a client has been chosen and assigned - the View closes itself.</summary>
    public event Action? Confirmed;

    public ClientSelectorViewModel()
    {
        LoadClients();
    }

    private void LoadClients()
    {
        Slots.Clear();

        if (!_captureService.IsAvailable)
        {
            StatusMessage = _captureService.LastError;
            return;
        }

        _availableClients = _captureService.FindClientWindows("PROClient").ToList();

        for (int clientNumber = 1; clientNumber <= MaxClients; clientNumber++)
        {
            bool found = _availableClients.Count >= clientNumber;

            string foundText = found
                ? $"Client {clientNumber} - PID {_availableClients[clientNumber - 1].ProcessId}"
                : $"Client {clientNumber} - Not Found";

            Slots.Add(new ClientSlotItem(clientNumber)
            {
                IsEnabled = found,
                FoundText = foundText,
                // Pre-fills whatever name was saved last time, even for a
                // slot with no client currently running in it - naming a
                // slot ahead of time (before that PRO instance is even open)
                // is harmless, since names are keyed by number, not by PID.
                CustomName = ClientNamesService.GetName(clientNumber) ?? string.Empty
            });
        }

        if (_availableClients.Count == 0 && !string.IsNullOrWhiteSpace(_captureService.LastError))
        {
            StatusMessage = _captureService.LastError;
        }
    }

    [RelayCommand]
    private void Confirm()
    {
        ClientSlotItem? selected = Slots.FirstOrDefault(s => s.IsChecked && s.IsEnabled);

        if (selected is null)
        {
            StatusMessage = "Please select a PRO client.";
            return;
        }

        // Save every slot's name, not just the one being picked - renaming a
        // client you're not assigning to right now should still stick.
        foreach (ClientSlotItem slot in Slots)
            ClientNamesService.SetName(slot.ClientNumber, slot.CustomName);

        ClientWindowInfo selectedClient = _availableClients[selected.ClientNumber - 1];

        _captureService.SelectWindow(selectedClient.Handle);
        SelectedClientNumber = selected.ClientNumber;

        Confirmed?.Invoke();
    }
}
