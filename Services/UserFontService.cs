using Avalonia.Media;
using Avalonia.Media.Fonts;
using Serilog;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// §385. Fonts the user supplies as files, loaded at run time - no
    /// rebuild, no catalog line, no restart to see a new one.
    ///
    /// The files live in the app's own Fonts folder under
    /// LocalApplicationData, beside the appearance files. At start-up every
    /// .ttf/.otf there is read into one font collection registered with
    /// Avalonia's FontManager under the key <c>fonts:UserFonts</c>; a font
    /// added from the editor is copied in and read into the same live
    /// collection at once. A family is addressed as
    /// <c>fonts:UserFonts#Family Name</c>, with Inter as the fallback the
    /// way ThemeManager.BuildFontFamily already gives the bundled fonts.
    ///
    /// Avalonia 12's FontCollectionBase does the work: TryAddGlyphTypeface
    /// takes a stream and files the face under the family name the font
    /// itself declares, which is the name the user then sees in the
    /// dropdown - so a file called griffy-regular.ttf still shows as
    /// "Griffy". Checked against the Avalonia.Base.dll this project builds
    /// with.
    ///
    /// Removing a font deletes its file; the face already loaded stays in
    /// the collection until the app restarts, which is harmless - it just
    /// keeps working for the rest of the session.
    /// </summary>
    public static class UserFontService
    {
        public static readonly Uri CollectionKey = new("fonts:UserFonts");

        public static readonly IReadOnlyList<string> FontExtensions = new[] { ".ttf", ".otf" };

        public static string FontsFolder =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PRO Tracker & Database",
                "Fonts");

        private static readonly object Gate = new();
        private static UserFontCollection? collection;

        // family name -> the files that carry it (a family can be several
        // files: Regular, Bold, Italic), and the reverse for Remove.
        private static readonly SortedDictionary<string, List<string>> familyFiles =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every family loaded, in name order.</summary>
        public static IReadOnlyList<string> FamilyNames
        {
            get
            {
                lock (Gate)
                    return familyFiles.Keys.ToList();
            }
        }

        public static bool IsUserFont(string? familyName)
        {
            if (string.IsNullOrWhiteSpace(familyName))
                return false;

            lock (Gate)
                return familyFiles.ContainsKey(familyName.Trim());
        }

        public static bool IsFontPath(string path) =>
            FontExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        /// <summary>The FontFamily a user font resolves to: the collection's
        /// key, the family, and Inter behind it for any weight the file does
        /// not carry - see ThemeManager.BuildFontFamily for why the fallback.</summary>
        public static FontFamily BuildFontFamily(string familyName) =>
            new($"{CollectionKey}#{familyName.Trim()}, Inter");

        /// <summary>Called once at start-up, before ThemeManager.Apply, so a
        /// saved appearance naming a user font resolves on the first paint.
        /// Registers the collection even when the folder is empty, so a font
        /// added later in the session has somewhere to go.</summary>
        public static void Load()
        {
            lock (Gate)
            {
                if (collection is not null)
                    return;

                collection = new UserFontCollection();

                try
                {
                    FontManager.Current.AddFontCollection(collection);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "UserFontService could not register its font collection; user fonts are off this session");
                    collection = null;
                    return;
                }

                try
                {
                    if (!Directory.Exists(FontsFolder))
                        return;

                    foreach (string file in Directory.GetFiles(FontsFolder)
                                 .Where(IsFontPath)
                                 .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                    {
                        LoadFile(file);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "UserFontService could not read {Folder}", FontsFolder);
                }

                Log.Information("UserFontService loaded {Count} user font families: {Families}",
                    familyFiles.Count, string.Join(", ", familyFiles.Keys));
            }
        }

        /// <summary>Copies a font file into the Fonts folder (numbered if a
        /// different file already has its name, returned as-is if the same
        /// file is already there) and loads it into the live collection.
        /// Returns the family name the font declares, which is what the
        /// dropdown shows and the settings store.</summary>
        public static string Add(string sourcePath)
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("The selected font file no longer exists.", sourcePath);

            if (!IsFontPath(sourcePath))
                throw new InvalidOperationException("Only .ttf and .otf font files can be added.");

            lock (Gate)
            {
                if (collection is null)
                    throw new InvalidOperationException("User fonts could not be set up this session - see the log.");

                Directory.CreateDirectory(FontsFolder);

                string stem = Path.GetFileNameWithoutExtension(sourcePath);
                string extension = Path.GetExtension(sourcePath).ToLowerInvariant();

                if (string.IsNullOrWhiteSpace(stem))
                    stem = "font";

                string destination = Path.Combine(FontsFolder, stem + extension);
                int counter = 2;

                while (File.Exists(destination) && !AppearanceAssetLibrary.SameContent(destination, sourcePath))
                {
                    destination = Path.Combine(FontsFolder, $"{stem} ({counter}){extension}");
                    counter++;
                }

                if (!File.Exists(destination))
                    File.Copy(sourcePath, destination);

                string? family = LoadFile(destination);

                if (family is null)
                {
                    try { File.Delete(destination); } catch { /* the copy is the only thing to tidy */ }
                    throw new InvalidOperationException("That file is not a font Avalonia can read.");
                }

                return family;
            }
        }

        /// <summary>Deletes every file of a family. The face stays loaded
        /// until restart; the name leaves the dropdown now.</summary>
        public static bool Remove(string familyName)
        {
            lock (Gate)
            {
                if (!familyFiles.TryGetValue(familyName.Trim(), out List<string>? files))
                    return false;

                foreach (string file in files)
                {
                    try
                    {
                        if (File.Exists(file))
                            File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "UserFontService could not delete {File}", file);
                    }
                }

                familyFiles.Remove(familyName.Trim());
                return true;
            }
        }

        // Reads one file into the collection. The stream is a MemoryStream
        // the collection may keep hold of, so it is not disposed here.
        private static string? LoadFile(string file)
        {
            try
            {
                var stream = new MemoryStream(File.ReadAllBytes(file));

                if (collection is null || !collection.TryAddGlyphTypeface(stream, out GlyphTypeface glyphTypeface))
                {
                    Log.Warning("UserFontService could not read {File} as a font", file);
                    return null;
                }

                string family = glyphTypeface.FamilyName;

                if (string.IsNullOrWhiteSpace(family))
                    family = Path.GetFileNameWithoutExtension(file);

                if (!familyFiles.TryGetValue(family, out List<string>? files))
                    familyFiles[family] = files = new List<string>();

                if (!files.Contains(file, StringComparer.OrdinalIgnoreCase))
                    files.Add(file);

                return family;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "UserFontService could not load {File}", file);
                return null;
            }
        }

        /// <summary>The collection itself: FontCollectionBase does the
        /// loading, matching and fallback; all this adds is the key.</summary>
        private sealed class UserFontCollection : FontCollectionBase
        {
            public override Uri Key => CollectionKey;
        }
    }
}
