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
    ///   - Whether the entire stats panel is hidden as one unit, regardless of
    ///     the per-stat list above - see StatsPanelHidden.
    ///   - Which sound (if any) plays when Since Form/Since Shiny resets - see
    ///     SinceFormSound/SinceShinySound.
    /// </summary>
    public class UiPreferences
    {
        public bool StatsPanelOnRight { get; set; } = true;

        public List<string> ExcludedStats { get; set; } = new();

        // §126. Which columns of the front encounter table are hidden -
        // see UiPreferencesService.ExcludableTableColumns for the valid keys.
        // Separate from ExcludedStats because they hide different things:
        // that list picks stat blocks in the side panel, this one picks
        // columns in the table. Sharing one list would make a stat key and a
        // column key collide as soon as two of them wanted the same name.
        public List<string> ExcludedTableColumns { get; set; } = new();

        // Hides the entire stats panel Border in MainWindow.axaml as one unit
        // (including Current Event/Pause Since Form, which aren't part of
        // ExcludedStats above since they're controls rather than pure stat
        // readouts) - see MainWindowViewModel.ShowStatsPanel and
        // ExcludeStatsWindow's master checkbox. Separate from ExcludedStats
        // because it's a different kind of hide: that list picks which
        // individual stat blocks show inside the panel, this hides the whole
        // panel regardless of what ExcludedStats says.
        public bool StatsPanelHidden { get; set; }

        // §201. Whether the Simulator draws its battles on the stadium
        // field - both Pokemon standing on the painted pads, the player seen
        // from behind - or falls back to the two bordered panels it used
        // before. Default on. A preference file from before this section has
        // no such key and loads as true, which is deliberate: the scene is
        // the point of the section and nobody has to go and find a switch to
        // see it. Section 188 gave everyone their own background image, so
        // there had to be a way back to the plain panels for anyone whose
        // background this fights with.
        public bool BattleSceneEnabled { get; set; } = true;

        // Which sound (see Services/SoundNotificationService.SoundCatalog)
        // plays when Since Form/Since Shiny resets, chosen independently for
        // each stat via MainWindow's File menu (Since Form Sound/Since Shiny
        // Sound submenus). "None" - the default for both - plays nothing, so
        // nobody's hunt starts making noise without asking. Windows only -
        // SoundNotificationService no-ops harmlessly on any other OS. This
        // started as one shared on/off switch with a single hardcoded sound
        // (see MIGRATION_GUIDE.md #65) before players asked for a per-stat
        // choice of sound (MIGRATION_GUIDE.md #71).
        public string SinceFormSound { get; set; } = "None";
        public string SinceShinySound { get; set; } = "None";

        // §152. How loud each of those plays, 0-100 (the Sound Settings
        // sliders; 100 is the clip as shipped, 0 is silence). A preference
        // file from before this section has no such keys and loads as 100,
        // so nobody's alerts get quieter without asking. Applied to the clip
        // itself - see SoundNotificationService's §152 remarks.
        public int SinceFormSoundVolume { get; set; } = 100;
        public int SinceShinySoundVolume { get; set; } = 100;

        // §135. The "Don't show this again" checkbox on the warning that
        // opens in front of Set Screen Boundaries. Per client like everything
        // else here - a player who dismissed it on one profile sees it once
        // more on another, which is the cheaper mistake.
        public bool SuppressBoundariesWarning { get; set; }

        // §135. The battle-window box the player drew by hand in Set Screen
        // Boundaries, or null when none is saved. Pixels of the captured
        // frame, not fractions - see ManualBattleBounds for why - and used by
        // BattleWindowLocator only when its automatic scan finds nothing.
        public ManualBattleBounds? ManualBattleBounds { get; set; }
    }
}
