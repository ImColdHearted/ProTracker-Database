using System.ComponentModel;

namespace Foot_Tracker.Models
{
    /// <summary>
    /// One detected wild encounter in the CURRENT hunting session - species,
    /// the level LevelDetector read (null until/unless one is read), and the
    /// route/location RouteDetector's corner OCR last showed. Deliberately
    /// tiny: three fields of text-sized data and no sprite, bitmap, or other
    /// UI object, so appending one of these per encounter costs nothing on the
    /// path that shows the encounter's sprite (see MIGRATION_GUIDE.md §99's
    /// performance notes and MainWindowViewModel.RegisterEncounter).
    ///
    /// Distinct from Models.HuntLogEntry on purpose: HuntLogEntry is the
    /// persistent Catch Logs record (successful catches only, with gender/
    /// shiny/timestamp detail, its own Clear All button, and a lifetime
    /// independent of any hunt), while this record exists only for the current
    /// hunting session and is cleared by the main Reset action alongside the
    /// session's encounter counts. A caught Pokemon therefore appears in both
    /// places - once here because it was encountered, once there because it
    /// was caught - and that is intended, not a duplicate.
    ///
    /// Implements INotifyPropertyChanged (by hand - Models in this project
    /// don't take the CommunityToolkit dependency) because Level and Location
    /// can legitimately improve AFTER the record is created: the §96 level
    /// consensus can settle on a value mid-battle (see
    /// EncounterTracker.EncounterLevelRefined), and the corner OCR can deliver
    /// the route a tick after the encounter registered. Those refinements
    /// UPDATE this record in place - they must never append a second record
    /// for the same encounter - and the change notifications are what let an
    /// already-open history window show the corrected value automatically.
    /// </summary>
    public sealed class SessionEncounterRecord : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Session-scoped identifier, assigned once per DETECTED
        /// encounter by SessionEncounterHistoryService.Append - which is only
        /// ever called from the single per-encounter registration path
        /// (EncounterTracker fires EncounterDetected exactly once per battle,
        /// however many frames the detector examines), so one real encounter
        /// can never produce two records.</summary>
        public long Id { get; init; }

        /// <summary>The resolved species/form name - the exact same key the
        /// Session Encounters table aggregates by (see
        /// PokemonSpriteService.ResolveEncounterName and
        /// MainWindowViewModel.RegisterEncounter), so a form the main table
        /// counts as its own row gets its own history too.</summary>
        public string PokemonName { get; init; } = string.Empty;

        /// <summary>§128. When this encounter happened. The permanent log
        /// has always had one; the in-memory record did not, and the history
        /// window's Timestamp column needs both lists to answer the same
        /// question. Stored UTC, shown local - the log is read by the person
        /// who made it, and a hunt that crosses a daylight-saving boundary
        /// should not shift.
        ///
        /// Nullable because a session file written before this existed has no
        /// timestamps, and a restored record has to say "not recorded"
        /// rather than claim the moment it was loaded.</summary>
        public DateTime? WhenUtc { get; init; }

        private int? level;

        /// <summary>Null while no reliable reading exists - displayed as
        /// "Lv. ?" via LevelDisplay, never as a blank or a raw null.</summary>
        public int? Level
        {
            get => level;
            set
            {
                if (level == value)
                    return;

                level = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LevelDisplay)));
            }
        }

        private string location = "Unknown";

        /// <summary>The route/location text at the moment of the encounter -
        /// "Unknown" until the corner OCR has produced a reading.</summary>
        public string Location
        {
            get => location;
            set
            {
                string normalized = string.IsNullOrWhiteSpace(value) ? "Unknown" : value;

                if (location == normalized)
                    return;

                location = normalized;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocationDisplay)));
            }
        }

        private string? gender;

        /// <summary>§124. Null until GenderDetector produces a reading -
        /// shown blank, never as the word Unknown. Same mutable-with-
        /// notification shape as Level and Location above, because it
        /// arrives after the row does for exactly the same reason.</summary>
        public string? Gender
        {
            get => gender;
            set
            {
                if (gender == value)
                    return;

                gender = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GenderDisplay)));
            }
        }

        private string? rareType;

        /// <summary>§124. "Shiny" or "Form", null for an ordinary
        /// encounter. "None" is normalized away on the way in so the display
        /// never has to know about it.</summary>
        public string? RareType
        {
            get => rareType;
            set
            {
                string? normalized =
                    string.IsNullOrWhiteSpace(value) || value == "None" ? null : value;

                if (rareType == normalized)
                    return;

                rareType = normalized;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RareDisplay)));
            }
        }

        public string LevelDisplay => Level is int value ? $"Lv. {value}" : "Lv. ?";

        public string LocationDisplay => location;

        /// <summary>One letter, so a long history list stays readable at a
        /// glance - and empty rather than "Unknown" when nothing was read.</summary>
        public string GenderDisplay => gender switch
        {
            "Male" => "M",
            "Female" => "F",
            "Genderless" => "-",
            _ => string.Empty
        };

        public string RareDisplay => rareType ?? string.Empty;

        /// <summary>Matches EncounterLogRow.WhenDisplay so the two modes of
        /// the history window read identically, and blank rather than a
        /// placeholder date when there is nothing to show.</summary>
        public string WhenDisplay =>
            WhenUtc is DateTime when
                ? when.ToLocalTime().ToString("d MMM yyyy  HH:mm")
                : string.Empty;
    }

    /// <summary>
    /// The on-disk shape of one SessionEncounterRecord - see
    /// SessionEncounterHistoryService's save/load. A separate plain DTO
    /// (rather than serializing the record itself) keeps the saved file down
    /// to exactly the three meaningful fields: Id is regenerated on load (it
    /// only exists to make records distinct within one process run) and the
    /// display strings are always derived.
    /// </summary>
    public sealed class SessionEncounterRecordData
    {
        public string PokemonName { get; set; } = string.Empty;

        public int? Level { get; set; }

        public string Location { get; set; } = "Unknown";

        // §128. Nullable for the same reason the three dictionaries in
        // HuntSessionSaveData are (§123): a file written before this
        // existed simply has no such property, and System.Text.Json leaves a
        // missing one at its default. Null makes "an old file" visibly
        // different from "recorded at the epoch".
        public DateTime? WhenUtc { get; set; }

        public string? Gender { get; set; }

        public string? RareType { get; set; }
    }
}
