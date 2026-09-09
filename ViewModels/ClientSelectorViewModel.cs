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
///
/// Slot-to-window matching is "sticky" via ClientWindowAssignmentService -
/// see its own doc comment for the "Client 3 silently pointed at the wrong
/// window" bug this replaced pure PID-sort-order indexing to fix.
///
/// §105 turned the four slots into switchable PROFILES while keeping the
/// multi-client presentation they have always had. Two things changed here:
/// every slot is selectable (a slot with no running PRO window still has a
/// session, catch logs, PVP log, boss cooldowns, appearance and stat
/// preferences of its own, and switching to it is a reasonable thing to
/// want), and a slot another live tracker window holds is offered rather
/// than blocked - picking it asks for confirmation and then takes it over,
/// with the other window stepping down instead of the two racing each other
/// (see SessionPersistenceService.StillOwnsClientLock). The window handle,
/// where one exists, is still bound exactly as before.
/// </summary>
public sealed partial class ClientSelectorViewModel : ViewModelBase
{
    public const int MaxClients = 4;

    private readonly IWindowCaptureService _captureService = WindowCaptureServiceFactory.Instance;
    private List<ClientWindowInfo> _availableClients = new();

    // Resolved by LoadClients, consumed by Confirm - which PRO window (if
    // any) each slot number currently maps to. Replaces indexing straight
    // into _availableClients by (clientNumber - 1); see
    // ClientWindowAssignmentService's doc comment for why that was wrong.
    private readonly Dictionary<int, ClientWindowInfo> _slotWindows = new();

    public ObservableCollection<ClientSlotItem> Slots { get; } = new();

    [ObservableProperty] private string? statusMessage;

    public int SelectedClientNumber { get; private set; }

    /// <summary>§105: true when the confirmed slot was held by another live
    /// tracker window, so the caller claims the lock with force. Read by
    /// MainWindow's picker hook alongside SelectedClientNumber.</summary>
    public bool ForceTakeover { get; private set; }

    /// <summary>Set by ClientSelectorWindow - the yes/no prompt this window
    /// uses for both of its confirmations: taking a profile away from another
    /// running tracker window (§105), and moving a running client onto a slot
    /// that has none (§110). Same Request*/Func hook shape as
    /// MainWindowViewModel's dialogs; if the View never wires it up, both
    /// simply do not happen rather than happening unasked.</summary>
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>Raised once a client has been chosen and assigned - the View closes itself.</summary>
    public event Action? Confirmed;

    public ClientSelectorViewModel()
    {
        LoadClients();
    }

    /// <summary>
    /// §110, from the report: "it should force the PID down to window 2, 3
    /// or 4, but with the popup warning". A slot with no PRO window of its
    /// own can take one over - this offers the window this tracker is on
    /// right now (or, failing that, the first one running), names the slot
    /// holding it today, and on acceptance moves the remembered PID across.
    /// Because SetLastKnownProcessId is exclusive (§110) that single call
    /// both gives the window to this slot and takes it from the old one, so
    /// the mapping the picker shows and the window Play captures follow the
    /// choice together.
    ///
    /// Declining is not a failure - the slot stays the plain profile switch
    /// §105 made it, and Play will say so in words rather than quietly
    /// putting the player back on client 1. Nothing here moves a window
    /// without being asked; the prompt is the feature.
    /// </summary>
    private async Task<bool> TryMoveRunningClientOntoSlot(ClientSlotItem selected)
    {
        if (_availableClients.Count == 0 || ConfirmAsync is null)
            return false;

        // "Whichever client this tracker is capturing" - the window mapped to
        // the profile this window is on, falling back to the first running
        // client when the active profile has none either.
        int activeClient = SessionPersistenceService.ActiveClientNumber;

        ClientWindowInfo candidate =
            _slotWindows.TryGetValue(activeClient, out ClientWindowInfo? onNow)
                ? onNow
                : _availableClients[0];

        // Which slot owns it today, if any - named in the prompt so the trade
        // is explicit rather than something the player discovers later.
        int heldBySlot = _slotWindows
            .Where(pair => pair.Value.ProcessId == candidate.ProcessId)
            .Select(pair => pair.Key)
            .FirstOrDefault();

        string target = ClientNamesService.GetDisplayName(selected.ClientNumber);
        string? holder = heldBySlot > 0 ? ClientNamesService.GetDisplayName(heldBySlot) : null;

        string question = holder is null
            ? $"Move the running PRO client (PID {candidate.ProcessId}) onto {target}?"
            : $"The running PRO client (PID {candidate.ProcessId}) is currently matched to {holder}. " +
              $"Move it onto {target}?";

        string consequence = holder is null
            ? string.Empty
            : $"\n\n{holder} keeps every bit of its hunt data, catch logs and appearance - " +
              "it just has no client to capture until another PRO window is open.";

        bool confirmed = await ConfirmAsync(
            $"{target} has no PRO client window of its own.\n\n{question}\n\n" +
            "Only which window gets captured moves. Hunt data, catch logs, boss cooldowns " +
            "and appearance all stay with the profile they belong to." + consequence);

        if (!confirmed)
            return false;

        _captureService.SelectWindow(candidate.Handle);
        ClientWindowAssignmentService.SetLastKnownProcessId(selected.ClientNumber, candidate.ProcessId);
        return true;
    }

