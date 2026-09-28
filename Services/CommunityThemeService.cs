using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Foot_Tracker.Models;
using Foot_Tracker.Tracking;
using Serilog;
using SkiaSharp;

namespace Foot_Tracker.Services;

// §345. The community appearance gallery: reads the approved themes from
// the events Worker, and posts this tracker's own appearance for review.
//
// Why the sharing happens HERE and not on the website. A theme already
// exists inside this app - the colours are on screen - so sharing it is one
// button rather than "export a file, find it, open a browser, fill in a
// form". And a web form has no barrier at all, while every request from
// here carries the per-install token the server hashes and rate-limits
// against, the same identity Report a Problem is metered by (§226).
//
// Nothing here is authenticated as a PERSON. The server sees a hash and an
// app version; it never sees a player name, and this service never sends
// one unless the user typed it into the Author box themselves.
public static class CommunityThemeService
{
    // Same 90 seconds BugReportUploadService allows. A background image is
    // at most a couple of megabytes after the re-encode below, but a
    // tracker uploading from a bad connection should not be cut off at 30.
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(90) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // The Worker answers in camelCase (id, imageUrl) while ThemeFile's
        // own fields are PascalCase inside "colours". One insensitive
        // reader handles both rather than two sets of attributes.
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The longest edge an uploaded background is allowed to keep.
    /// A tracker window is 875x600 by default and at most a 4K screen, so
    /// beyond this is bytes nobody sees.</summary>
    public const int MaxImageEdge = 2560;

    /// <summary>What the re-encode aims for. JPEG at 85 puts a typical
    /// wallpaper somewhere around 200-600 KB, well under the server's
    /// 2 MB refusal.</summary>
    private const int JpegQuality = 85;

    /// <summary>§346. A GIF cannot be re-encoded - Skia decodes the format
    /// and does not write it - so an animated background travels as the
    /// bytes that arrived, and these are refusals rather than resizes.
    ///
    /// Calibrated against a real one: a typical shared background is around
    /// 480x480, sixty frames and under half a megabyte. Four megabytes is
    /// eight times that, which leaves room for a bigger one without leaving
    /// room for someone's screen recording.</summary>
    public const int MaxGifBytes = 4 * 1024 * 1024;
    public const int MaxGifEdge = 1920;
    public const int MaxGifFrames = 400;

    public const int MaxNameLength = 40;
    public const int MaxAuthorLength = 32;

    /// <summary>§346. What PrepareImage decided: either bytes ready to
    /// upload, or a sentence saying why not.
    ///
    /// One return value rather than a nullable plus an out parameter,
    /// because every caller needs both halves and splitting them invites
    /// the one that forgets to look at the refusal.</summary>
    private sealed record PreparedImage(
        byte[]? Bytes,
        int Width,
        int Height,
        string Extension,
        string ContentType,
        string? Refusal)
    {
        public static PreparedImage Ok(byte[] bytes, int width, int height, string extension, string contentType) =>
            new(bytes, width, height, extension, contentType, null);

        public static PreparedImage No(string refusal) =>
            new(null, 0, 0, string.Empty, string.Empty, refusal);
    }

    /// <summary>One approved appearance as the gallery lists it.</summary>
    public sealed class CommunityTheme
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Author { get; set; } = string.Empty;

        /// <summary>The §209 portable subset, exactly as a .protheme.json
        /// carries it - so applying one of these goes through
        /// AppearanceSettingsRepository.ImportTheme like any other theme
        /// rather than through a second set of merge rules.</summary>
        public AppearanceSettingsRepository.ThemeFile? Colours { get; set; }

        /// <summary>Empty when the theme carries no background image.
        /// Otherwise an https URL on the downloads domain.</summary>
        public string ImageUrl { get; set; } = string.Empty;

        public int ImageWidth { get; set; }

        public int ImageHeight { get; set; }

        public string SubmittedUtc { get; set; } = string.Empty;

        public int Applied { get; set; }

