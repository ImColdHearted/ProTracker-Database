using System.Text.Json;
using Foot_Tracker.Models;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// Looks up a move's Type/Power/Effect by name for display in the boss
    /// detail panel - see BossDetailViewModel's BossTeamMemberItem.MoveDisplayItems.
    /// Reads SharedPokemonLibrary/Data/Moves/moves.json once, the first time a
    /// lookup is requested, and caches it - no explicit startup wiring needed
    /// (unlike CounterpartSpriteService.Load(), which a caller has to remember
    /// to invoke).
    /// </summary>
    public static class MoveLookupService
    {
        private static readonly Dictionary<string, MoveData> movesByName =
            new(StringComparer.OrdinalIgnoreCase);

        private static bool loaded;

        /// <summary>Returns null if moves.json is missing, unreadable, or has no
        /// entry matching this name - callers should fall back to showing the
        /// plain move name in that case rather than failing.</summary>
        public static MoveData? Find(string? moveName)
        {
            if (string.IsNullOrWhiteSpace(moveName))
                return null;

            EnsureLoaded();

            string trimmed = moveName.Trim();

            if (movesByName.TryGetValue(trimmed, out MoveData? move))
                return move;

            // Boss files write Hidden Power's rolled type in parentheses, e.g.
            // "Hidden Power (Fire)" / "Hidden Power (Ice)" - see MIGRATION_GUIDE.md
            // §46. moves.json only carries one generic "Hidden Power" row (Normal /
            // 60 / "Type and power depends on user's IVs."), the same row every
            // typed variant should show, so falling back to it here means every
            // "Hidden Power (X)" gets full detail without moves.json needing a
            // near-duplicate row per possible type.
            if (trimmed.StartsWith("Hidden Power (", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith(')'))
                return movesByName.TryGetValue("Hidden Power", out MoveData? baseMove) ? baseMove : null;

            return null;
        }

        private static void EnsureLoaded()
        {
            if (loaded)
                return;

            loaded = true;

            string path = Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Data",
                "Moves",
                "moves.json"
            );

            if (!File.Exists(path))
                return;

            string json = File.ReadAllText(path);

            List<MoveData>? data = JsonSerializer.Deserialize<List<MoveData>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (data == null)
                return;

            foreach (MoveData move in data)
            {
                if (string.IsNullOrWhiteSpace(move.Name))
                    continue;

                // moves.json has no duplicate names (checked when it was built) -
                // a plain assignment is fine even if that ever changes.
                movesByName[move.Name] = move;
            }
        }
    }
}
