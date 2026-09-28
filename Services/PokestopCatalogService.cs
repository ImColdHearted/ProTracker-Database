using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Foot_Tracker.Models;
using Serilog;

namespace Foot_Tracker.Services
{
    /// <summary>§419. One Pokéstop as the catalog lists it: the pin id it is
    /// published under, its region and where it stands.</summary>
    public sealed record PokestopInfo(string Id, string Region, string Name);

    /// <summary>§419. One reward a Pokéstop can give, with how many.</summary>
    public sealed record PokestopReward(string Item, int Min, int Max)
    {
        public string QuantityText => Min == Max ? Min.ToString() : $"{Min}-{Max}";
    }

    /// <summary>
    /// §419. The Pokéstops - the NPCs that hand out two random items and
    /// refresh after two real days - read from DataFiles/Pokestops/
    /// pokestops.json: the stops the PRO wiki lists, the reward table (with
    /// the Exp. Candy amounts as they are in play), the cooldown and each
    /// region's requirement.
    ///
    /// A placed Pokéstop is a boss pin (§409) whose id begins "Pokestop":
    /// the Worker keeps it in the same table, under the same master-token
    /// routes, so placing Pokéstops needs no new server. Everything that
    /// draws or names a pin asks here first - <see cref="IsPokestop"/>,
    /// <see cref="LabelFor"/>, <see cref="PortraitFor"/> - and falls back
    /// to the boss catalog otherwise.
    /// </summary>
    public static class PokestopCatalogService
    {
        public const string IdPrefix = "Pokestop";

        public const string DisplayName = "Pokéstop";

        private static readonly object Gate = new();

        private static bool loaded;

        private static List<PokestopInfo> stops = new();

        private static List<PokestopReward> rewards = new();

        private static Dictionary<string, string> requirements = new(StringComparer.OrdinalIgnoreCase);

        private static string picture = "SharedPokemonLibrary/Assets/Bosses/pokestop.png";

        private static int cooldownHours = 48;

        private static int itemsPerVisit = 2;

        private static bool portraitRead;

        private static Bitmap? portrait;

        public static IReadOnlyList<PokestopInfo> All
        {
            get { EnsureLoaded(); return stops; }
        }

        public static IReadOnlyList<PokestopReward> Rewards
        {
            get { EnsureLoaded(); return rewards; }
        }

        public static int ItemsPerVisit
        {
            get { EnsureLoaded(); return itemsPerVisit; }
        }

        /// <summary>"2 days (real time)".</summary>
        public static string CooldownText
        {
            get
            {
                EnsureLoaded();
                return cooldownHours % 24 == 0 ? $"{cooldownHours / 24} days (real time)" : $"{cooldownHours} hours (real time)";
            }
        }

        /// <summary>What a region's stops ask of the player; "-" when the
        /// file says nothing for it.</summary>
        public static string RequirementFor(string? region)
        {
            EnsureLoaded();
            return region is not null && requirements.TryGetValue(region, out string? text) ? text : "-";
        }

        /// <summary>Whether a pin id is a Pokéstop's.</summary>
        public static bool IsPokestop(string? id) =>
            id is not null && SpawnMap.KeyFor(id).StartsWith("pokestop", StringComparison.Ordinal);

        public static bool IsPokestop(BossPin? pin) => IsPokestop(pin?.BossId);

        /// <summary>§420. A new stop's pin id: "Pokestop" and twelve hex
        /// digits, its own whatever it is named - so any number can be
        /// placed, two may share a name, and renaming one keeps its pin.</summary>
        public static string NewId() => IdPrefix + Guid.NewGuid().ToString("N")[..12];

        /// <summary>The catalog's stop for a pin id; null for a stop typed
        /// in the editor that the file does not list.</summary>
        public static PokestopInfo? Find(string? id)
        {
            EnsureLoaded();
            string key = SpawnMap.KeyFor(id);
            return stops.FirstOrDefault(s => SpawnMap.KeyFor(s.Id) == key);
        }

        /// <summary>Where a placed stop stands: the name it was saved with,
        /// without the "Pokéstop - " in front (§420: the name is the admin's,
        /// the id says nothing); the catalog's name for a pin that has none.</summary>
        public static string PlaceOf(BossPin pin)
        {
            string name = pin.Boss.Trim();
            string lead = DisplayName + " - ";

            if (name.StartsWith(lead, StringComparison.OrdinalIgnoreCase))
                name = name[lead.Length..].Trim();

            if (name.Length == 0 || string.Equals(name, DisplayName, StringComparison.OrdinalIgnoreCase))
                return Find(pin.BossId)?.Name ?? DisplayName;

            return name;
        }

