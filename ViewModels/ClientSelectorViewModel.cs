using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;
using Foot_Tracker.Tracking.Capture;

namespace Foot_Tracker.ViewModels;

public sealed partial class ClientSelectorViewModel : ViewModelBase
{
    private readonly IWindowCaptureService _captureService = WindowCaptureServiceFactory.Instance;
    private List<ClientWindowInfo> _availableClients = new();

    [ObservableProperty] private string client1Text = "Client 1 - Not Found";
    [ObservableProperty] private bool client1Enabled;
    [ObservableProperty] private bool client1Checked;

    [ObservableProperty] private string client2Text = "Client 2 - Not Found";
    [ObservableProperty] private bool client2Enabled;
    [ObservableProperty] private bool client2Checked;

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
        if (!_captureService.IsAvailable)
        {
            StatusMessage = _captureService.LastError;
            return;
        }

        _availableClients = _captureService.FindClientWindows("PROClient").ToList();

        Client1Enabled = _availableClients.Count >= 1;
        Client2Enabled = _availableClients.Count >= 2;

        Client1Text = _availableClients.Count >= 1
            ? $"Client 1 - PID {_availableClients[0].ProcessId}"
            : "Client 1 - Not Found";

        Client2Text = _availableClients.Count >= 2
            ? $"Client 2 - PID {_availableClients[1].ProcessId}"
            : "Client 2 - Not Found";

        if (_availableClients.Count == 0 && !string.IsNullOrWhiteSpace(_captureService.LastError))
        {
            StatusMessage = _captureService.LastError;
        }
    }

    [RelayCommand]
    private void Confirm()
    {
        ClientWindowInfo? selectedClient = null;
        int clientNumber = 0;

        if (Client1Checked && _availableClients.Count >= 1)
        {
            selectedClient = _availableClients[0];
            clientNumber = 1;
        }
        else if (Client2Checked && _availableClients.Count >= 2)
        {
            selectedClient = _availableClients[1];
            clientNumber = 2;
        }

        if (selectedClient is null)
        {
            StatusMessage = "Please select a PRO client.";
            return;
        }

        _captureService.SelectWindow(selectedClient.Handle);
        SelectedClientNumber = clientNumber;

        Confirmed?.Invoke();
    }
}
