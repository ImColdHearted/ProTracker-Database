using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using PokemonSim.Models;
using Serilog;

namespace Foot_Tracker.Services.Simulator
{
    /// <summary>§154. Where the Simulator gets its artwork. Behind an
    /// interface so the user can swap in dedicated battle art later without
    /// touching the battle view model.</summary>
    public interface ISimulatorSpriteProvider
    {
        /// <summary>The sprite for a species name, or null when there is
        /// none (the view shows its clean placeholder). §164: shiny asks
        /// for the shiny artwork, falling back to the ordinary art when
        /// the library has no shiny for this name. Never throws; safe off
        /// the UI thread.</summary>
        Task<Bitmap?> GetSpriteAsync(string speciesName, bool shiny = false);

        /// <summary>§199. The picture at an explicit path, for a Pokemon
        /// whose slot named one - see PokemonState.SpritePath. Null when
        /// the path is empty or the file will not read, which is what
        /// sends the caller back to the species artwork. Never throws;
        /// safe off the UI thread.</summary>
        Task<Bitmap?> GetSpriteFromPathAsync(string? path);

        /// <summary>§200. The picture for an exact national-dex id - the
        /// only way to ask for one form of a species rather than whichever
        /// one its name resolves to. Null for 0, or for an id the library
        /// has no file for, which sends the caller on to the name. Never
        /// throws; safe off the UI thread.</summary>
        Task<Bitmap?> GetSpriteByDexAsync(int dexNumber);

        /// <summary>§201. The artwork for the battle SCENE: seen from
        /// behind when this is the player's side, and cropped to its opaque
        /// box so the bottom of the bitmap is the Pokemon's feet. Null when
        /// the library has nothing at all for it. Never throws; safe off the
        /// UI thread. See BattleSpriteService for the rungs it falls
        /// through.</summary>
        Task<Bitmap?> GetBattleSpriteAsync(PokemonState pokemon, bool back);
    }

