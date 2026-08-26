using CommunityToolkit.Mvvm.ComponentModel;

namespace Foot_Tracker.ViewModels;

/// <summary>One checkbox row in the Exclude Stats window (Stats menu). Key
/// matches an entry in UiPreferencesService.ExcludableStats/UiPreferences.ExcludedStats -
/// DisplayName is just the label shown next to the checkbox.</summary>
public sealed partial class StatToggleOption : ObservableObject
{
    public string Key { get; }
    public string DisplayName { get; }

    [ObservableProperty] private bool isExcluded;

    public StatToggleOption(string key, string displayName, bool isExcluded)
    {
        Key = key;
        DisplayName = displayName;
        this.isExcluded = isExcluded;
    }
}
