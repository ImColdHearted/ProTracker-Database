using System.Collections.Generic;
using System.Linq;

namespace Foot_Tracker.Models
{
    /// <summary>
    /// §207. The counterpart events the game is currently running, as the
    /// admin published them - what every tracker reads to know that a form
    /// encounter right now is far more likely to be a Summer one than a
    /// Valentines one.
    ///
    /// Empty is a real and important answer: it means nothing has been
    /// published, and the tracker then behaves exactly as it did before this
    /// existed, considering every event skin. Nobody who never opens the
    /// admin console is affected by any of this.
    /// </summary>
    public sealed class ActiveEvents
    {
        /// <summary>The most the server will store, and the most the console
        /// offers. §219 raised it from three to six; the server enforces it
        /// so a hand-made request cannot publish thirty.
        ///
        /// This number lives in TWO places that have to agree - here, and
        /// MAX_ACTIVE_EVENTS in Backend/EventsWorker/worker.js. The worker is
        /// the one that actually decides, because it truncates before it
        /// stores. Raising it here alone would let the console offer six
        /// slots and publish six, and the server would silently keep the
        /// first three.</summary>
        public const int MaxActive = 6;

        public static readonly ActiveEvents None = new(new List<string>(), null, string.Empty);

        public ActiveEvents(IEnumerable<string> events, string? updatedUtc, string updatedBy)
        {
            Events = events
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim())
                .Distinct(System.StringComparer.OrdinalIgnoreCase)
                .Take(MaxActive)
                .ToList();

            UpdatedUtc = updatedUtc;
            UpdatedBy = updatedBy;
        }

        public IReadOnlyList<string> Events { get; }

        public string? UpdatedUtc { get; }

        public string UpdatedBy { get; }

        public bool Any => Events.Count > 0;

        /// <summary>Whether this event name is one of the running ones. The
        /// counterpart catalog's own spelling is what both sides use, so the
        /// comparison is by name and case-insensitive.</summary>
        public bool Includes(string? eventName) =>
            !string.IsNullOrWhiteSpace(eventName) &&
            Events.Any(e => string.Equals(e, eventName.Trim(), System.StringComparison.OrdinalIgnoreCase));

        /// <summary>Whether this event may still be considered. TRUE when
        /// nothing is published, because an empty list means "no narrowing"
        /// rather than "nothing is allowed" - that distinction is the whole
        /// reason an untouched install is unaffected by §207.
        ///
        /// An instance method rather than a static on the service so a
        /// caller weighing several events weighs them all against ONE
        /// snapshot; a static that re-read the service per call could
        /// straddle a refresh mid-loop and mix two published lists.</summary>
        public bool Allows(string? eventName) => !Any || Includes(eventName);
    }
}
