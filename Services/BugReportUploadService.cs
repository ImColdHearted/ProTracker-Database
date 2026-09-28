using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Foot_Tracker.Services;

// §226. Delivers a Report a Problem bundle to the events Worker, which
// relays it into a private Discord channel.
//
// BugReportEmailService (§73) is the thing this replaces, and it is worth
// being precise about what was wrong with it: nothing, except that it could
// not be switched on. Sending needed a Gmail App Password, and the only
// place to put one was ReportEmailSettings - a const in a client anyone can
// decompile. It was left as a placeholder and the button was never wired to
// it, so the feature has spent its whole life saving files to Downloads and
// asking the player to send them by hand.
//
// The Worker changes that because a secret can live there. The client knows
// only the server's own address, which is not a secret and is already in the
// config file EventsSyncService reads; the webhook URL - which IS a
// credential, and a permanent write one - stays a Cloudflare secret. Nothing
// shipped to a player can post into the channel; it can only ask the Worker
// to, and the Worker caps how often.
//
// Every failure is silent and returns false, because the caller's existing
// message is already the correct fallback for all of them: no internet, no
// server configured, the webhook not set up yet, a report too large, the
// hourly cap reached. In every one of those cases the files are sitting in
// the player's Downloads folder, which is exactly what that message says.
internal static class BugReportUploadService
{
    // Deliberately not EventsSyncService's client. Every other call to this
    // server is a few hundred bytes of JSON and twelve seconds is generous
    // for one; a report is megabytes, and twelve seconds would fail an
    // upload that was working perfectly well on a slow line.
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(90) };

    // Kept in step with REPORT_MAX_TOTAL_BYTES in worker.js. Checked here as
    // well as there so an oversized bundle costs nothing rather than a
    // pointless upload the server was always going to refuse. §391:
    // internal, so the log copy can be cut to what the bundle has left.
    internal const long MaxTotalBytes = 8L * 1024 * 1024;

    // Kept in step with REPORT_MAX_FILE_BYTES in worker.js, for the same
    // reason as the total above. §391: internal, so the log copy can be cut
    // to fit it before it is ever offered.
    internal const long MaxFileBytes = 4L * 1024 * 1024;

    // §229. Kept in step with REPORT_MIN_INTERVAL_MINUTES in worker.js. The
    // server is what actually enforces this - a number in a client anyone
    // can edit defends nothing - but knowing it here means a player who
    // presses the button twice is told to wait instead of writing a
    // description, uploading megabytes, and being refused at the end of it.
    internal const int CooldownMinutes = 10;

    private static readonly string LastSentPath = Path.Combine(
        EventsSyncService.LocalDataFolder,
        "last-report-utc.txt");

    /// <summary>Never throws. Null means nothing was sent and the caller
    /// should show its usual "the files are in your Downloads folder"
    /// message, which is what it showed before any of this existed. §391:
    /// otherwise the paths that actually went - a file skipped for its size
    /// is not among them, so the caller neither deletes it nor tells the
    /// player it arrived. A report used to say "sent, cleaned up" of a log
    /// that was over the cap, dropped here, and then deleted from Downloads
    /// with the rest.</summary>
    internal static async Task<IReadOnlyList<string>?> TrySendAsync(
        IReadOnlyList<string> paths,
        string note,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!EventsSyncService.IsOnline)
                return null;

            // Owned by the request below, which disposes it.
            var content = new MultipartFormDataContent();

            long total = 0;
            var sent = new List<string>();

            foreach (string path in paths)
            {
                if (!File.Exists(path))
                    continue;

                long length = new FileInfo(path).Length;

                // An empty file says nothing, and one that would push the
                // bundle over the cap is skipped rather than failing the
                // whole report - a log without its screenshot still beats
                // nothing arriving at all.
                // §227: the per-file cap the server also applies. Without
                // it a large file passed this check, travelled, and was
                // then dropped server-side where nobody could see why.
                if (length == 0 || length > MaxFileBytes || total + length > MaxTotalBytes)
                {
                    Log.Information(
                        "Report upload skipped {File} ({Bytes} bytes) - over a size cap.",
                        Path.GetFileName(path),
                        length);

                    continue;
                }

                byte[] bytes = await File
                    .ReadAllBytesAsync(path, cancellationToken)
                    .ConfigureAwait(false);

                content.Add(FilePart(bytes, Path.GetFileName(path)));

                total += length;
                sent.Add(path);
            }

            if (sent.Count == 0)
            {
                content.Dispose();
                return null;
            }

            content.Add(TextPart(note ?? string.Empty, "note"));
            content.Add(TextPart(AppVersionString(), "version"));
            content.Add(TextPart(PlatformName(), "platform"));

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                EventsSyncService.BaseUrl + "/v1/report")
            {
                Content = content,
            };

            request.Headers.TryAddWithoutValidation(
                "X-Install-Token",
                EventsSyncService.InstallToken);

            using HttpResponseMessage response = await http
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                MarkSent(); // §229
                return sent;
            }

            // The status is the useful half - 503 means the webhook is not
            // configured on the server yet, 429 means this tracker has sent
            // too many, 413 means the bundle was too big. None of them are
            // the player's problem and none of them change what happens
            // next, so none of them reach the screen.
            // §227: the BODY, not just the status. Every refusal this
            // server can return carries a sentence saying which of them it
            // was, and the first version of this line threw that away -
            // which cost a build cycle and a PowerShell probe to recover
            // something the app already had in its hand.
            string reason = string.Empty;

            try
            {
                reason = await response.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // A body that will not read changes nothing about what
                // happens next.
            }

            if (reason.Length > 300)
                reason = reason[..300];

            Log.Information(
                "Report upload refused with {Status}: {Reason}. The files stay in Downloads.",
                (int)response.StatusCode,
                reason);

            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Report upload failed; the files stay in Downloads.");
            return null;
        }
    }

    /// <summary>§227. A file part whose headers the receiving parser will
    /// actually accept.
    ///
    /// MultipartFormDataContent.Add(content, name, fileName) looks like the
    /// right call and is not. It sets FileNameStar as well as FileName, so
    /// the part goes out carrying a filename*=utf-8'' parameter - and the
    /// WHATWG multipart parser behind Request.formData(), which is what a
    /// Cloudflare Worker runs, rejects the WHOLE BODY when it sees one. Not
    /// the part: the body. It does the same for an unquoted name=, which
    /// that overload can also produce, since every character of "files" is
    /// a legal token character and .NET only quotes when it must.
    ///
    /// So the header is written here instead. Assigning an already-quoted
    /// string is the documented way to make the setter keep the quotes, and
    /// never touching FileNameStar is what keeps filename* off the wire.
    /// The single-argument Add is then the one that leaves a disposition
    /// alone once it exists.
    ///
    /// The symptom was a flat 400 from a server that was configured
    /// correctly and a route that worked: curl posting the same file to the
    /// same endpoint succeeded, because curl quotes both parameters and
    /// sends no filename*.</summary>
    private static ByteArrayContent FilePart(byte[] bytes, string fileName)
    {
        var part = new ByteArrayContent(bytes);

        part.Headers.ContentDisposition =
            new ContentDispositionHeaderValue("form-data")
            {
                Name = "\"files\"",
                FileName = "\"" + Sanitize(fileName) + "\"",
            };

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

    /// <summary>§227. A quote or a control character inside a file name
    /// would close the header's quoted string early and break the very
    /// framing this exists to get right. These names are generated by
    /// ReportProblemButton_Click and contain neither, so this is a guard
    /// against a future caller rather than against today's.</summary>
    private static string Sanitize(string fileName)
    {
        var clean = new System.Text.StringBuilder(fileName.Length);

        foreach (char c in fileName)
        {
            if (c != '"' && c != '\\' && !char.IsControl(c))
                clean.Append(c);
        }

        return clean.Length == 0 ? "report.bin" : clean.ToString();
    }

    /// <summary>§229. How much of the cooldown is left, or zero when a
    /// report can be sent now. An unreadable or nonsense stored time counts
    /// as zero: the server still holds the real gate, and refusing to let
    /// someone report a problem because a text file went strange would be
    /// the wrong way round.</summary>
    internal static TimeSpan RemainingCooldown()
    {
        try
        {
            if (!File.Exists(LastSentPath))
                return TimeSpan.Zero;

            string raw = File.ReadAllText(LastSentPath).Trim();

            if (!DateTimeOffset.TryParse(
                    raw,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                        System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset last))
            {
                return TimeSpan.Zero;
            }

            TimeSpan since = DateTimeOffset.UtcNow - last;

            // A clock that moved backwards would otherwise lock the button
            // for as long as the jump.
            if (since < TimeSpan.Zero)
                return TimeSpan.Zero;

            TimeSpan remaining = TimeSpan.FromMinutes(CooldownMinutes) - since;

            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the report cooldown; treating it as expired.");
            return TimeSpan.Zero;
        }
    }

    private static void MarkSent()
    {
        try
        {
            Directory.CreateDirectory(EventsSyncService.LocalDataFolder);

            File.WriteAllText(
                LastSentPath,
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            // The server's own gate still holds, so this costs a warning
            // and nothing else.
            Log.Warning(ex, "Could not record when the last report was sent.");
        }
    }

    /// <summary>§229. Removes the copies a delivered report left in
    /// Downloads, and returns how many actually went.
    ///
    /// Only ever called after the server confirmed delivery, and only ever
    /// with the exact paths this run created - never a wildcard over the
    /// folder, which would reach reports the player kept on purpose. A file
    /// that will not delete is left alone and logged; the report has
    /// already arrived, so nothing about that is worth interrupting anyone
    /// over. The log copy is a copy, so this never touches the live one.</summary>
    internal static int DeleteSentFiles(IReadOnlyList<string> paths)
    {
        int removed = 0;

        foreach (string path in paths)
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                File.Delete(path);
                removed++;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not remove {File} after sending it.", Path.GetFileName(path));
            }
        }

        return removed;
    }

    // §223 is the reason this is sent at all: an evening went into a fault
    // that turned out to be a build question, and no report said which build
    // it came from. §231: and until then it always answered 1.0.0.0, because
    // the csproj set no version for it to read - so the field was there and
    // said nothing. AppVersion is now shared with the presence heartbeat, so
    // a report and a heartbeat can never disagree about what is running.
    private static string AppVersionString() => AppVersion.Current;

    private static string PlatformName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS"
        : "unknown";
}
