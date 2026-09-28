using System.Text;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// Turns a PascalCase name into a human-readable label by inserting a
    /// space before each internal capital letter - "CommunityHunting"
    /// becomes "Community Hunting", "Giveaway" stays "Giveaway" (only one
    /// capital, nothing to split). Originally written for the Events board's
    /// event-type enum (gone in §253); used for the Boss Database's boss
    /// list (see BossListViewModel) and the simulator's boss opponents -
    /// boss IDs are plain filename-derived strings
    /// rather than enum members, but the same PascalCase-splitting still
    /// applies. Not tied to any one enum or source, so it's free to reuse
    /// again wherever a PascalCase name needs the same treatment.
    ///
    /// Simple on purpose: no handling for back-to-back capitals/acronyms
    /// (e.g. "PVPNight" stays "PVPNight", not "PVP Night") since nothing that
    /// uses this today needs that - revisit if that changes.
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
