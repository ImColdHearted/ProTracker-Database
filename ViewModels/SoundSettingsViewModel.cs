using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Backs the "Sound Settings" window (File menu). Lets the user pick which
/// sound (if any) plays when Since Form/Since Shiny resets - see
/// SoundNotificationService.SoundCatalog and UiPreferences.SinceFormSound/
/// SinceShinySound - and, since §152, how loud (SinceFormSoundVolume/
/// SinceShinySoundVolume). Follows the same load-a-working-copy/Save
/// pattern as AppearanceViewModel/ExcludeStatsViewModel.
///
/// This started as a single File-menu checkbox (MIGRATION_GUIDE.md #65),
/// then a pair of File-menu submenus (#71), now this dedicated window (#72)
/// so both choices sit in one obviously-a-dropdown place instead of a
/// checkbox-style submenu, and so a future third sound needs nothing more
/// than a new SoundCatalog entry - see SoundOptions below, which is
/// populated from that catalog rather than hardcoded here.
/// </summary>
public sealed partial class SoundSettingsViewModel : ViewModelBase
{
    private readonly UiPreferences _workingPreferences = UiPreferencesService.Load();

    // "None" first, then every SoundNotificationService.SoundCatalog key -
    // same ItemsSource-from-a-catalog approach as AppearanceViewModel's
    // FontFamilyOptions/FontSizeOptions/etc., so a new catalog entry shows
    // up in both dropdowns with no changes needed to this file or the XAML.
    public IReadOnlyList<string> SoundOptions { get; } =
        new[] { "None" }.Concat(SoundNotificationService.SoundCatalog.Keys).ToList();

    [ObservableProperty] private string selectedSinceFormSound = "None";
    [ObservableProperty] private string selectedSinceShinySound = "None";

    // §152: the sliders, 0-100. Doubles because that is what a Slider's
    // Value is; rounded to whole percents when played or saved. Test plays
    // at the slider's current position, saved or not, so "how loud is that"
    // is answered before Save.
    [ObservableProperty] private double sinceFormVolume = SoundNotificationService.MaxVolumePercent;
    [ObservableProperty] private double sinceShinyVolume = SoundNotificationService.MaxVolumePercent;

    // The "40%" beside each slider - the number Test plays at and Save keeps.
    [ObservableProperty] private string sinceFormVolumeText = $"{SoundNotificationService.MaxVolumePercent}%";
    [ObservableProperty] private string sinceShinyVolumeText = $"{SoundNotificationService.MaxVolumePercent}%";

    partial void OnSinceFormVolumeChanged(double value) => SinceFormVolumeText = $"{WholePercent(value)}%";
    partial void OnSinceShinyVolumeChanged(double value) => SinceShinyVolumeText = $"{WholePercent(value)}%";

    [ObservableProperty] private string? saveError;
    [ObservableProperty] private bool hasSaveError;

    // §146: what the Test buttons last did, and which clients a save
    // reaches - the sounds used to go quietly into one client's preference
    // file, so a hunt on another client played nothing.
    [ObservableProperty] private string testNote = string.Empty;
    [ObservableProperty] private string clientNote = string.Empty;

    partial void OnSaveErrorChanged(string? value) => HasSaveError = !string.IsNullOrEmpty(value);

    /// <summary>Raised when Save completes successfully - the View closes itself.</summary>
    public event Action? SavedSuccessfully;

    public SoundSettingsViewModel()
    {
        SelectedSinceFormSound = _workingPreferences.SinceFormSound;
        SelectedSinceShinySound = _workingPreferences.SinceShinySound;
        SinceFormVolume = Math.Clamp(_workingPreferences.SinceFormSoundVolume, 0, SoundNotificationService.MaxVolumePercent);
        SinceShinyVolume = Math.Clamp(_workingPreferences.SinceShinySoundVolume, 0, SoundNotificationService.MaxVolumePercent);

        int activeClient = SessionPersistenceService.ActiveClientNumber;

        ClientNote = activeClient > 0
            ? $"Sounds are the same for every client. This tracker is on {ClientNamesService.GetDisplayName(activeClient)}; Save applies the choice to all of them."
            : "Sounds are the same for every client. Save applies the choice to all of them.";
    }

    /// <summary>§146. Plays the selected sound through the exact path a real
    /// alert uses, at Shiny priority so nothing can suppress it - the quickest
    /// way to tell "no sound is selected" from "this machine cannot play it".
    /// The log records what MCI answered either way. §152: at the slider's
    /// volume, so it is also the quickest way to hear how loud that is.</summary>
    [RelayCommand]
    private void TestFormSound() => Test(SelectedSinceFormSound, "Since Form", SinceFormVolume);

    [RelayCommand]
    private void TestShinySound() => Test(SelectedSinceShinySound, "Since Shiny", SinceShinyVolume);

    private void Test(string soundName, string label, double volume)
    {
        if (string.IsNullOrWhiteSpace(soundName) || soundName == "None")
        {
            TestNote = $"{label} is set to None - nothing to play.";
            return;
        }

        // §148: Windows, Linux and macOS all play now.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            TestNote = "Sounds are not supported on this platform.";
            return;
        }

        int volumePercent = WholePercent(volume);

        if (volumePercent == 0)
        {
            TestNote = $"{label} is at 0% - it would play silently. Move the slider up to hear it.";
            return;
        }

        SoundNotificationService.PlaySound(soundName, SoundNotificationService.SoundPriority.Shiny, volumePercent);
        TestNote = $"Playing {soundName} at {volumePercent}%. If nothing is heard, today's log says what the system's player answered.";
    }

    private static int WholePercent(double volume) =>
        (int)Math.Round(Math.Clamp(volume, 0, SoundNotificationService.MaxVolumePercent));

    [RelayCommand]
    private void Save()
    {
        try
        {
            int formVolume = WholePercent(SinceFormVolume);
            int shinyVolume = WholePercent(SinceShinyVolume);

            _workingPreferences.SinceFormSound = SelectedSinceFormSound;
            _workingPreferences.SinceShinySound = SelectedSinceShinySound;
            _workingPreferences.SinceFormSoundVolume = formVolume;
            _workingPreferences.SinceShinySoundVolume = shinyVolume;

            UiPreferencesService.Save(_workingPreferences);

            // §146: the same values into every other client's file too.
            UiPreferencesService.SaveSoundsForEveryClient(
                SelectedSinceFormSound, SelectedSinceShinySound, formVolume, shinyVolume, ClientSelectorViewModel.MaxClients);

            SavedSuccessfully?.Invoke();
        }
        catch (Exception ex)
        {
            SaveError = $"The sound preferences could not be saved.\n\n{ex.Message}";
        }
    }
}