    /// <summary>
    /// The phase-one implementation: the tracker's own encounter sprites,
    /// through PokemonSpriteService - which already resolves form names by
    /// the project's rules (ResolveEncounterName + the forms table), uses
    /// case-correct paths for Linux/macOS, preserves the PNGs' transparency,
    /// and caches decoded bitmaps (misses included) so nothing rescans the
    /// asset folder per turn. The first decode of a sprite is pushed off the
    /// caller's thread; repeats are cache hits. A tiny name-keyed task cache
    /// on top means each species is only ever decoded once per run even if
    /// two views ask at the same moment.
    /// </summary>
    public sealed class TrackerSpriteProvider : ISimulatorSpriteProvider
    {
        private readonly Dictionary<string, Task<Bitmap?>> inFlight = new(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new();

        public Task<Bitmap?> GetSpriteAsync(string speciesName, bool shiny = false)
        {
            if (string.IsNullOrWhiteSpace(speciesName))
                return Task.FromResult<Bitmap?>(null);

            // §164: shiny and ordinary art cache separately.
            string key = shiny ? "shiny|" + speciesName : speciesName;

            lock (gate)
            {
                if (inFlight.TryGetValue(key, out Task<Bitmap?>? existing))
                    return existing;

                Task<Bitmap?> task = Task.Run(() =>
                {
                    try
                    {
                        // §164: the shiny lookup falls back to the ordinary
                        // art internally (§139) - null only means the NAME
                        // missed, which is where the §161 mega bridge
                        // comes in.
                        Bitmap? sprite = shiny
                            ? PokemonSpriteService.GetShinyEncounterSprite(speciesName)
                            : PokemonSpriteService.GetEncounterSprite(speciesName);

                        if (sprite != null)
                            return sprite;

                        // §161: the engine names mega formes "Mega X" (its
                        // pokedex spelling); the forms table names them
                        // "X-Mega" / "X-Mega-Y". Bridge on a miss so the
                        // battle view shows the mega artwork.
                        if (!TryMegaFormName(speciesName, out string formName))
                            return null;

                        return shiny
                            ? PokemonSpriteService.GetShinyEncounterSprite(formName)
                            : PokemonSpriteService.GetEncounterSprite(formName);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Simulator: sprite for {Species} could not be loaded.", speciesName);
                        return null;
                    }
                });

                inFlight[key] = task;
                return task;
            }
        }

        /// <summary>§201. The scene's artwork, cached by the file it
        /// resolved to rather than by the Pokemon - two slots of the same
        /// species drawn the same way share one read, and the front and the
        /// back of one Pokemon are simply two different files.</summary>
        public Task<Bitmap?> GetBattleSpriteAsync(PokemonState pokemon, bool back)
        {
            string? path = null;

            try
            {
                path = BattleSpriteService.ResolvePath(pokemon, back);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Simulator: could not work out which sprite {Species} should use.", pokemon.Species);
            }

            if (string.IsNullOrEmpty(path))
                return Task.FromResult<Bitmap?>(null);

            string key = "battle|" + path;

            lock (gate)
            {
                if (inFlight.TryGetValue(key, out Task<Bitmap?>? existing))
                    return existing;

                Task<Bitmap?> task = Task.Run<Bitmap?>(() => BattleSpriteService.LoadTrimmed(path));

                inFlight[key] = task;
                return task;
            }
        }

        /// <summary>§200. An exact form by its dex id, cached by id.
        /// Shiny is deliberately not a parameter: the library's shiny folder
        /// is keyed by the same ids, but a boss's dexNumber says WHICH FORM
        /// and its shininess is a separate fact, so the shiny lookup stays
        /// where it was - on the name path below.</summary>
        public Task<Bitmap?> GetSpriteByDexAsync(int dexNumber)
        {
            if (dexNumber <= 0)
                return Task.FromResult<Bitmap?>(null);

            string key = "dex|" + dexNumber;

            lock (gate)
            {
                if (inFlight.TryGetValue(key, out Task<Bitmap?>? existing))
                    return existing;

                Task<Bitmap?> task = Task.Run<Bitmap?>(() =>
                {
                    try
                    {
                        return PokemonSpriteService.GetSpriteByDexNumber(dexNumber);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Simulator: the sprite for dex {Dex} could not be loaded.", dexNumber);
                        return null;
                    }
                });

                inFlight[key] = task;
                return task;
            }
        }

        /// <summary>§199. An explicit picture, cached by path. It goes
        /// through the same §139 card-sized read the encounter cards use,
        /// so a counterpart already on screen elsewhere costs nothing here
        /// and the artwork lands on the battle view at the size a species
        /// sprite would - the counterpart files are 120x120 against the
        /// library's 96x96, and left alone they would draw a fifth
        /// larger.</summary>
        public Task<Bitmap?> GetSpriteFromPathAsync(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return Task.FromResult<Bitmap?>(null);

            string key = "path|" + path;

            lock (gate)
            {
                if (inFlight.TryGetValue(key, out Task<Bitmap?>? existing))
                    return existing;

                Task<Bitmap?> task = Task.Run<Bitmap?>(() =>
                {
                    try
                    {
                        return CounterpartSpriteService.GetCardSprite(path);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Simulator: the sprite at {Path} could not be loaded.", path);
                        return null;
                    }
                });

                inFlight[key] = task;
                return task;
            }
        }

        /// <summary>"Mega Garchomp" -> "Garchomp-Mega"; "Mega Charizard X"
        /// -> "Charizard-Mega-X". False for anything that is not a "Mega "
        /// name.</summary>
        internal static bool TryMegaFormName(string speciesName, out string formName)
        {
            formName = string.Empty;

            const string prefix = "Mega ";

            if (!speciesName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            string rest = speciesName.Substring(prefix.Length).Trim();

            if (rest.Length == 0)
                return false;

            // A trailing single-letter variant (X / Y) moves behind Mega.
            int lastSpace = rest.LastIndexOf(' ');

            if (lastSpace > 0 && rest.Length - lastSpace == 2)
            {
                formName = $"{rest.Substring(0, lastSpace)}-Mega-{rest.Substring(lastSpace + 1)}";
                return true;
            }

            formName = $"{rest}-Mega";
            return true;
        }
    }
}
