using System;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// §278. Which way the front encounter table is being kept, for the
    /// running app.
    ///
    /// The persisted answer lives in UiPreferences.PerMapEncounterTable; this
    /// is the one place the RUNNING tracker asks, so a single flag is read by
    /// the view model and set by the settings window without either needing to
    /// know about the other. The same shape IsolatedSession (§249) gave the
    /// "which session is active" question, and for the same reason: one
    /// expression to change, rather than a preference object threaded through
    /// every caller.
    /// </summary>
    public static class TrackerSettings
    {
        /// <summary>False is the tracker as it has always worked, and is what
        /// an app that never loads a preference file gets.</summary>
        public static bool PerMapEncounterTable { get; private set; }

        /// <summary>§429. Whether encounter levels are shared with the events
        /// server - the player's opt-in, false until they tick it. Read by
        /// LevelShareService before it records anything.</summary>
        public static bool ShareLevelData { get; private set; }

        /// <summary>Raised when the mode changes, so the front table can
        /// rebuild from the other source at once rather than at the next
        /// encounter.</summary>
        public static event Action? Changed;

        /// <summary>Takes the mode from a loaded preference file at startup
        /// and on a client switch, and from the settings window when the
        /// player picks. Quiet when nothing actually changed.</summary>
        public static void Apply(bool perMap)
        {
            if (PerMapEncounterTable == perMap)
                return;

            PerMapEncounterTable = perMap;

            Changed?.Invoke();
        }

        /// <summary>§429. Its own method rather than a second parameter on
        /// Apply, so the two callers that apply the per-map choice at startup
        /// keep their one-argument shape and no caller can set one by
        /// accidentally passing the other.</summary>
        public static void ApplyLevelSharing(bool share)
        {
            if (ShareLevelData == share)
                return;

            ShareLevelData = share;

            Changed?.Invoke();
        }
    }
}
