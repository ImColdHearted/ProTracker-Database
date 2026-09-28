using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Serilog;

namespace Foot_Tracker.Services
{
    /// <summary>§409. One boss as the catalog lists it: the file's name (the
    /// id every window opens it by), its display name, where the file says
    /// it stands, its picture, cooldown and requirement. §415: and the other
    /// names it is known by, which a search finds it under.</summary>
    public sealed record BossInfo(
        string BossId,
        string Name,
        string Location,
        string NpcPicture,
        string CooldownText,
        string Requirements,
        IReadOnlyList<string> Difficulties,
        IReadOnlyList<string>? Aliases = null)
    {
        /// <summary>Whether a typed name is this boss - its name or any of
        /// its other names, case-insensitive.</summary>
        public bool IsCalled(string name) =>
            string.Equals(Name, name, StringComparison.OrdinalIgnoreCase)
            || (Aliases?.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    /// <summary>
    /// §409. The bosses in DataFiles/Bosses, read once: what the Map Boxes
    /// editor offers to pin, what the Maps window searches and shows a card
    /// for. The id is the FILE NAME without its extension, as the boss list
    /// (§260) settled: BossRepository opens "{id}.json", so the file name is
    /// the only id guaranteed to open. Fail-soft like every catalog here: a
    /// file that will not parse is skipped and logged.
    /// </summary>
    public static class BossCatalogService
    {
        private static readonly object Gate = new();

        private static List<BossInfo>? all;

        private static readonly Dictionary<string, Bitmap?> portraits = new(StringComparer.OrdinalIgnoreCase);

        private static readonly string Folder =
            Path.Combine(AppContext.BaseDirectory, "DataFiles", "Bosses");

        /// <summary>Every boss, by name.</summary>
        public static IReadOnlyList<BossInfo> All
        {
            get
            {
                lock (Gate)
                {
                    return all ??= Read();
                }
            }
        }

        public static BossInfo? Find(string? bossId) =>
            string.IsNullOrWhiteSpace(bossId)
                ? null
                : All.FirstOrDefault(b => string.Equals(b.BossId, bossId.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>A boss by its name or, §415, any other name it is known
        /// by - "Team Rocket", "Jessie", "James", "Jessie & James".</summary>
        public static BossInfo? FindByName(string? name) =>
            string.IsNullOrWhiteSpace(name)
                ? null
                : All.FirstOrDefault(b => b.IsCalled(name.Trim()));

        /// <summary>The boss's picture, from the path its file names; null
        /// when the file is not there. Cached, misses included.</summary>
        public static Bitmap? Portrait(string? bossId)
        {
            BossInfo? boss = Find(bossId);

            if (boss is null || boss.NpcPicture.Length == 0)
                return null;

            lock (Gate)
            {
                if (portraits.TryGetValue(boss.BossId, out Bitmap? cached))
                    return cached;

                Bitmap? bitmap = null;

                try
                {
                    string path = Path.Combine(AppContext.BaseDirectory, boss.NpcPicture.Replace('/', Path.DirectorySeparatorChar));

                    if (File.Exists(path))
                        bitmap = new Bitmap(path);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Bosses: the picture for {Boss} could not be read.", boss.Name);
                }

                portraits[boss.BossId] = bitmap;
                return bitmap;
            }
        }

        private static List<BossInfo> Read()
        {
            var bosses = new List<BossInfo>();

            if (!Directory.Exists(Folder))
                return bosses;

            foreach (string file in Directory.GetFiles(Folder, "*.json"))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                    JsonElement root = doc.RootElement;

                    string id = Path.GetFileNameWithoutExtension(file);
                    string name = Text(root, "name");

                    if (name.Length == 0)
                        continue;

                    string cooldown = Text(root, "cooldown");

                    if (cooldown.Length == 0 && TryNumber(root, "BossCooldown", out int hours) && hours > 0)
                        cooldown = hours % 24 == 0 ? $"{hours / 24} days" : $"{hours} hours";

                    string requirements = Text(root, "requirements");

                    if (requirements.Length == 0)
                        requirements = Text(root, "requirement");

                    var difficulties = new List<string>();

                    foreach (JsonProperty property in root.EnumerateObject())
                    {
                        if (string.Equals(property.Name, "difficulties", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object)
                        {
                            foreach (JsonProperty difficulty in property.Value.EnumerateObject())
                                difficulties.Add(difficulty.Name.Trim().ToLowerInvariant());
                        }
                    }

                    // §415: a pair whose file names neither of them - Jessie
                    // and James are "Team Rocket" in their file - is listed
                    // by the two names first, the file's name after, so the
                    // pin editor's list has them under J and a search for
                    // either finds them. A pair already named (Medusa &
                    // Eldir) is left as it is.
                    List<string> people = Pictured(root);
                    var aliases = new List<string>();

                    if (people.Count >= 2)
                    {
                        aliases.Add(string.Join(" & ", people));
                        aliases.Add(string.Join(" and ", people));
                        aliases.AddRange(people);

                        if (people.All(p => name.IndexOf(p, StringComparison.OrdinalIgnoreCase) < 0))
                        {
                            aliases.Add(name);
                            name = $"{string.Join(" & ", people)} ({name})";
                        }
                    }

                    bosses.Add(new BossInfo(
                        id,
                        name,
                        Text(root, "location"),
                        Text(root, "NPCPicture"),
                        cooldown,
                        requirements,
                        difficulties,
                        aliases.Distinct(StringComparer.OrdinalIgnoreCase)
                            .Where(a => !string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
                            .ToList()));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Bosses: {File} could not be read.", Path.GetFileName(file));
                }
            }

            bosses.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

            return bosses;
        }

        /// <summary>A string property by name, case-insensitively - the boss
        /// files spell their keys three ways between them.</summary>
        private static string Text(JsonElement root, string name)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                    return (property.Value.GetString() ?? string.Empty).Trim();
            }

            return string.Empty;
        }

        /// <summary>§415. The people a dual-boss file pictures - the keys of
        /// its npcPictures object, in the file's order; none for one boss.</summary>
        private static List<string> Pictured(JsonElement root)
        {
            var people = new List<string>();

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, "npcPictures", StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (JsonProperty person in property.Value.EnumerateObject())
                {
                    string who = person.Name.Trim();

                    if (who.Length > 0 && !people.Contains(who, StringComparer.OrdinalIgnoreCase))
                        people.Add(who);
                }
            }

            return people;
        }

        private static bool TryNumber(JsonElement root, string name, out int value)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out value))
                    return true;
            }

            value = 0;
            return false;
        }
    }
}
