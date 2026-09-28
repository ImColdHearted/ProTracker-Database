using CommunityToolkit.Mvvm.ComponentModel;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// §278. File - Tracker Settings: how the front encounter table is kept.
///
/// Two modes, and the first one is the tracker exactly as it has always
/// worked. That is not politeness about a default - the second mode writes a
/// file for every map visited, and a player who never asked for that should
/// never start paying for it.
///
/// The setting is per client, like everything else in UiPreferences, and is
/// written the moment it is chosen. There is no OK button to forget to press:
/// this window has one decision in it and closing it is not a way of
/// cancelling one.
/// </summary>
public sealed partial class TrackerSettingsViewModel : ViewModelBase
{
    private readonly UiPreferences preferences;

    private bool suppressSave = true;

    /// <summary>Classic: one table for the whole hunt, as before §278.</summary>
    [ObservableProperty] private bool wholeHuntSelected;

    /// <summary>Per map: the table is whichever map you are standing on.</summary>
    [ObservableProperty] private bool perMapSelected;

    /// <summary>What the last choice did, so the window confirms rather than
    /// leaving the player guessing whether a radio button saved anything.</summary>
    [ObservableProperty] private string statusMessage = string.Empty;

    /// <summary>§429. The level-sharing opt-in. Unticked until the player
    /// ticks it; saved with the other preferences and applied to the
    /// running tracker at once.</summary>
    [ObservableProperty] private bool shareLevelData;

    public TrackerSettingsViewModel()
        : this(UiPreferencesService.Load())
    {
    }

    public TrackerSettingsViewModel(UiPreferences preferences)
    {
        this.preferences = preferences;

        perMapSelected = preferences.PerMapEncounterTable;
        wholeHuntSelected = !preferences.PerMapEncounterTable;
        shareLevelData = preferences.ShareLevelData;

        suppressSave = false;
    }

    partial void OnPerMapSelectedChanged(bool value)
    {
        if (suppressSave)
            return;

        // Two radio buttons in one group: the one being turned OFF also fires
        // this. Only the one being turned ON decides anything, or the setting
        // would be written twice per click and the second write would be the
        // stale one.
        if (!value)
            return;

        Apply(true);
    }

    partial void OnShareLevelDataChanged(bool value)
    {
        if (suppressSave)
            return;

        preferences.ShareLevelData = value;

        UiPreferencesService.Save(preferences);

        TrackerSettings.ApplyLevelSharing(value);

        if (!value)
            LevelShareService.Discard();

        StatusMessage = value
            ? "Level sharing is on. From the next encounter, the species, map and level of what you meet is added to the shared level ranges - nothing else, and nothing that names you."
            : "Level sharing is off. Nothing more is sent; anything waiting to go was dropped.";
    }

    partial void OnWholeHuntSelectedChanged(bool value)
    {
        if (suppressSave || !value)
            return;

        Apply(false);
    }

    private void Apply(bool perMap)
    {
        preferences.PerMapEncounterTable = perMap;

        UiPreferencesService.Save(preferences);

        // The running tracker follows immediately - the choice is about what
        // the table in front of the player shows, and making them restart to
        // see it would be the wrong kind of setting.
        TrackerSettings.Apply(perMap);

        StatusMessage = perMap
            ? "The encounter table now follows the map you are on. Your stats are unchanged - they still cover the whole hunt."
            : "The encounter table covers the whole hunt again. The per-map tables are kept, not deleted.";
    }
}
