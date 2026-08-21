using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;
using Foot_Tracker.Tracking;
using Foot_Tracker.Tracking.Capture;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Ported from ProTrackerandDatabase.cs (the WinForms main form). The hunt-session
/// domain logic (HuntSession, LifetimeStatsService, SessionPersistenceService,
/// EncounterTracker) is untouched - only the "glue" that used to live directly in
/// the form's code-behind (InvokeRequired/BeginInvoke, direct control.Text = ...,
/// MessageBox.Show, and hand-built TableLayoutPanel rows) has been rewritten as
/// bindable properties/commands for MainWindow.axaml.
///
/// NOT yet ported from the original form (tracked in MIGRATION_GUIDE.md):
///   - Admin-elevation warning dialog (ShowAdministratorWarning)
///   - Multi-client picker dialog (ClientSelector) - currently auto-picks client 1
///   - The 20+ menu items that open the other WinForms child forms
///     (Interactive Maps, Boss Cooldowns, Counterparts, Lifetime Stats, etc.)
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly HuntSession huntSession = new();
    private readonly DispatcherTimer huntTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly EncounterTracker encounterTracker = new();
    private readonly IWindowCaptureService captureService = WindowCaptureServiceFactory.Instance;

    private LifetimeStats lifetimeStats = LifetimeStatsService.Load();
    private int lifetimeSaveTickCounter;
    private int selectedClientNumber;

    public ObservableCollection<EncounterCountRow> SessionEncounters { get; } = new();
    public ObservableCollection<EncounterCountRow> SessionEncountersLeft { get; } = new();
    public ObservableCollection<EncounterCountRow> SessionEncountersRight { get; } = new();

    // Matches the original's TableLayoutPanel behavior: fill the left column
    // first, then continue filling the right column once the left one is full.
    private const int EncounterColumnCapacity = 15;

    public IReadOnlyList<string> AvailablePokemon =>
        PokemonSpriteService.AllPokemon
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToList();

    [ObservableProperty] private string windowTitle = "Pro Tracker & Database";
    [ObservableProperty] private string? targetPokemonInput;

    [ObservableProperty] private string totalEncounters = "0";
    [ObservableProperty] private string timeHunting = "00:00:00";
    [ObservableProperty] private string sinceShiny = "0";
    [ObservableProperty] private string sinceForm = "0";
    [ObservableProperty] private string catchRate = "0.00%";
    [ObservableProperty] private string successfulCatches = "0";
    [ObservableProperty] private string failedCatches = "0";

    [ObservableProperty] private Bitmap? currentlyHuntingSprite;
    [ObservableProperty] private Bitmap? currentEncounterSprite;
    [ObservableProperty] private Bitmap? previousEncounterSprite;

    [ObservableProperty] private string currentlyHuntedLabel = "None";
    [ObservableProperty] private string currentEncounteredLabel = "None";
    [ObservableProperty] private string previouslyEncounteredLabel = "None";

    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private string? statusMessage;

    /// <summary>
    /// Set by MainWindow.axaml.cs to show ClientSelectorWindow (a ViewModel shouldn't
    /// own a Window reference). Returns the chosen client number, or 0 if cancelled.
    /// Replaces: using ClientSelector form = new(); form.ShowDialog(this);
    /// </summary>
    public Func<Task<int>>? RequestClientSelection { get; set; }

    /// <summary>
    /// Set by MainWindow.axaml.cs to show PokemonSelectorWindow. Returns the chosen
    /// Pokémon name, or null if cancelled. Replaces: using var selector = new PokemonSelectorForm();
    /// </summary>
    public Func<Task<string?>>? RequestPokemonSelection { get; set; }

    /// <summary>Set by MainWindow.axaml.cs - replaces MessageBox.Show(..., MessageBoxButtons.YesNo).</summary>
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>
    /// Set by MainWindow.axaml.cs to show a save-file dialog. Takes a suggested
    /// file name and the extension ("csv"/"json"), returns the chosen path or
    /// null if cancelled. Replaces WinForms' SaveFileDialog.
    /// </summary>
    public Func<string, string, Task<string?>>? RequestSaveFilePath { get; set; }

    /// <summary>
    /// Set by MainWindow.axaml.cs to show an open-file dialog for the given
    /// extension ("csv"/"json"), returning the chosen path or null if cancelled.
    /// Replaces WinForms' OpenFileDialog.
    /// </summary>
    public Func<string, Task<string?>>? RequestOpenFilePath { get; set; }

    public MainWindowViewModel()
    {
        huntTimer.Tick += HuntTimer_Tick;

        encounterTracker.EncounterDetected += name =>
            Dispatcher.UIThread.Post(() => RegisterEncounter(name));

        encounterTracker.StatusChanged += status =>
            Dispatcher.UIThread.Post(() => StatusMessage = status);

        encounterTracker.CatchResultDetected += result =>
            Dispatcher.UIThread.Post(() => OnCatchResultDetected(result));

        encounterTracker.RareEncounterDetected += (name, rareType) =>
            Dispatcher.UIThread.Post(() => OnRareEncounterDetected(rareType));

        LoadPreviousSession();
        UpdateTrackerDisplay();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Play()
    {
        if (string.IsNullOrWhiteSpace(huntSession.TargetPokemon))
        {
            StatusMessage = "Select a Pokémon before starting the tracker.";
            return;
        }

        var clients = captureService.FindClientWindows("PROClient");

        if (clients.Count == 0)
        {
            StatusMessage = !captureService.IsAvailable
                ? captureService.LastError
                : "No Pokémon Revolution Online client was found.";
            return;
        }

        // AssignTrackerClient may reload a previously-saved session for this
        // client (e.g. the very first time it's assigned, or after switching
        // clients). Remember whatever the user just picked so it isn't lost
        // if nothing had been persisted for this client yet.
        string? pendingTarget = huntSession.TargetPokemon;

        if (clients.Count == 1)
        {
            captureService.SelectWindow(clients[0].Handle);
            AssignTrackerClient(1);
        }
        else if (selectedClientNumber == 0)
        {
            int chosen = RequestClientSelection is not null
                ? await RequestClientSelection()
                : 0;

            if (chosen == 0)
                return;

            AssignTrackerClient(chosen);
        }

        if (string.IsNullOrWhiteSpace(huntSession.TargetPokemon) &&
            !string.IsNullOrWhiteSpace(pendingTarget))
        {
            huntSession.TargetPokemon = pendingTarget;
            SessionPersistenceService.Save(huntSession);
        }

        huntSession.Start();
        huntTimer.Start();

        // EncounterTracker's polling loop still uses the Windows-only OCR pipeline
        // (System.Drawing-based image processing feeding Tesseract) - see
        // MIGRATION_GUIDE.md for why that's a separate follow-up from window
        // finding/capture. Hunt session tracking (timer/target/manual stats) still
        // works fine elsewhere; only automatic encounter detection is skipped.
        if (OperatingSystem.IsWindows())
        {
            encounterTracker.Start();
        }
        else
        {
            StatusMessage = "Hunting started. Automatic encounter detection isn't available on " +
                             $"{captureService.PlatformName} yet - tracking is manual for now.";
        }

        IsRunning = true;
        UpdateTrackerDisplay();
        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() => !huntSession.IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        if (!huntSession.IsRunning)
            return;

        huntSession.Pause();
        huntTimer.Stop();

        await encounterTracker.StopAsync();

        SessionPersistenceService.Save(huntSession);
        FlushPendingLifetimeTime();

        IsRunning = false;
        UpdateTrackerDisplay();
        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private bool CanStop() => huntSession.IsRunning;

    [RelayCommand]
    private async Task Reset()
    {
        bool confirmed = ConfirmAsync is not null
            ? await ConfirmAsync("Reset the current hunt?")
            : true;

        if (!confirmed)
            return;

        huntTimer.Stop();
        await encounterTracker.StopAsync();

        FlushPendingLifetimeTime();
        huntSession.Reset();
        SessionPersistenceService.Delete();

        IsRunning = false;
        UpdateTrackerDisplay();
        UpdateSessionEncounters();
        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task SelectTarget()
    {
        // Prefer the sprite-picker dialog (ported PokemonSelectorForm) if the View
        // wired one up; otherwise fall back to whatever was typed in the text box.
        string? chosen = RequestPokemonSelection is not null
            ? await RequestPokemonSelection()
            : TargetPokemonInput;

        if (string.IsNullOrWhiteSpace(chosen))
            return;

        huntSession.TargetPokemon = chosen.Trim();
        TargetPokemonInput = huntSession.TargetPokemon;
        UpdateTrackerDisplay();
        SessionPersistenceService.Save(huntSession);
    }

    // ============================================================
    // IMPORT / EXPORT - ported from saveDataToolStripMenuItem1/2_Click and
    // saveJSONDataToolStripMenuItem/importJSONToolStripMenuItem_Click.
    // ============================================================

    [RelayCommand]
    private async Task ExportCsv()
    {
        string suggestedName = $"ProTracker-Hunt-{DateTime.Now:yyyy-MM-dd-HHmmss}.csv";

        string? path = RequestSaveFilePath is not null
            ? await RequestSaveFilePath(suggestedName, "csv")
            : null;

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            HuntDataExportService.ExportCsv(huntSession, path);
            StatusMessage = "Hunt data exported successfully.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"The hunt data could not be exported: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task ImportCsv() =>
        ImportHuntData(HuntDataExportService.ImportCsv, "csv");

    [RelayCommand]
    private async Task ExportJson()
    {
        string suggestedName = $"ProTracker-Hunt-{DateTime.Now:yyyy-MM-dd-HHmmss}.json";

        string? path = RequestSaveFilePath is not null
            ? await RequestSaveFilePath(suggestedName, "json")
            : null;

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            HuntDataExportService.ExportJson(huntSession, path);
            StatusMessage = "Hunt data exported successfully.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"The hunt data could not be exported: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task ImportJson() =>
        ImportHuntData(HuntDataExportService.ImportJson, "json");

    private async Task ImportHuntData(Func<string, HuntSessionSaveData> importer, string extension)
    {
        string? path = RequestOpenFilePath is not null
            ? await RequestOpenFilePath(extension)
            : null;

        if (string.IsNullOrWhiteSpace(path))
            return;

        bool confirmed = ConfirmAsync is not null
            ? await ConfirmAsync("Importing this file will replace the current hunt statistics.\n\nContinue?")
            : true;

        if (!confirmed)
            return;

        try
        {
            HuntSessionSaveData data = importer(path);

            huntTimer.Stop();
            huntSession.Restore(data);

            IsRunning = false;
            UpdateTrackerDisplay();
            UpdateSessionEncounters();
            SessionPersistenceService.Save(huntSession);

            PlayCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();

            StatusMessage = "Hunt data imported successfully.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"The hunt data could not be imported: {ex.Message}";
        }
    }

    // Ported from ProTrackerandDatabase.cs's assignClientToolStripMenuItem_Click -
    // manually (re)opens the client picker at any time, not just automatically
    // on the first Play. Matches the original: does NOT stop an in-progress
    // encounterTracker poll when switching clients mid-hunt (huntSession itself
    // gets paused, so any stray detections are ignored - see AssignTrackerClient).
    [RelayCommand]
    private async Task AssignClient()
    {
        int chosen = RequestClientSelection is not null
            ? await RequestClientSelection()
            : 0;

        if (chosen == 0)
            return;

        AssignTrackerClient(chosen);
    }

    private void AssignTrackerClient(int clientNumber)
    {
        if (clientNumber < 1)
            return;

        // Already tracking this client - nothing to reload. Without this guard,
        // every Play click re-ran LoadPreviousSession() below and clobbered
        // whatever the user had just picked with the last-saved file.
        if (selectedClientNumber == clientNumber)
            return;

        if (selectedClientNumber > 0)
        {
            if (huntSession.IsRunning)
            {
                huntSession.Pause();
                huntTimer.Stop();
            }

            FlushPendingLifetimeTime();
            SessionPersistenceService.Save(huntSession);
        }

        selectedClientNumber = clientNumber;
        SessionPersistenceService.SetActiveClient(clientNumber);

        LoadPreviousSession();
        UpdateTrackerDisplay();
        UpdateSessionEncounters();
    }

    private void HuntTimer_Tick(object? sender, EventArgs e)
    {
        if (huntSession.IsRunning)
        {
            lifetimeSaveTickCounter++;

            if (lifetimeSaveTickCounter >= 30)
            {
                lifetimeStats = LifetimeStatsService.AddHuntingTime(
                    TimeSpan.FromSeconds(lifetimeSaveTickCounter));

                lifetimeSaveTickCounter = 0;
            }
        }

        UpdateTrackerDisplay();
    }

    private void LoadPreviousSession()
    {
        HuntSessionSaveData? saved = SessionPersistenceService.Load();

        if (saved is null)
        {
            huntSession.Reset();
        }
        else
        {
            huntSession.Restore(saved);
        }

        UpdateTrackerDisplay();
        UpdateSessionEncounters();
    }

    private void FlushPendingLifetimeTime()
    {
        if (lifetimeSaveTickCounter <= 0)
            return;

        lifetimeStats = LifetimeStatsService.AddHuntingTime(
            TimeSpan.FromSeconds(lifetimeSaveTickCounter));

        lifetimeSaveTickCounter = 0;
    }

    private void RegisterEncounter(string pokemonName)
    {
        if (!huntSession.IsRunning)
            return;

        string resolvedName = PokemonSpriteService.ResolveEncounterName(pokemonName);

        huntSession.PreviousEncounter = huntSession.CurrentEncounter;
        huntSession.CurrentEncounter = resolvedName;

        huntSession.TotalEncounters++;
        huntSession.EncountersSinceShiny++;
        huntSession.EncountersSinceForm++;

        huntSession.RegisterPokemonEncounter(resolvedName);

        lifetimeStats = LifetimeStatsService.AddEncounter(resolvedName);

        UpdateTrackerDisplay();
        UpdateSessionEncounters();
        SessionPersistenceService.Save(huntSession);
    }

    private void OnCatchResultDetected(CatchResult result)
    {
        if (!huntSession.IsRunning)
            return;

        switch (result)
        {
            case CatchResult.Success:
                huntSession.SuccessfulCatches++;
                lifetimeStats = LifetimeStatsService.AddSuccessfulCatch();
                break;

            case CatchResult.Failed:
                huntSession.FailedCatches++;
                lifetimeStats = LifetimeStatsService.AddFailedCatch();
                break;

            default:
                return;
        }

        UpdateTrackerDisplay();
        SessionPersistenceService.Save(huntSession);
    }

    private void OnRareEncounterDetected(RareEncounterType rareType)
    {
        if (!huntSession.IsRunning)
            return;

        switch (rareType)
        {
            case RareEncounterType.Shiny:
                huntSession.EncountersSinceShiny = 0;
                lifetimeStats = LifetimeStatsService.AddShinyEncounter();
                break;

            case RareEncounterType.Form:
                huntSession.EncountersSinceForm = 0;
                lifetimeStats = LifetimeStatsService.AddFormEncounter();
                break;

            default:
                return;
        }

        UpdateTrackerDisplay();
        SessionPersistenceService.Save(huntSession);
    }

    private void UpdateSessionEncounters()
    {
        // Replaces ResetEncounterTable/UpdateSessionEncounters, which manually
        // built TableLayoutPanel rows. MainWindow.axaml binds directly to these
        // collections instead - split across two columns the same way the
        // original continued into a second SessionEncountersTableRight panel
        // once the left one filled up.
        SessionEncounters.Clear();
        SessionEncountersLeft.Clear();
        SessionEncountersRight.Clear();

        int total = huntSession.EncounterCounts.Values.Sum();

        foreach (var kvp in huntSession.EncounterCounts.OrderByDescending(k => k.Value))
        {
            var row = new EncounterCountRow
            {
                PokemonName = kvp.Key,
                Count = kvp.Value,
                RatePercent = total > 0 ? kvp.Value / (double)total * 100.0 : 0,
                Sprite = PokemonSpriteService.GetSprite(kvp.Key)
            };

            SessionEncounters.Add(row);

            if (SessionEncountersLeft.Count < EncounterColumnCapacity)
                SessionEncountersLeft.Add(row);
            else
                SessionEncountersRight.Add(row);
        }
    }

    private void UpdateTrackerDisplay()
    {
        string clientText = selectedClientNumber > 0 ? $" - Client {selectedClientNumber}" : string.Empty;

        WindowTitle = huntSession.IsRunning && !string.IsNullOrWhiteSpace(huntSession.TargetPokemon)
            ? $"Pro Tracker & Database - Hunting {huntSession.TargetPokemon}{clientText}"
            : $"Pro Tracker & Database{clientText}";

        TotalEncounters = huntSession.TotalEncounters.ToString();
        TimeHunting = TimeFormatHelper.FormatElapsed(huntSession.GetCurrentElapsedTime());
        SinceShiny = huntSession.EncountersSinceShiny.ToString();
        SinceForm = huntSession.EncountersSinceForm.ToString();

        int totalCatchAttempts = huntSession.SuccessfulCatches + huntSession.FailedCatches;
        double rate = totalCatchAttempts > 0
            ? huntSession.SuccessfulCatches / (double)totalCatchAttempts * 100.0
            : 0;

        CatchRate = $"{rate:F2}%";
        SuccessfulCatches = huntSession.SuccessfulCatches.ToString();
        FailedCatches = huntSession.FailedCatches.ToString();

        CurrentlyHuntingSprite = PokemonSpriteService.GetEncounterSprite(huntSession.TargetPokemon);
        CurrentEncounterSprite = PokemonSpriteService.GetEncounterSprite(huntSession.CurrentEncounter);
        PreviousEncounterSprite = PokemonSpriteService.GetEncounterSprite(huntSession.PreviousEncounter);

        CurrentlyHuntedLabel = string.IsNullOrWhiteSpace(huntSession.TargetPokemon) ? "None" : huntSession.TargetPokemon;
        CurrentEncounteredLabel = string.IsNullOrWhiteSpace(huntSession.CurrentEncounter) ? "None" : huntSession.CurrentEncounter;
        PreviouslyEncounteredLabel = string.IsNullOrWhiteSpace(huntSession.PreviousEncounter) ? "None" : huntSession.PreviousEncounter;
    }

    /// <summary>Call from MainWindow's Closing event - replaces OnFormClosing.</summary>
    public void OnClosing()
    {
        if (huntSession.IsRunning)
            huntSession.Pause();

        huntTimer.Stop();
        FlushPendingLifetimeTime();
        SessionPersistenceService.Save(huntSession);
    }

    public void Dispose()
    {
        huntTimer.Tick -= HuntTimer_Tick;
        encounterTracker.Dispose();
    }
}