using System.Collections.Generic;

namespace Foot_Tracker.Models
{
    /// <summary>
    /// Small persisted preferences for the main window's own layout/display -
    /// distinct from AppearanceSettings (colors/fonts/background) and from the
    /// hunt data itself (HuntSession). Currently covers:
    ///   - Which side the stats panel docks to (see MainWindowViewModel's
    ///     StatsPanelOnRight/StatsPanelDock) - lets multi-client hunters running
    ///     several instances side by side keep the stats column toward the
    ///     middle of the screen.
    ///   - Which individual stats the "Exclude Stats" window (Stats menu) has
    ///     hidden from the main form's stats panel. Excluded stats keep
    ///     counting internally (HuntSession is untouched) - they're just not
    ///     displayed. See UiPreferencesService.ExcludableStats for the list of
    ///     valid keys.
    /// </summary>
    public class UiPreferences
    {
        public bool StatsPanelOnRight { get; set; } = true;

        public List<string> ExcludedStats { get; set; } = new();
    }
}