        public bool HasImage => !string.IsNullOrWhiteSpace(ImageUrl);
    }

    private sealed class ThemeListResponse
    {
        [JsonPropertyName("themes")]
        public List<CommunityTheme> Themes { get; set; } = new();
    }

    private sealed class SubmitResponse
    {
        public string Id { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string? Error { get; set; }
    }

    /// <summary>Reads the approved gallery. Returns an empty list rather
    /// than throwing when the server is unreachable or not configured -
    /// a gallery that cannot be reached is an empty gallery with a message,
    /// not an error dialog over the top of the tracker.</summary>
    public static async Task<IReadOnlyList<CommunityTheme>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        if (!EventsSyncService.IsOnline)
            return Array.Empty<CommunityTheme>();

        try
        {
            using var response = await http.GetAsync(
                EventsSyncService.BaseUrl + "/v1/themes",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning(
                    "Community appearances list returned {Status}",
                    (int)response.StatusCode);
                return Array.Empty<CommunityTheme>();
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            ThemeListResponse? parsed =
                JsonSerializer.Deserialize<ThemeListResponse>(body, JsonOptions);

            // A row whose colours failed to parse is dropped rather than
            // shown as a blank card: every other field on it is cosmetic,
            // but without Colours there is nothing to apply.
            return parsed?.Themes.Where(t => t.Colours is not null).ToList()
                   ?? (IReadOnlyList<CommunityTheme>)Array.Empty<CommunityTheme>();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Community appearances list could not be read");
            return Array.Empty<CommunityTheme>();
        }
    }

    /// <summary>Downloads a gallery theme's background into the same folder
    /// the Appearance window's own custom backgrounds live in, and returns
    /// the local path for CustomBackgroundPath. Null if there is no image or
    /// it could not be fetched - the caller then applies the colours alone,
    /// which is a theme that works rather than one that fails.</summary>
    public static async Task<string?> DownloadBackgroundAsync(
        CommunityTheme theme,
        CancellationToken cancellationToken = default)
    {
        if (!theme.HasImage)
            return null;

        try
        {
            byte[] bytes = await http.GetByteArrayAsync(theme.ImageUrl, cancellationToken);

            // Written to a temp file and handed to SaveCustomBackground so
            // the per-client naming (see that method's remarks) is applied
            // by the one place that knows those rules.
            // §346. The extension matters, it is not cosmetic:
            // AnimatedBackgroundService.IsGif decides whether to animate a
            // background by looking at the file name. A shared GIF saved as
            // ".jpg" would sit on its first frame forever, which is the same
            // silent failure §346 fixed on the sending side.
            string extension =
                theme.ImageUrl.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? ".gif"
                : theme.ImageUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ".png"
                : ".jpg";

            string temp = Path.Combine(Path.GetTempPath(), "protheme-" + theme.Id + extension);
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken);

            try
            {
                return AppearanceSettingsRepository.SaveCustomBackground(temp);
            }
            finally
            {
                try { File.Delete(temp); } catch { /* a temp file that outlives us is harmless */ }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Community appearance background could not be downloaded");
            return null;
        }
    }

    // §366. The bytes behind View Image, and a small cache so a second look
    // at the same card is instant.
    //
    // Deliberately separate from DownloadBackgroundAsync above: that one
    // exists to INSTALL a background - it writes into the appearance folder
    // under the per-client naming rules and hands back a path. Looking at a
    // picture must not do any of that. Someone browsing the gallery and
    // pressing View Image on six cards has not chosen any of them, and
    // writing six files into their appearance folder because they looked
    // would be the kind of thing nobody notices until the folder is full.
    //
    // Bounded rather than unbounded: eight is more than a page of cards, and
    // a gallery of large pictures would otherwise hold every one of them in
    // memory for as long as the app runs. Oldest out first.
    private const int ImageCacheLimit = 8;

    private static readonly Dictionary<string, byte[]> imageCache = new();

    private static readonly List<string> imageCacheOrder = new();

    private static readonly object imageCacheLock = new();

    /// <summary>§366. The raw bytes of a gallery theme's background, for
    /// showing it rather than installing it. Null when there is no image or
    /// it could not be fetched; the caller says so rather than failing.</summary>
    public static async Task<byte[]?> DownloadImageBytesAsync(
        CommunityTheme theme,
        CancellationToken cancellationToken = default)
    {
        if (!theme.HasImage)
            return null;

        lock (imageCacheLock)
        {
            if (imageCache.TryGetValue(theme.ImageUrl, out byte[]? cached))
                return cached;
        }

        try
        {
            byte[] bytes = await http.GetByteArrayAsync(theme.ImageUrl, cancellationToken);

            lock (imageCacheLock)
            {
                // Re-checked inside the lock: two cards can be opened fast
                // enough that both miss the check above, and the second
                // should not evict something to store what is already there.
                if (!imageCache.ContainsKey(theme.ImageUrl))
                {
                    imageCache[theme.ImageUrl] = bytes;
                    imageCacheOrder.Add(theme.ImageUrl);

                    while (imageCacheOrder.Count > ImageCacheLimit)
                    {
                        string oldest = imageCacheOrder[0];
                        imageCacheOrder.RemoveAt(0);
                        imageCache.Remove(oldest);
                    }
                }
            }

            return bytes;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Community appearance image could not be downloaded for viewing");
            return null;
        }
    }

    /// <summary>Posts this tracker's current appearance for review. Returns
    /// null on success, or a sentence to show the user.</summary>
    public static async Task<string?> ShareAsync(
        AppearanceSettings settings,
        string name,
        string? author,
        string? backgroundImagePath,
        CancellationToken cancellationToken = default)
    {
        if (!EventsSyncService.IsOnline)
            return "The appearance server is not reachable from this build.";

        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
            return "Give the appearance a name first.";

        try
        {
            var content = new MultipartFormDataContent();

            AppearanceSettingsRepository.ThemeFile file =
                AppearanceSettingsRepository.BuildThemeFile(settings, name);

            content.Add(TextPart(JsonSerializer.Serialize(file), "theme"));
            content.Add(TextPart(Truncate(name, MaxNameLength), "name"));
            content.Add(TextPart(Truncate(author ?? string.Empty, MaxAuthorLength), "author"));
            content.Add(TextPart(AppVersion.Current, "version"));

            if (!string.IsNullOrWhiteSpace(backgroundImagePath) &&
                File.Exists(backgroundImagePath))
            {
                PreparedImage prepared = PrepareImage(backgroundImagePath);

                // §346. The refusal is the image's own sentence, not a
                // generic one: "that GIF has 812 frames" tells the user what
                // to do about it, and "could not be read as an image" does
                // not.
                if (prepared.Refusal is not null || prepared.Bytes is null)
                    return prepared.Refusal ?? "That background image could not be read as an image.";

                content.Add(ImagePart(prepared.Bytes, prepared.Extension, prepared.ContentType));
                content.Add(TextPart(prepared.Width.ToString(), "width"));
                content.Add(TextPart(prepared.Height.ToString(), "height"));
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                EventsSyncService.BaseUrl + "/v1/themes")
            {
                Content = content,
            };

            request.Headers.Add("X-Install-Token", EventsSyncService.InstallToken);

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);

            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                Log.Information("Community appearance submitted: {Name}", name);
                return null;
            }

            SubmitResponse? parsed = null;
            try { parsed = JsonSerializer.Deserialize<SubmitResponse>(body, JsonOptions); }
            catch (JsonException) { /* fall through to the status-code sentence */ }

            Log.Warning(
                "Community appearance rejected: {Status} {Body}",
                (int)response.StatusCode,
                body);

            // The server's own sentence when it sent one - it knows why far
            // better than a status code does (too many today, already
            // waiting for review, a colour that is not a colour).
            return !string.IsNullOrWhiteSpace(parsed?.Error)
                ? parsed!.Error
                : "The server refused that appearance (" + (int)response.StatusCode + ").";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Community appearance could not be submitted");
            return "The appearance could not be sent - " + ex.Message;
        }
    }

    /// <summary>§345. Decodes the user's image, caps its longest edge, and
    /// re-encodes it as JPEG. Null when it does not decode.
    ///
    /// This is the part that actually makes an upload safe, and it is
    /// deliberately on this side rather than on the server:
    ///
    ///  - a file that does not decode is not an image, whatever it is
    ///    named, and it never leaves this machine;
    ///  - the dimensions are capped before a byte goes over the wire
    ///    rather than after;
    ///  - re-encoding normalises the format, so the gallery is never
    ///    serving a BMP or a TIFF someone renamed;
    ///  - and it STRIPS EXIF. People pick wallpapers out of their camera
    ///    roll, and camera-roll photos carry GPS coordinates. Without this
    ///    step, sharing a theme would publish the sharer's home address.
    ///
    /// The server's magic-byte and size checks are a backstop for a client
    /// that skipped this, not the defence.</summary>
    /// <summary>§346. Decides what leaves this machine, and in what shape.
    ///
    /// Two roads, because GIF cannot take the first one. Skia decodes the
    /// format and does not write it (Encode(SKEncodedImageFormat.Gif, ...)
    /// returns null), so an animated background cannot be re-encoded at all
    /// - and the app has played animated backgrounds since §141, so
    /// silently flattening one to its first frame is the wrong answer. That
    /// is what this code did before §346: SKBitmap.Decode on a GIF returns
    /// frame one, and the sharer was told it had worked.
    ///
    /// What the GIF road keeps, loses and gains against re-encoding:
    ///
    ///  - EXIF: nothing to lose. GIF has no EXIF, so the strongest reason
    ///    for re-encoding a photograph simply does not apply here.
    ///  - "is it really an image": STRONGER. Every frame is decoded, not
    ///    just the first, so a GIF header with a payload stapled behind it
    ///    fails where a header-only check would pass.
    ///  - dimensions: lost. Without an encoder there is no resize, so an
    ///    oversized GIF is refused instead of shrunk. §141 scales heavy
    ///    ones down at playback anyway, so this only bites the extreme.
    ///  - trailing bytes: handled. Anything after the GIF's own terminator
    ///    is cut off rather than uploaded.</summary>
    private static PreparedImage PrepareImage(string path)
    {
        try
        {
            byte[] raw = File.ReadAllBytes(path);

            if (IsGif(raw))
                return PrepareGif(raw);

            var encoded = ReEncode(path);

            return encoded is null
                ? PreparedImage.No("That background image could not be read as an image.")
                : PreparedImage.Ok(encoded.Value.bytes, encoded.Value.width, encoded.Value.height,
                                   "jpg", "image/jpeg");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Background image could not be prepared for sharing");
            return PreparedImage.No("That background image could not be read - " + ex.Message);
        }
    }

    /// <summary>GIF87a or GIF89a. The extension is not consulted: a file
    /// named .png that is really a GIF still has to take the GIF road, or
    /// SKBitmap.Decode would flatten it exactly as before.</summary>
    private static bool IsGif(byte[] bytes) =>
        bytes.Length > 6 &&
        bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' &&
        bytes[3] == (byte)'8' && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') &&
        bytes[5] == (byte)'a';

    /// <summary>§346. Validates an animated background and returns the
    /// bytes to send, which are the original ones up to the trailer.</summary>
    private static PreparedImage PrepareGif(byte[] raw)
    {
        // §346. Everything after the GIF's own terminator (0x3B) is not part
        // of the image. Cutting there means a file with something appended
        // uploads as the picture it appears to be and nothing else. Searched
        // from the end, because 0x3B occurs inside pixel data all the time.
        int trailer = Array.LastIndexOf(raw, (byte)0x3B);

        if (trailer <= 0)
            return PreparedImage.No("That GIF looks damaged - it has no end marker.");

        byte[] bytes = trailer == raw.Length - 1 ? raw : raw[..(trailer + 1)];

        if (bytes.Length > MaxGifBytes)
        {
            return PreparedImage.No(
                $"That GIF is {bytes.Length / (1024 * 1024)} MB. The limit for animated backgrounds is " +
                $"{MaxGifBytes / (1024 * 1024)} MB - most are well under one.");
        }

        using var stream = new MemoryStream(bytes);
        using SKCodec? codec = SKCodec.Create(stream);

        if (codec is null)
            return PreparedImage.No("That GIF could not be read.");

        int width = codec.Info.Width;
        int height = codec.Info.Height;
        int frames = Math.Max(1, codec.FrameCount);

        if (Math.Max(width, height) > MaxGifEdge)
        {
            return PreparedImage.No(
                $"That GIF is {width}x{height}. Animated backgrounds cannot be resized before sharing, " +
                $"so the limit is {MaxGifEdge} pixels on the longest side.");
        }

        if (frames > MaxGifFrames)
            return PreparedImage.No($"That GIF has {frames} frames. The limit is {MaxGifFrames}.");

        // Every frame, not just the first. This is what actually proves the
        // file is a GIF all the way through rather than a GIF header with
        // something else behind it - and it is a stricter test than the
        // re-encode road applies to a still.
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var buffer = new SKBitmap(info);

        for (int i = 0; i < frames; i++)
        {
            SKCodecResult status = codec.GetPixels(info, buffer.GetPixels(), new SKCodecOptions(i));

            // §141 tolerates IncompleteInput when PLAYING a GIF, because a
            // half-decoded frame on your own screen beats no background at
            // all. Sharing is the other way round: a damaged file would be
            // damaged for everyone who applied it, so this refuses.
            if (status != SKCodecResult.Success)
            {
                return PreparedImage.No(
                    $"That GIF could not be fully read - frame {i + 1} of {frames} is damaged.");
            }
        }

        return PreparedImage.Ok(bytes, width, height, "gif", "image/gif");
    }

    private static (byte[] bytes, int width, int height)? ReEncode(string path)
    {
        try
        {
            using SKBitmap? decoded = SKBitmap.Decode(path);

            if (decoded is null)
                return null;

            int width = decoded.Width;
            int height = decoded.Height;

            SKBitmap source = decoded;
            SKBitmap? resized = null;

            try
            {
                int longest = Math.Max(width, height);

                if (longest > MaxImageEdge)
                {
                    double scale = (double)MaxImageEdge / longest;
                    width = Math.Max(1, (int)Math.Round(width * scale));
                    height = Math.Max(1, (int)Math.Round(height * scale));

                    // ImageOps.ResizeBilinear, not its nearest-neighbour
                    // sibling: Resize exists for Tesseract, where hard pixel
                    // edges read better (§97), and it is the wrong tool for
                    // shrinking a photograph - nearest-neighbour downscaling
                    // throws away most of the pixels and aliases the rest.
                    resized = ImageOps.ResizeBilinear(source, width, height);
                    source = resized;
                }

                using SKData data = source.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);

                if (data is null)
                    return null;

                return (data.ToArray(), width, height);
            }
            finally
            {
                resized?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Background image could not be re-encoded for sharing");
            return null;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value.Substring(0, max);

    /// <summary>§227's rules, which apply to every multipart part this app
    /// sends: both parameters quoted, and no filename* on the wire. The
    /// WHATWG parser behind a Worker's Request.formData() rejects the whole
    /// body over one, not just the part - see BugReportUploadService's
    /// FilePart for the afternoon that taught us.</summary>
    private static ByteArrayContent ImagePart(byte[] bytes, string extension, string contentType)
    {
        var part = new ByteArrayContent(bytes);

        part.Headers.ContentDisposition =
            new ContentDispositionHeaderValue("form-data")
            {
                Name = "\"image\"",
                FileName = "\"background." + extension + "\"",
            };

        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        return part;
    }

    private static StringContent TextPart(string value, string name)
    {
        var part = new StringContent(value);

        part.Headers.ContentDisposition =
            new ContentDispositionHeaderValue("form-data")
            {
                Name = "\"" + name + "\"",
            };

        return part;
    }
}
