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
    ///   - §271: which counterpart sprite each hunting target is drawn as -
    ///     see TargetSpriteSkins.
    ///   - §278: whether the encounter table is kept per map - see
    ///     PerMapEncounterTable.
    ///   - Whether the entire stats panel is hidden as one unit, regardless of
    ///     the per-stat list above - see StatsPanelHidden.
    ///   - Which sound (if any) plays when Since Form/Since Shiny resets - see
    ///     SinceFormSound/SinceShinySound - and, §389, through which output
    ///     device - see SoundOutputDevice.
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

        // §389. Where those play: a device pin as SoundOutputDevice.Pin
        // writes it ("pulse:alsa_output.usb-...", "winmm:Headset Earphone
        // (Logitech..."), or empty - the default, and what every file from
        // before this section reads as - for whatever the operating system
        // sends sound to. The label beside it is what the Sound Settings
        // window shows for the pin while the device is unplugged, and what
        // the log names. Same for every client, like the four above; a
        // Linux hunter's alerts kept leaving his headset, which is where
        // this came from.
        public string SoundOutputDevice { get; set; } = string.Empty;
        public string SoundOutputDeviceLabel { get; set; } = string.Empty;

        // §135. The "Don't show this again" checkbox on the warning that
        // opens in front of Set Screen Boundaries. Per client like everything
        // else here - a player who dismissed it on one profile sees it once
        // more on another, which is the cheaper mistake.
        public bool SuppressBoundariesWarning { get; set; }

        // §278. Whether the front encounter table is kept PER MAP rather than
        // for the hunt as a whole - File, Tracker Settings. False is the
        // tracker as it has always worked, and is the default on purpose:
        // per-map tracking writes a file for every map visited, and that is a
        // cost nobody should start paying without asking for it.
        //
        // Only the TABLE partitions. Every stat - time hunting, Since Shiny,
        // the catch totals and the rate - stays whole in either mode, because
        // those are about the hunt and not about the ground you are standing
        // on. See MapEncounterService.
        public bool PerMapEncounterTable { get; set; }

        // §429. Whether this tracker SHARES the levels it meets Pokémon at -
        // species, map and level, nothing else - with the events server, so
        // every Map Explorer can show the level range each species spawns
        // in on each map (and a hunter can pick a repel-trick level). OFF by
        // default, and only ever turned on by the player: File, Tracker
        // Settings, or the "Share level data" box in the Map Explorer. What
        // is sent is the lowest and highest level per species per map, once
        // an encounter's record is final; the server keeps only the widest
        // range it has ever been told. See LevelShareService.
        public bool ShareLevelData { get; set; }

        // §271. Which counterpart sprite each hunting target is DRAWN as -
        // species name (as the target was set) to the catalog-relative image
        // path, e.g. "Charizard" ->
        // "SharedPokemonLibrary/Assets/Counterparts/Halloween/Charizard.png".
        // A species with no entry here draws its ordinary sprite, which is
        // every species until the player right-clicks one and picks.
        //
        // Here rather than in HuntSession because nothing about it is hunt
        // data: it changes no count, it is not exported, and a hunt reset
        // should not undo a skin the player chose. Keyed by SPECIES rather
        // than by slot so reordering the targets - or swapping one out and
        // back - keeps each one looking the way it was set, and so the same
        // choice applies in World Quest mode, where the target is not the
        // player's to change.
        public Dictionary<string, string> TargetSpriteSkins { get; set; } = new();

        // §135. The battle-window box the player drew by hand in Set Screen
        // Boundaries, or null when none is saved. Pixels of the captured
        // frame, not fractions - see ManualBattleBounds for why - and used by
        // BattleWindowLocator only when its automatic scan finds nothing.
        public ManualBattleBounds? ManualBattleBounds { get; set; }
    }
}
