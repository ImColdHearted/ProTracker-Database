using Avalonia.Media.Imaging;
using Foot_Tracker.Models;
using Serilog;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// §381. The pictures the Appearance editor's asset library offers for the
    /// window background.
    ///
    /// Before this the app kept exactly ONE custom background per client -
    /// SaveCustomBackground copies the chosen file to
    /// custom-background-clientN.ext and deletes the previous one - so a user
    /// who tried three pictures had to find the first two again on disk. The
    /// library keeps every picture the user imports, in a Library folder
    /// beside those files, under its own name. Applying a picture still goes
    /// through SaveCustomBackground exactly as before, so the settings file,
    /// the per-client copy, the animated-GIF path and the community share all
    /// see what they always saw; the library is purely additive.
    ///
    /// Nothing here lives in a temporary directory: the folder is the same
    /// LocalApplicationData one the applied backgrounds already use, which is
    /// how the library survives a restart and an update.
    /// </summary>
    public static class AppearanceAssetLibrary
    {
        public const string BuiltInName = "Slate (built-in)";

        public static readonly IReadOnlyList<string> ImageExtensions =
            new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif" };

        public static string LibraryFolder =>
            Path.Combine(AppearanceSettingsRepository.CustomBackgroundFolder, "Library");

        /// <summary>Thumbnails are decoded at this width, not at the picture's
        /// own size: a library of ten 4K wallpapers decoded whole would cost
        /// hundreds of megabytes for a row of 132px cards.</summary>
        public const int ThumbnailWidth = 264;

        /// <summary>The built-in background first, then every picture in the
        /// library by name. A file that has stopped being an image (renamed,
        /// truncated) is listed without a thumbnail rather than breaking the
        /// list.</summary>
        public static IReadOnlyList<AppearanceAsset> List()
        {
            var assets = new List<AppearanceAsset>();

            string builtIn = ThemeManager.GetBackgroundPath(AppearanceSettings.DefaultBackgroundId);

            if (File.Exists(builtIn))
                assets.Add(new AppearanceAsset(BuiltInName, builtIn, IsBuiltIn: true));

            try
            {
                if (Directory.Exists(LibraryFolder))
                {
                    foreach (string file in Directory.GetFiles(LibraryFolder)
                                 .Where(IsImagePath)
                                 .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                    {
                        assets.Add(new AppearanceAsset(Path.GetFileName(file), file, IsBuiltIn: false));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "AppearanceAssetLibrary could not list {Folder}", LibraryFolder);
            }

            return assets;
        }

        public static bool IsImagePath(string path) =>
            ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        /// <summary>Copies a picture into the library under its own file name,
        /// numbered if that name is taken by a DIFFERENT file - importing the
        /// same picture twice returns the copy already there rather than a
        /// duplicate. A file already inside the library is returned as-is.</summary>
        public static AppearanceAsset Import(string sourcePath)
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("The selected image no longer exists.", sourcePath);

            if (!IsImagePath(sourcePath))
                throw new InvalidOperationException("Only .png, .jpg, .jpeg, .bmp, .webp and .gif pictures can be added.");

            Directory.CreateDirectory(LibraryFolder);

            string fullSource = Path.GetFullPath(sourcePath);

            if (IsInsideLibrary(fullSource))
                return new AppearanceAsset(Path.GetFileName(fullSource), fullSource, IsBuiltIn: false);

            string stem = Path.GetFileNameWithoutExtension(sourcePath);
            string extension = Path.GetExtension(sourcePath).ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(stem))
                stem = "background";

            string destination = Path.Combine(LibraryFolder, stem + extension);
            int counter = 2;

            while (File.Exists(destination) && !SameContent(destination, fullSource))
            {
                destination = Path.Combine(LibraryFolder, $"{stem} ({counter}){extension}");
                counter++;
            }

            if (!File.Exists(destination))
                File.Copy(fullSource, destination);

            return new AppearanceAsset(Path.GetFileName(destination), destination, IsBuiltIn: false);
        }

        /// <summary>Deletes a library picture. The built-in background and
        /// anything outside the library are refused, so a bug can never reach
        /// past the folder this class owns. The per-client APPLIED copy is a
        /// separate file and is not touched: a background in use stays in use.</summary>
        public static bool Remove(AppearanceAsset asset)
        {
            if (asset.IsBuiltIn)
                return false;

            string full = Path.GetFullPath(asset.Path);

            if (!IsInsideLibrary(full) || !File.Exists(full))
                return false;

            File.Delete(full);
            return true;
        }

        public static bool IsInsideLibrary(string path)
        {
            string folder = Path.GetFullPath(LibraryFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            return Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A small bitmap for a card, or null when the file cannot be
        /// decoded - the card then shows its name over a blank tile, which is
        /// better than a library that will not open because one picture is
        /// broken.</summary>
        public static Bitmap? LoadThumbnail(string path)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, ThumbnailWidth);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "AppearanceAssetLibrary could not read a thumbnail for {Path}", path);
                return null;
            }
        }

        /// <summary>§386. A picture's size in pixels from its header - PNG,
        /// GIF, BMP, JPEG and WebP are read without decoding, anything else
        /// by decoding it - so the editor can say "1920 × 1080" beside a
        /// name. False when the file is not a picture it can read.</summary>
        public static bool TryReadImageSize(string path, out int width, out int height)
        {
            width = 0;
            height = 0;

            try
            {
                using FileStream stream = File.OpenRead(path);
                var header = new byte[32];
                int read = stream.Read(header, 0, header.Length);

                if (read >= 24 && header[0] == 0x89 && header[1] == 'P' && header[2] == 'N' && header[3] == 'G')
                {
                    width = BigEndian(header, 16);
                    height = BigEndian(header, 20);
                }
                else if (read >= 10 && header[0] == 'G' && header[1] == 'I' && header[2] == 'F')
                {
                    width = header[6] | (header[7] << 8);
                    height = header[8] | (header[9] << 8);
                }
                else if (read >= 26 && header[0] == 'B' && header[1] == 'M')
                {
                    width = header[18] | (header[19] << 8) | (header[20] << 16) | (header[21] << 24);
                    height = Math.Abs(header[22] | (header[23] << 8) | (header[24] << 16) | (header[25] << 24));
                }
                else if (read >= 3 && header[0] == 0xFF && header[1] == 0xD8)
                {
                    stream.Position = 2;
                    (width, height) = ReadJpegSize(stream);
                }
                else if (read >= 30 && header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F' &&
                         header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P')
                {
                    (width, height) = ReadWebPSize(header);
                }

                if (width > 0 && height > 0)
                    return true;

                // Something else, or a header this does not know: decode it.
                stream.Position = 0;
                using var bitmap = new Bitmap(stream);
                width = bitmap.PixelSize.Width;
                height = bitmap.PixelSize.Height;
                return width > 0 && height > 0;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "AppearanceAssetLibrary could not read the size of {Path}", path);
                width = 0;
                height = 0;
                return false;
            }
        }

        private static int BigEndian(byte[] bytes, int offset) =>
            (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];

        // JPEG: walk the markers to the first start-of-frame, which carries
        // the height then the width, big-endian, after a precision byte.
        private static (int Width, int Height) ReadJpegSize(Stream stream)
        {
            var two = new byte[2];
            var frame = new byte[7];

            while (stream.Position < stream.Length)
            {
                int marker = stream.ReadByte();

                if (marker != 0xFF)
                    continue;

                int kind = stream.ReadByte();

                while (kind == 0xFF)
                    kind = stream.ReadByte();

                if (kind < 0)
                    break;

                if (kind is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
                    continue;

                if (stream.Read(two, 0, 2) < 2)
                    break;

                int length = (two[0] << 8) | two[1];

                bool startOfFrame = kind is >= 0xC0 and <= 0xCF && kind is not (0xC4 or 0xC8 or 0xCC);

                if (startOfFrame)
                {
                    if (stream.Read(frame, 0, 5) < 5)
                        break;

                    return ((frame[3] << 8) | frame[4], (frame[1] << 8) | frame[2]);
                }

                stream.Position += Math.Max(0, length - 2);
            }

            return (0, 0);
        }

        // WebP: the size sits in different places for the three chunk kinds.
        private static (int Width, int Height) ReadWebPSize(byte[] h)
        {
            string chunk = new string(new[] { (char)h[12], (char)h[13], (char)h[14], (char)h[15] });

            switch (chunk)
            {
                case "VP8 ":
                    return ((h[26] | (h[27] << 8)) & 0x3FFF, (h[28] | (h[29] << 8)) & 0x3FFF);
                case "VP8L":
                    int bits = h[21] | (h[22] << 8) | (h[23] << 16) | (h[24] << 24);
                    return ((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
                case "VP8X":
                    return ((h[24] | (h[25] << 8) | (h[26] << 16)) + 1, (h[27] | (h[28] << 8) | (h[29] << 16)) + 1);
                default:
                    return (0, 0);
            }
        }

        /// <summary>Byte-for-byte equality, cheap when the lengths differ.
        /// Used to recognise the applied copy of a library picture, which
        /// lives under another name (see SaveCustomBackground).</summary>
        public static bool SameContent(string a, string b)
        {
            try
            {
                var fa = new FileInfo(a);
                var fb = new FileInfo(b);

                if (fa.Length != fb.Length)
                    return false;

                using FileStream sa = fa.OpenRead();
                using FileStream sb = fb.OpenRead();

                var bufferA = new byte[81920];
                var bufferB = new byte[81920];

                while (true)
                {
                    int readA = sa.Read(bufferA, 0, bufferA.Length);
                    int readB = sb.Read(bufferB, 0, bufferB.Length);

                    if (readA != readB)
                        return false;

                    if (readA == 0)
                        return true;

                    if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
