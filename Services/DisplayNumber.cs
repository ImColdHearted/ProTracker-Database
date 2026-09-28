using System.Globalization;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// §279. One rule for showing a counted number: grouped by thousands, in
    /// the reader's own culture. 10283 reads as 10,283.
    ///
    /// It exists because the app had three spellings of this and a fourth that
    /// was no spelling at all. The World Quest figures used "#,0", the boss
    /// rewards and the encounter-log total used ":N0", and every other counter
    /// in the program - total encounters, Since Shiny, the lifetime stats, the
    /// front table's own columns - used a bare ToString() and ran the digits
    /// together. A hunt reaching five figures is exactly when a counter most
    /// needs to be readable, and that was the one place the grouping was
    /// missing.
    ///
    /// CurrentCulture, not Invariant: this is what a person reads, and a
    /// reader whose locale groups with spaces or dots should see their own
    /// separator. Every place that writes a number for a MACHINE - the CSV and
    /// JSON exports, the save files, the OCR parsers - keeps
    /// InvariantCulture and is deliberately untouched by this.
    ///
    /// WHAT IS NOT A COUNT. Page numbers ("Page 1 of 15") are ordinals, not
    /// quantities. Levels, IVs, EVs and anything typed into a box that is
    /// parsed back are values, and a comma in one of those is a parse failure
    /// waiting to happen. Percentages carry their own formatting. None of
    /// those go through here.
    /// </summary>
    public static class DisplayNumber
    {
        /// <summary>The format itself, named once. "#,0" rather than "N0"
        /// only because it is the spelling already in this codebase; for
        /// whole numbers the two are identical.</summary>
        private const string GroupedFormat = "#,0";

        public static string Count(int value) =>
            value.ToString(GroupedFormat, CultureInfo.CurrentCulture);

        public static string Count(long value) =>
            value.ToString(GroupedFormat, CultureInfo.CurrentCulture);
    }
}
