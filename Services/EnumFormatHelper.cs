using System.Text;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// Turns a PascalCase enum member name into a human-readable label by
    /// inserting a space before each internal capital letter -
    /// "CommunityHunting" becomes "Community Hunting", "Giveaway" stays
    /// "Giveaway" (only one capital, nothing to split). Used for
    /// GuildEventType so far (see CreateEventWindow's Type ComboBox and
    /// EventsViewModel's card TypeLabel), but deliberately not tied to that
    /// one enum in case another multi-word enum needs the same treatment
    /// later.
    ///
    /// Simple on purpose: no handling for back-to-back capitals/acronyms
    /// (e.g. "PVPNight" stays "PVPNight", not "PVP Night") since nothing in
    /// this app names an enum value that way today - revisit if that changes.
    /// </summary>
    public static class EnumFormatHelper
    {
        public static string ToDisplayName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName))
                return rawName;

            var builder = new StringBuilder(rawName.Length + 4);

            for (int i = 0; i < rawName.Length; i++)
            {
                char c = rawName[i];

                if (i > 0 && char.IsUpper(c) && !char.IsUpper(rawName[i - 1]))
                    builder.Append(' ');

                builder.Append(c);
            }

            return builder.ToString();
        }
    }
}
