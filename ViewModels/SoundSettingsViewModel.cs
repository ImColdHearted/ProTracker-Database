using System.Collections.ObjectModel;
using Avalonia.Threading;
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
/// SinceShinySoundVolume), and since §389 through which output device
/// (SoundOutputDevice). Follows the same load-a-working-copy/Save
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

    // §389: where the alerts play. "System default" first and always, then
    // whatever SoundOutputDevices lists - filled in off the UI thread after
    // the window opens, because on Linux listing means running a tool or
    // two. A saved device that is not in today's list is added at the end,
    // marked, and stays selected: a headset that is switched off while the
    // window is open is not a reason to lose the setting. Test plays through
    // the selection, saved or not, like the volume.
    public ObservableCollection<SoundOutputDevice> OutputDeviceOptions { get; } = new() { SoundOutputDevice.SystemDefault };

    // Nullable because a ComboBox with nothing selected pushes null; that
    // reads as the system default everywhere below.
    [ObservableProperty] private SoundOutputDevice? selectedOutputDevice = SoundOutputDevice.SystemDefault;

    // Under the picker: how many devices were found, what the choice means,
    // or that the saved device is away.
    [ObservableProperty] private string outputDeviceNote = string.Empty;

    // Windows and Linux offer the picker; the Mac's own Sound settings
    // choose the output (afplay has no device option), and the window says
    // so instead.
    public bool CanPickOutputDevice { get; } = SoundOutputDevices.CanPickOnThisPlatform;

    public string OutputDevicePlatformNote { get; } = OperatingSystem.IsMacOS()
        ? "macOS chooses the output device itself - System Settings, Sound."
        : "This platform chooses the output device itself.";

    // The pin as loaded, and the stand-in row for it while it is not listed.
    private readonly SoundOutputDevice? savedOutputDevice;
    private SoundOutputDevice? absentOutputDevice;
    private bool outputDevicesListed;

    private const string AbsentSuffix = "  (not found right now)";

    partial void OnSelectedOutputDeviceChanged(SoundOutputDevice? value) =>
        OutputDeviceNote = DescribeOutputChoice(value ?? SoundOutputDevice.SystemDefault);

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
            ? $"Sounds and the output device are the same for every client. This tracker is on {ClientNamesService.GetDisplayName(activeClient)}; Save applies the choice to all of them."
            : "Sounds and the output device are the same for every client. Save applies the choice to all of them.";

        // §389.
        savedOutputDevice = SoundOutputDevice.Parse(_workingPreferences.SoundOutputDevice, _workingPreferences.SoundOutputDeviceLabel);

        if (CanPickOutputDevice)
        {
            OutputDeviceNote = "Looking for output devices...";
            ListOutputDevices();
        }
    }

    /// <summary>§389. Lists the devices off the UI thread and shows them
    /// when they arrive. Goes through SoundNotificationService so the list
    /// it checks alerts against is the same one the window shows.</summary>
    private void ListOutputDevices()
    {
        Task.Run(() =>
        {
            IReadOnlyList<SoundOutputDevice> found;

            try
            {
                found = SoundNotificationService.RefreshOutputDevices();
            }
            catch
            {
                found = Array.Empty<SoundOutputDevice>();
            }

            Dispatcher.UIThread.Post(() => ShowOutputDevices(found));
        });
    }

    private void ShowOutputDevices(IReadOnlyList<SoundOutputDevice> found)
    {
        // System default stays first; today's list follows.
        while (OutputDeviceOptions.Count > 1)
            OutputDeviceOptions.RemoveAt(OutputDeviceOptions.Count - 1);

        foreach (SoundOutputDevice device in found)
            OutputDeviceOptions.Add(device);

        SoundOutputDevice? choice = null;
        absentOutputDevice = null;

        if (savedOutputDevice is not null)
        {
            choice = found.FirstOrDefault(device => device.Matches(savedOutputDevice));

            if (choice is null)
            {
                absentOutputDevice = savedOutputDevice with { Label = savedOutputDevice.Label + AbsentSuffix };
                OutputDeviceOptions.Add(absentOutputDevice);
                choice = absentOutputDevice;
            }
        }

        outputDevicesListed = true;
        SelectedOutputDevice = choice ?? SoundOutputDevice.SystemDefault;

        // The setter above says nothing when the selection did not change.
        OutputDeviceNote = DescribeOutputChoice(SelectedOutputDevice ?? SoundOutputDevice.SystemDefault);
    }

    /// <summary>§389. The device a Test or a Save means: the saved pin
    /// itself when its stand-in row is selected, the row otherwise, null
    /// for the system default.</summary>
    private SoundOutputDevice? ChosenOutputDevice()
    {
        SoundOutputDevice selected = SelectedOutputDevice ?? SoundOutputDevice.SystemDefault;

        if (selected.IsSystemDefault)
            return null;

        if (absentOutputDevice is not null && ReferenceEquals(selected, absentOutputDevice))
            return savedOutputDevice;

        return selected;
    }

    private string DescribeOutputChoice(SoundOutputDevice choice)
    {
        if (!outputDevicesListed)
            return "Looking for output devices...";

        int listed = OutputDeviceOptions.Count - 1 - (absentOutputDevice is null ? 0 : 1);

        string found = listed switch
        {
            0 => $"No output devices were found - {SoundOutputDevices.ListingHint}.",
            1 => "1 output device found.",
            _ => $"{listed} output devices found.",
        };

        if (choice.IsSystemDefault)
            return found + " Alerts play on whatever the system sends sound to.";

        if (absentOutputDevice is not null && ReferenceEquals(choice, absentOutputDevice) && savedOutputDevice is not null)
            return $"{savedOutputDevice.Label} is not connected right now - alerts play on the system default until it is back. Save keeps the choice.";

        return $"{found} Alerts play through {choice.Label}; if it is unplugged they fall back to the system default.";
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

        // §389: through the picker's choice, saved or not.
        SoundOutputDevice? through = ChosenOutputDevice();

        SoundNotificationService.PlaySound(soundName, SoundNotificationService.SoundPriority.Shiny, volumePercent, through);

        TestNote = through is null
            ? $"Playing {soundName} at {volumePercent}%. If nothing is heard, today's log says what the system's player answered."
            : $"Playing {soundName} at {volumePercent}% through {through.Label}. If nothing is heard, today's log says what the system's player answered, and whether the device was found.";
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

            // §389: the pin and its label; empty for the system default.
            SoundOutputDevice? chosen = ChosenOutputDevice();
            string outputPin = chosen?.Pin ?? string.Empty;
            string outputLabel = chosen?.Label ?? string.Empty;
            _workingPreferences.SoundOutputDevice = outputPin;
            _workingPreferences.SoundOutputDeviceLabel = outputLabel;

            UiPreferencesService.Save(_workingPreferences);

            // §146: the same values into every other client's file too.
            UiPreferencesService.SaveSoundsForEveryClient(
                SelectedSinceFormSound, SelectedSinceShinySound, formVolume, shinyVolume, outputPin, outputLabel, ClientSelectorViewModel.MaxClients);

            SavedSuccessfully?.Invoke();
        }
        catch (Exception ex)
        {
            SaveError = $"The sound preferences could not be saved.\n\n{ex.Message}";
        }
    }
}
