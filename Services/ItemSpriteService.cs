using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Serilog;

namespace Foot_Tracker.Services;

/// <summary>
/// §159. Item artwork from SharedPokemonLibrary/Assets/Items - the folder
/// the PokeAPI item catalog was dropped into. The folder mixes two naming
/// styles (the older hand-collected "Choice_Band.png" set and the
/// kebab-case "choice-band.png" catalog), so lookups go through a
/// normalized index (letters and digits only, case-insensitive) built once
/// on first use. Decoded bitmaps are cached, misses included, exactly like
/// PokemonSpriteService; everything is defensive - a missing folder or an
/// unreadable file means a null sprite, never a throw. §375: a file named
/// with the catalog's "--bag" variant suffix also answers to the name
/// without it - see BuildIndex.
/// </summary>
public static class ItemSpriteService
{
    static readonly object gate = new();
    static Dictionary<string, string>? index;
    static readonly Dictionary<string, Bitmap?> cache = new();

    static string Normalize(string name) =>
        Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]", "");

    static Dictionary<string, string> BuildIndex()
    {
        var built = new Dictionary<string, string>(StringComparer.Ordinal);

        string folder = Path.Combine(
            AppContext.BaseDirectory,
            "SharedPokemonLibrary",
            "Assets",
            "Items");

        try
        {
            if (Directory.Exists(folder))
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*.png"))
                {
                    string stem = Path.GetFileNameWithoutExtension(file);
                    string key = Normalize(stem);

                    if (key.Length > 0 && !built.ContainsKey(key))
                        built[key] = file;

                    // §375: the PokeAPI catalog draws each Z-Crystal twice
                    // and names the bag-sprite "waterium-z--bag.png", which
                    // normalizes to "wateriumzbag" and so never answered to
                    // "Waterium Z". The "--bag" suffix is a variant tag,
                    // not part of the name: index the file under the name
                    // without it as well, unless a plainer file already has
                    // that name.
                    if (stem.EndsWith("--bag", StringComparison.OrdinalIgnoreCase))
                    {
                        string bare = Normalize(stem.Substring(0, stem.Length - "--bag".Length));

                        if (bare.Length > 0 && !built.ContainsKey(bare))
                            built[bare] = file;
                    }
                }
            }
            else
            {
                Log.Warning("ItemSpriteService: item sprite folder not found at {Folder}.", folder);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ItemSpriteService: could not index the item sprites.");
        }

        return built;
    }

    /// <summary>The sprite for an item name or normalized id ("Choice
    /// Band", "choiceband" and "choice-band" all land on the same file),
    /// or null when the catalog has none.</summary>
    public static Bitmap? GetSprite(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
            return null;

        string key = Normalize(itemName);

        if (key.Length == 0)
            return null;

        lock (gate)
        {
            index ??= BuildIndex();

            if (cache.TryGetValue(key, out Bitmap? cached))
                return cached;

            Bitmap? bitmap = null;

            if (index.TryGetValue(key, out string? path))
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    bitmap = new Bitmap(stream);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "ItemSpriteService: could not load {Path}.", path);
                }
            }

            cache[key] = bitmap;
            return bitmap;
        }
    }
}
