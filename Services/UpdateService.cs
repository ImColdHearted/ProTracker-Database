using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Foot_Tracker.Services;

// §369. Updating the tracker from inside the tracker.
//
// WHAT THIS REPLACES. Every release so far has been a forum post with a
// MediaFire link per platform: the player notices, downloads a zip, finds
// their tracker folder, unzips over it. Most never notice. The version in a
// bug report (§231) has been the clearest evidence of that - reports arrive
// from builds several releases old, about things already fixed.
//
// WHY MEDIAFIRE CANNOT BE PART OF THIS. A MediaFire link serves an
// interstitial PAGE, not a file; there is no stable URL an app can GET. The
// Worker already owns an R2 bucket called protracker-downloads, served from
// dl.protrackerdb.com, which has been hosting community appearance images
// since §345. Builds go in the same bucket, under builds/, and the manifest
// sits at the root. Cutting a release becomes: upload the zips, edit one
// small JSON file. No Worker deploy - deliberately, because a release should
// not be gated on deploying code.
//
// THE SHAPE OF THE MANIFEST, which is the contract this file depends on:
//
//   {
//     "version": "1.0.7",
//     "notes": "Bigger target sprites, black gallery cards",
//     "published": "2026-09-19",
//     "downloads": {
//       "win-x64":     { "url": "https://dl.protrackerdb.com/builds/1.0.7/win-x64.zip",
//                        "sha256": "…", "bytes": 132000000 },
//       "linux-x64":   { "url": "…" },
//       "linux-arm64": { "url": "…" },
//       "osx-x64":     { "url": "…" },
//       "osx-arm64":   { "url": "…" }
//     }
//   }
//
// Only "version" and one matching "downloads" entry with a "url" are
// required. sha256 and bytes are optional and checked when present.
//
// WHAT THIS DELIBERATELY DOES NOT DO. It never updates without being asked.
// The check is a GET of one small file; the download, the swap and the
// restart happen only after the player presses Update in the window this
// opens. Nothing is installed in the background and nothing is silent.
public static class UpdateService
{
    /// <summary>The one file a release has to change. Everything else this
    /// class does is driven by what it says.</summary>
    public const string ManifestUrl = "https://dl.protrackerdb.com/version.json";

    // Short: this runs at startup and nobody should wait on it. A tracker
    // that cannot reach the manifest simply does not offer an update.
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal sealed class ManifestDownload
    {
        public string Url { get; set; } = string.Empty;

        public string? Sha256 { get; set; }

        public long Bytes { get; set; }
    }

    internal sealed class Manifest
    {
        public string Version { get; set; } = string.Empty;

        public string? Notes { get; set; }

        public string? Published { get; set; }

        public Dictionary<string, ManifestDownload> Downloads { get; set; } = new();
    }

    /// <summary>What CheckAsync found: a newer build, and where to get the
    /// one for THIS platform.
    ///
    /// Public, like CommunityThemeService.CommunityTheme and for the same
    /// reason: MainWindowViewModel is a public class and hands this to the
    /// window the menu item opens, and a public member whose type is
    /// internal does not compile. The two Manifest types below stay
    /// internal - they are the wire format and nothing outside this file
    /// reads them.</summary>
    public sealed record AvailableUpdate(
        string Version,
        string CurrentVersion,
        string Url,
        string? Sha256,
        long Bytes,
        string? Notes,
        string? Published);

    // ===================================================================
    // WHICH BUILD AM I.
    // ===================================================================

    /// <summary>The manifest key for this install - "win-x64", "osx-arm64"
    /// and so on, matching the RIDs the publish commands in this guide use.
    ///
    /// Composed from the OS and the process architecture rather than read
    /// from RuntimeInformation.RuntimeIdentifier, because that can come back
    /// more specific than the RID a build was published with ("win10-x64"),
    /// and a key that does not match the manifest reads as "no update for
    /// you" - the quietest possible failure.</summary>
    public static string PlatformKey { get; } = DetectPlatform();