    private void LoadClients()
    {
        Slots.Clear();
        _slotWindows.Clear();

        if (!_captureService.IsAvailable)
        {
            StatusMessage = _captureService.LastError;
            return;
        }

        _availableClients = _captureService.FindClientWindows("PROClient").ToList();

        // §110: the two sticky/leftover passes that used to live here moved
        // into ClientWindowAssignmentService.MapSlotsToWindows, because Play
        // needs the identical answer - see that method's remarks. This window
        // and the hunt loop now read one mapping, so a slot labelled "no PRO
        // window" here cannot be a slot Play quietly captures something for.
        foreach (KeyValuePair<int, ClientWindowInfo> pair in
                 ClientWindowAssignmentService.MapSlotsToWindows(_availableClients, MaxClients))
        {
            _slotWindows[pair.Key] = pair.Value;
        }

        int currentClient = SessionPersistenceService.ActiveClientNumber;

        for (int clientNumber = 1; clientNumber <= MaxClients; clientNumber++)
        {
            // §109: the out parameter is nullable and presence is the null test
            // itself. Behaviour is unchanged - TryGetValue only ever returns
            // true with a window attached - but the compiler cannot carry a
            // bool held in a separate variable across to the dereference on the
            // next line, which is exactly what CS8600 and CS8602 reported here.
            _slotWindows.TryGetValue(clientNumber, out ClientWindowInfo? window);
            bool found = window is not null;

            string foundText = window is not null
                ? $"Client {clientNumber} - PID {window.ProcessId}"
                : $"Client {clientNumber} - no PRO window";

            // §105: the lock is reported, not enforced, in this list. Held by
            // a still-running tracker window means "you will be asked to
            // confirm", not "you cannot have this".
            bool free = SessionPersistenceService.IsClientLockAvailable(clientNumber, out int? heldBy);
            bool isCurrent = clientNumber == currentClient;

            string statusText =
                isCurrent ? "This window is on this profile now."
                : !free ? $"In use by another tracker window (PID {heldBy}) - picking it will ask to take over."
                : !found ? "Profile only - no client to capture until a PRO window is open."
                : string.Empty;

            Slots.Add(new ClientSlotItem(clientNumber)
            {
                FoundText = foundText,
                StatusText = statusText,
                IsHeldByAnotherWindow = !free && !isCurrent,
                HeldByProcessId = heldBy,
                IsChecked = isCurrent,
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
    private async Task Confirm()
    {
        ClientSlotItem? selected = Slots.FirstOrDefault(s => s.IsChecked);

        if (selected is null)
        {
            StatusMessage = "Please select a client.";
            return;
        }

        // Save every slot's name, not just the one being picked - renaming a
        // client you're not assigning to right now should still stick.
        foreach (ClientSlotItem slot in Slots)
            ClientNamesService.SetName(slot.ClientNumber, slot.CustomName);

        // §105: taking a profile away from another live tracker window is an
        // explicit, confirmed act - never a side effect of picking a row.
        if (selected.IsHeldByAnotherWindow)
        {
            if (ConfirmAsync is null)
            {
                StatusMessage = "That profile is in use by another tracker window.";
                return;
            }

            string name = string.IsNullOrWhiteSpace(selected.CustomName)
                ? $"Client {selected.ClientNumber}"
                : $"Client {selected.ClientNumber} ({selected.CustomName.Trim()})";

            bool confirmed = await ConfirmAsync(
                $"{name} is already being tracked by another Pro Tracker window " +
                $"(PID {selected.HeldByProcessId}).\n\n" +
                "Take it over? That window will finish its last save and stop " +
                "recording to this profile - its counters stay on screen, and " +
                "none of its data is deleted.");

            if (!confirmed)
                return;

            ForceTakeover = true;
        }

        // §109: nullable out. The inline pattern narrows it inside the
        // block, so the two uses below need no guard.
        if (_slotWindows.TryGetValue(selected.ClientNumber, out ClientWindowInfo? selectedClient))
        {
            _captureService.SelectWindow(selectedClient.Handle);

            // Remembered so the NEXT time this slot is offered, it finds this
            // exact window again first - see ClientWindowAssignmentService.
            ClientWindowAssignmentService.SetLastKnownProcessId(selected.ClientNumber, selectedClient.ProcessId);
        }
        else
        {
            // §110: the slot has no window of its own. §105 left that as a
            // silent profile switch, which is how a player could land on a
            // profile that could never capture anything. Now it offers to
            // move a running client across first.
            await TryMoveRunningClientOntoSlot(selected);
        }

        SelectedClientNumber = selected.ClientNumber;

        Confirmed?.Invoke();
    }
}