        /// <summary>§420. The catalog's stop a pin is - by its id when it was
        /// placed under one, else by the name it was saved with; null for a
        /// place the catalog does not list.</summary>
        public static PokestopInfo? FindFor(BossPin pin)
        {
            if (Find(pin.BossId) is PokestopInfo byId)
                return byId;

            string place = PlaceOf(pin);
            return All.FirstOrDefault(s => string.Equals(s.Name, place, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The name a pin is published with: "Pokéstop - {place}",
        /// within the server's 60 characters.</summary>
        public static string PinNameFor(string place)
        {
            string name = $"{DisplayName} - {place.Trim()}";
            return name.Length > 60 ? name[..60] : name;
        }

        /// <summary>§419. A pin's label wherever pins are drawn: a boss's
        /// catalog name, a Pokéstop's place, else the pin's own name.</summary>
        public static string LabelFor(BossPin pin) =>
            IsPokestop(pin)
                ? $"{DisplayName} - {PlaceOf(pin)}"
                : BossCatalogService.Find(pin.BossId)?.Name ?? pin.Boss;

        /// <summary>§419. A pin's picture: the Pokéstop sprite for a stop,
        /// the boss's for a boss.</summary>
        public static Bitmap? PortraitFor(BossPin pin) =>
            IsPokestop(pin) ? Portrait() : BossCatalogService.Portrait(pin.BossId);

        /// <summary>The Pokéstop sprite, from the path the file names;
        /// cached, a miss included.</summary>
        public static Bitmap? Portrait()
        {
            EnsureLoaded();

            lock (Gate)
            {
                if (portraitRead)
                    return portrait;

                portraitRead = true;

                try
                {
                    string path = Path.Combine(AppContext.BaseDirectory, picture.Replace('/', Path.DirectorySeparatorChar));

                    if (File.Exists(path))
                        portrait = new Bitmap(path);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Pokéstops: the picture could not be read.");
                }

                return portrait;
            }
        }

        private static void EnsureLoaded()
        {
            lock (Gate)
            {
                if (loaded)
                    return;

                loaded = true;

                string path = Path.Combine(AppContext.BaseDirectory, "DataFiles", "Pokestops", "pokestops.json");

                try
                {
                    if (!File.Exists(path))
                        return;

                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                    JsonElement root = doc.RootElement;

                    if (root.TryGetProperty("picture", out JsonElement pic) && pic.ValueKind == JsonValueKind.String)
                        picture = pic.GetString() ?? picture;

                    if (root.TryGetProperty("cooldownHours", out JsonElement hours) && hours.TryGetInt32(out int h) && h > 0)
                        cooldownHours = h;

                    if (root.TryGetProperty("itemsPerVisit", out JsonElement per) && per.TryGetInt32(out int n) && n > 0)
                        itemsPerVisit = n;

                    if (root.TryGetProperty("requirements", out JsonElement req) && req.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty region in req.EnumerateObject())
                            requirements[region.Name] = region.Value.GetString() ?? string.Empty;
                    }

                    if (root.TryGetProperty("rewards", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement r in list.EnumerateArray())
                        {
                            string item = r.TryGetProperty("item", out JsonElement i) ? i.GetString() ?? string.Empty : string.Empty;
                            int min = r.TryGetProperty("min", out JsonElement a) && a.TryGetInt32(out int av) ? av : 1;
                            int max = r.TryGetProperty("max", out JsonElement b) && b.TryGetInt32(out int bv) ? bv : min;

                            if (item.Length > 0)
                                rewards.Add(new PokestopReward(item, min, Math.Max(min, max)));
                        }
                    }

                    if (root.TryGetProperty("stops", out JsonElement all) && all.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement s in all.EnumerateArray())
                        {
                            string name = s.TryGetProperty("name", out JsonElement nm) ? nm.GetString() ?? string.Empty : string.Empty;
                            string region = s.TryGetProperty("region", out JsonElement rg) ? rg.GetString() ?? string.Empty : string.Empty;
                            string id = s.TryGetProperty("id", out JsonElement idv) ? idv.GetString() ?? string.Empty : string.Empty;

                            if (name.Length == 0)
                                continue;

                            if (!IsPokestop(id))
                                id = IdPrefix + string.Concat(Regex.Matches(name, "[A-Za-z0-9]+").Select(m => char.ToUpperInvariant(m.Value[0]) + m.Value[1..]));

                            stops.Add(new PokestopInfo(id, region, name));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Pokéstops: {File} could not be read.", path);
                }
            }
        }
    }
}