    private static string DetectPlatform()
    {
        string os =
            OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux"
            : string.Empty;

        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => string.Empty,
        };

        return os.Length == 0 || arch.Length == 0
            ? string.Empty
            : os + "-" + arch;
    }

    /// <summary>The folder this build is installed in - the one that gets
    /// replaced. Environment.ProcessPath rather than AppContext.BaseDirectory
    /// because the Windows build is published single-file, and the two agree
    /// there only because IncludeNativeLibrariesForSelfExtract is off (see
    /// the csproj). The process path is the file actually running either
    /// way.</summary>
    public static string? InstallDirectory =>
        string.IsNullOrEmpty(Environment.ProcessPath)
            ? null
            : Path.GetDirectoryName(Environment.ProcessPath);

    // ===================================================================
    // IS THERE A NEWER ONE.
    // ===================================================================

    /// <summary>Reads the manifest and returns an update only if it is
    /// genuinely newer than this build and there is a download for this
    /// platform. Null for every other outcome, including every failure -
    /// a tracker that cannot check is a tracker that does not nag.</summary>
    public static async Task<AvailableUpdate?> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        if (PlatformKey.Length == 0)
        {
            Log.Information("Update check skipped: unrecognised platform.");
            return null;
        }

        try
        {
            string json = await http.GetStringAsync(ManifestUrl, cancellationToken);

            Manifest? manifest = JsonSerializer.Deserialize<Manifest>(json, JsonOptions);

            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
            {
                Log.Warning("Update check: the manifest carried no version.");
                return null;
            }

            string current = AppVersion.Current;

            if (!IsNewer(manifest.Version, current))
            {
                Log.Information(
                    "Update check: running {Current}, latest {Latest} - nothing to do.",
                    current, manifest.Version);
                return null;
            }

            if (!manifest.Downloads.TryGetValue(PlatformKey, out ManifestDownload? download) ||
                string.IsNullOrWhiteSpace(download.Url))
            {
                Log.Information(
                    "Update check: {Latest} is newer than {Current} but has no {Platform} build.",
                    manifest.Version, current, PlatformKey);
                return null;
            }

            // A manifest is a file on a server, and a URL out of it is about
            // to be downloaded and unpacked over the install. It has to be
            // the bucket this app ships from, over HTTPS, and not something
            // a redirect or a typo wandered onto.
            if (!IsTrustedDownloadUrl(download.Url))
            {
                Log.Warning(
                    "Update check: refusing a download URL outside the downloads host ({Url}).",
                    download.Url);
                return null;
            }

            Log.Information(
                "Update available: {Latest} for {Platform} (running {Current}).",
                manifest.Version, PlatformKey, current);

            return new AvailableUpdate(
                manifest.Version.Trim(),
                current,
                download.Url.Trim(),
                string.IsNullOrWhiteSpace(download.Sha256) ? null : download.Sha256.Trim(),
                download.Bytes,
                string.IsNullOrWhiteSpace(manifest.Notes) ? null : manifest.Notes.Trim(),
                string.IsNullOrWhiteSpace(manifest.Published) ? null : manifest.Published.Trim());
        }
        catch (Exception ex)
        {
            // Every failure is the same outcome: no button. Logged rather
            // than shown, because "could not reach the update server" is not
            // news to somebody who is hunting.
            Log.Information(ex, "Update check could not be completed.");
            return null;
        }
    }

    /// <summary>Host allow-list for a download. HTTPS and the downloads host
    /// only - the same bucket the appearance gallery already serves from.</summary>
    public static bool IsTrustedDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(uri.Host, "dl.protrackerdb.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="candidate"/> is a strictly higher
    /// version than <paramref name="current"/>.
    ///
    /// Both sides have to parse. A version string this code does not
    /// understand means no update is offered rather than one being offered
    /// on a guess - and "unknown", which AppVersion returns when an assembly
    /// carries no version at all, is exactly such a string.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        return TryParse(candidate, out Version? newer) &&
               TryParse(current, out Version? running) &&
               newer > running;
    }

    private static bool TryParse(string text, out Version? version)
    {
        version = null;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        string trimmed = text.Trim();

        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[1..];

        // "1.0.7-beta2" and "1.0.7+abc" both compare as 1.0.7. AppVersion
        // already drops the build metadata; this handles a manifest that
        // did not.
        int cut = trimmed.IndexOfAny(new[] { '-', '+', ' ' });

        if (cut >= 0)
            trimmed = trimmed[..cut];

        return Version.TryParse(trimmed, out version);
    }

    // ===================================================================
    // FETCH IT.
    // ===================================================================

    /// <summary>Downloads the update to a temp file, reporting 0..1 as it
    /// goes. Returns the zip's path, or null if anything went wrong -
    /// including a hash that does not match, which is the whole reason to
    /// publish one.</summary>
    public static async Task<string?> DownloadAsync(
        AvailableUpdate update,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!IsTrustedDownloadUrl(update.Url))
            return null;

        string target = Path.Combine(
            Path.GetTempPath(),
            $"protracker-update-{update.Version}-{PlatformKey}.zip");

        try
        {
            using HttpResponseMessage response = await http.GetAsync(
                update.Url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength ??
                          (update.Bytes > 0 ? update.Bytes : null);

            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (FileStream destination = File.Create(target))
            {
                byte[] buffer = new byte[81920];
                long written = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;

                    if (total is > 0)
                        progress?.Report(Math.Clamp(written / (double)total.Value, 0, 1));
                }
            }

            if (update.Sha256 is not null && !HashMatches(target, update.Sha256))
            {
                Log.Warning("Update download rejected: SHA-256 did not match the manifest.");
                TryDelete(target);
                return null;
            }

            progress?.Report(1);
            return target;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update download failed.");
            TryDelete(target);
            return null;
        }
    }

    private static bool HashMatches(string path, string expected)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            byte[] hash = SHA256.HashData(stream);
            string actual = Convert.ToHexString(hash);

            return string.Equals(actual, expected.Replace("-", string.Empty).Trim(),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update download could not be hashed.");
            return false;
        }
    }

    // ===================================================================
    // UNPACK IT, AND CHECK IT IS A TRACKER.
    // ===================================================================

    /// <summary>§370. What Stage found - either a folder holding the build,
    /// or a sentence saying why not. Problem is written for the window,
    /// because the first real attempt at this (§369's own test) ended in
    /// "not a tracker" with no way to tell which of four reasons that was.</summary>
    public sealed record StageResult(string? Root, string Problem)
    {
        public bool Succeeded => Root is not null;
    }

    /// <summary>Extracts the zip to a temp folder and finds the build inside
    /// it - wherever it is. Returns a folder that contains a file named
    /// exactly like the executable currently running, or a StageResult whose
    /// Problem says precisely what was wrong with the download instead.
    ///
    /// THAT CHECK IS THE POINT OF THIS METHOD: it is the last moment before
    /// the tracker closes itself and hands a folder to a script, and a
    /// staging folder that does not contain the app is the one thing
    /// guaranteed to leave the user with nothing.
    ///
    /// §370 changed how the build is looked for. §369 descended one level
    /// when the zip wrapped everything in a single folder, and refused
    /// anything else. That refuses a zip of the whole win-x64 folder (build
    /// output AND publish/ inside it), a zip with a README beside the folder,
    /// and two levels of wrapping - all of which are things a person makes by
    /// right-clicking. So it searches instead: every copy of the executable
    /// in the archive is a candidate, one under a folder called publish is
    /// preferred because that is the single-file build and the other is the
    /// framework-dependent one beside it, and otherwise the shallowest wins.</summary>
    public static StageResult Stage(string zipPath)
    {
        string staging = Path.Combine(
            Path.GetTempPath(),
            "protracker-update-" + Guid.NewGuid().ToString("N"));

        string? exeName = Path.GetFileName(Environment.ProcessPath);

        if (string.IsNullOrEmpty(exeName))
        {
            Log.Warning("Update staging: this process has no path to compare against.");
            return new StageResult(null, "This process has no executable path to compare the download against.");
        }

        // Before unpacking: is it even a zip? The download is whatever the
        // server sent for that URL, and a server that answers a missing file
        // with a page rather than a 404 hands back HTML that ZipFile will
        // choke on with a message about a header. Say what it is instead.
        string kind = DescribeFile(zipPath);

        if (kind != "zip")
        {
            Log.Warning("Update staging rejected: the download is {Kind}, not a zip.", kind);
            return new StageResult(null,
                $"The download is {kind}, not a zip file. Check that the URL in version.json " +
                "points at the zip itself.");
        }

        try
        {
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update could not be unpacked.");
            TryDeleteDirectory(staging);
            return new StageResult(null,
                "The zip could not be unpacked: " + ex.Message);
        }

        string[] candidates;

        try
        {
            candidates = Directory.GetFiles(staging, exeName, SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update staging: the unpacked folder could not be searched.");
            TryDeleteDirectory(staging);
            return new StageResult(null, "The unpacked files could not be searched: " + ex.Message);
        }

        if (candidates.Length == 0)
        {
            string top = DescribeTopLevel(staging);

            Log.Warning(
                "Update staging rejected: the download does not contain {Exe}. Top level: {Top}",
                exeName, top);
            TryDeleteDirectory(staging);
            return new StageResult(null,
                $"The zip does not contain {exeName}. Its top level holds: {top}. " +
                "The zip has to hold the published build - the folder with the exe in it.");
        }

        string chosen = ChooseBuild(candidates, staging);

        if (candidates.Length > 1)
        {
            Log.Information(
                "Update staging: {Count} copies of {Exe} in the download; using {Chosen}.",
                candidates.Length, exeName, chosen);
        }

        return new StageResult(Path.GetDirectoryName(chosen)!, string.Empty);
    }

    /// <summary>§370. Between several copies of the executable, the one under
    /// a folder named publish; failing that, the shallowest. See Stage.</summary>
    private static string ChooseBuild(string[] candidates, string staging)
    {
        string best = candidates[0];
        int bestDepth = int.MaxValue;
        bool bestPublished = false;

        foreach (string candidate in candidates)
        {
            string relative = Path.GetRelativePath(staging, candidate);
            string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            int depth = parts.Length;

            bool published = false;

            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (string.Equals(parts[i], "publish", StringComparison.OrdinalIgnoreCase))
                    published = true;
            }

            bool better =
                (published && !bestPublished) ||
                (published == bestPublished && depth < bestDepth);

            if (better)
            {
                best = candidate;
                bestDepth = depth;
                bestPublished = published;
            }
        }

        return best;
    }

    /// <summary>§370. What a downloaded file actually is, from its first
    /// bytes, in words a person can act on.</summary>
    private static string DescribeFile(string path)
    {
        try
        {
            byte[] head = new byte[8];
            int n;

            using (FileStream stream = File.OpenRead(path))
                n = stream.Read(head, 0, head.Length);

            if (n >= 4 && head[0] == 0x50 && head[1] == 0x4B &&
                (head[2] == 0x03 || head[2] == 0x05 || head[2] == 0x07))
            {
                return "zip";
            }

            if (n >= 6 && head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC && head[3] == 0xAF)
                return "a 7-Zip archive (.7z)";

            if (n >= 4 && head[0] == 0x52 && head[1] == 0x61 && head[2] == 0x72 && head[3] == 0x21)
                return "a RAR archive";

            if (n >= 2 && head[0] == 0x1F && head[1] == 0x8B)
                return "a gzip file (.gz or .tar.gz)";

            string text = Encoding.ASCII.GetString(head, 0, n).TrimStart();

            if (text.StartsWith("<", StringComparison.Ordinal))
                return "a web page (HTML)";

            if (text.StartsWith("{", StringComparison.Ordinal) || text.StartsWith("[", StringComparison.Ordinal))
                return "a JSON document";

            if (n == 0)
                return "an empty file";

            return "an unrecognised file";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update download could not be read back.");
            return "an unreadable file";
        }
    }

    /// <summary>§370. The first few names at the top of the unpacked folder,
    /// so a zip with the wrong shape says what shape it has.</summary>
    private static string DescribeTopLevel(string staging)
    {
        try
        {
            var names = new List<string>();

            foreach (string dir in Directory.GetDirectories(staging))
                names.Add(Path.GetFileName(dir) + "/");

            foreach (string file in Directory.GetFiles(staging))
                names.Add(Path.GetFileName(file));

            if (names.Count == 0)
                return "(nothing)";

            const int shown = 8;

            string list = string.Join(", ", names.GetRange(0, Math.Min(shown, names.Count)));

            return names.Count > shown
                ? list + $" and {names.Count - shown} more"
                : list;
        }
        catch
        {
            return "(could not be listed)";
        }
    }

    // ===================================================================
    // SWAP IT.
    // ===================================================================

    /// <summary>Writes the helper that does the swap, starts it detached,
    /// and returns true. The CALLER then closes the tracker - the helper is
    /// already waiting for this process to disappear before it touches
    /// anything.
    ///
    /// A helper process rather than doing it here, because a program cannot
    /// replace the files it is running out of: Windows holds the executable
    /// open for as long as the process lives. The script waits for the PID,
    /// copies, relaunches and deletes itself.
    ///
    /// It COPIES over the install rather than mirroring it. Mirroring would
    /// delete anything in the folder that is not in the new build, and that
    /// folder belongs to the user - a screenshot they saved next to the exe
    /// is not this program's to remove. The cost is that a file dropped from
    /// a release lingers; for a self-contained build that is harmless.</summary>
    public static bool Apply(string stagingDirectory)
    {
        string? install = InstallDirectory;
        string? exe = Environment.ProcessPath;

        if (install is null || string.IsNullOrEmpty(exe))
        {
            Log.Warning("Update cannot be applied: this build has no install path.");
            return false;
        }

        try
        {
            string script = OperatingSystem.IsWindows()
                ? WriteWindowsHelper(stagingDirectory, install, exe)
                : WriteUnixHelper(stagingDirectory, install, exe);

            var start = new System.Diagnostics.ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            };

            if (OperatingSystem.IsWindows())
            {
                start.FileName = "cmd.exe";
                start.Arguments = "/c \"" + script + "\"";
            }
            else
            {
                start.FileName = "/bin/sh";
                start.Arguments = "\"" + script + "\"";
            }

            System.Diagnostics.Process.Start(start);

            Log.Information("Update helper started; closing for the swap.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The update helper could not be started.");
            return false;
        }
    }

    private static string WriteWindowsHelper(string staging, string install, string exe)
    {
        int pid = Environment.ProcessId;

        string path = Path.Combine(
            Path.GetTempPath(), "protracker-update-" + Guid.NewGuid().ToString("N") + ".cmd");

        // ping rather than timeout for the wait: timeout fails outright when
        // stdin is redirected, which it is for a process started this way.
        //
        // robocopy's exit codes are a bit-field where anything under 8 means
        // it did what it was asked; 8 and above is a real failure. "if
        // errorlevel 8" is true for >= 8, which is exactly the test wanted.
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine($"set \"PID={pid}\"");
        sb.AppendLine($"set \"SRC={staging}\"");
        sb.AppendLine($"set \"DST={install}\"");
        sb.AppendLine($"set \"EXE={exe}\"");
        sb.AppendLine(":wait");
        sb.AppendLine("tasklist /FI \"PID eq %PID%\" /NH 2>nul | find \"%PID%\" >nul");
        sb.AppendLine("if not errorlevel 1 (");
        sb.AppendLine("  ping -n 2 127.0.0.1 >nul");
        sb.AppendLine("  goto wait");
        sb.AppendLine(")");
        sb.AppendLine("robocopy \"%SRC%\" \"%DST%\" /E /R:3 /W:2 /NFL /NDL /NJH /NJS /NP >nul");
        sb.AppendLine("if errorlevel 8 goto failed");
        sb.AppendLine("start \"\" \"%EXE%\"");
        sb.AppendLine("goto cleanup");
        sb.AppendLine(":failed");
        sb.AppendLine("> \"%DST%\\UPDATE-FAILED.txt\" echo The update could not be copied into place.");
        sb.AppendLine(">> \"%DST%\\UPDATE-FAILED.txt\" echo The new files are here: %SRC%");
        sb.AppendLine(">> \"%DST%\\UPDATE-FAILED.txt\" echo Copy them over this folder by hand, then delete this file.");
        sb.AppendLine("start \"\" \"%DST%\"");
        sb.AppendLine("goto done");
        sb.AppendLine(":cleanup");
        sb.AppendLine("rmdir /S /Q \"%SRC%\" 2>nul");
        sb.AppendLine(":done");
        // The standard self-delete: hand the rest of the file to nothing, so
        // cmd has closed it by the time del runs.
        sb.AppendLine("(goto) 2>nul & del \"%~f0\"");

        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        return path;
    }

    private static string WriteUnixHelper(string staging, string install, string exe)
    {
        int pid = Environment.ProcessId;

        string path = Path.Combine(
            Path.GetTempPath(), "protracker-update-" + Guid.NewGuid().ToString("N") + ".sh");

        // chmod is not optional here: .NET's ZipFile does not restore the
        // Unix executable bit, so a freshly unpacked build is a folder full
        // of files nothing can run. The same is true of the start scripts
        // the macOS and Linux downloads are documented around.
        var sb = new StringBuilder();
        sb.Append("#!/bin/sh\n");
        sb.Append($"PID={pid}\n");
        sb.Append($"SRC='{staging}'\n");
        sb.Append($"DST='{install}'\n");
        sb.Append($"EXE='{exe}'\n");
        sb.Append("while kill -0 \"$PID\" 2>/dev/null; do sleep 1; done\n");
        sb.Append("if cp -R \"$SRC/.\" \"$DST/\"; then\n");
        sb.Append("  chmod +x \"$EXE\" 2>/dev/null\n");
        sb.Append("  find \"$DST\" -maxdepth 1 -name '*.sh' -exec chmod +x {} + 2>/dev/null\n");
        // Downloaded-by-a-browser files carry this and macOS refuses to run
        // them. HttpClient does not set it, but a user who unpacked a zip by
        // hand once may have left it on the folder.
        sb.Append("  xattr -dr com.apple.quarantine \"$DST\" 2>/dev/null\n");
        sb.Append("  rm -rf \"$SRC\"\n");
        sb.Append("  cd \"$DST\" && \"$EXE\" >/dev/null 2>&1 &\n");
        sb.Append("else\n");
        sb.Append("  {\n");
        sb.Append("    echo 'The update could not be copied into place.'\n");
        sb.Append("    echo \"The new files are here: $SRC\"\n");
        sb.Append("    echo 'Copy them over this folder by hand, then delete this file.'\n");
        sb.Append("  } > \"$DST/UPDATE-FAILED.txt\"\n");
        sb.Append("fi\n");
        sb.Append("rm -f \"$0\"\n");

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex)
        {
            // Harmless: it is started as an argument to /bin/sh, which does
            // not need the bit. Logged because if it ever IS needed, this is
            // the line that failed.
            Log.Debug(ex, "Update helper could not be marked executable.");
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* temp file */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* temp dir */ }
    }
}
