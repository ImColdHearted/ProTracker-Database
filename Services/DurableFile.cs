using System;
using System.IO;
using System.Text;

namespace Foot_Tracker.Services;

/// <summary>
/// §193. One durable way to replace a small settings or data file.
///
/// THE BUG THIS EXISTS FOR. A user's lifetime-stats.json was a run of 0x00
/// bytes at the file's old length, which stopped the app starting at all
/// (§190). It was easy to blame the file system, and the log proved
/// otherwise. It shows the tracker recording encounters until 22:54:07 and a
/// fresh "Pro Tracker starting." at 22:55:39 with NO "Pro Tracker shutting
/// down." between them: the run that had been saving statistics every few
/// seconds was killed mid-flight.
///
/// LifetimeStatsService already wrote through a temp file and renamed it into
/// place, which looks atomic and is not. File.WriteAllText hands the bytes to
/// the operating system's cache and returns; the rename that follows is a
/// metadata change that NTFS journals. Lose power, or lose the process, in
/// the window between them, and the file system can honour the rename while
/// the data behind it was never written - leaving a directory entry of
/// exactly the right length pointing at nothing but zeros. That is not a
/// theory about what happened to that file; it is the shape of the file that
/// arrived.
///
/// So the temp file is written through a FileStream and flushed with
/// Flush(true), which is the only call that makes the operating system put
/// the bytes on the disk rather than merely accept them. Only then is it
/// renamed. A crash before the flush leaves the previous file untouched; a
/// crash after it leaves the new one complete. There is no window in which
/// the live file is a length with no contents.
///
/// It is not free - a flush to disk costs a disk round trip - which is why it
/// is for the small files this app rewrites on a timer, and not for exports,
/// screenshots or anything the user picked a destination for.
/// </summary>
public static class DurableFile
{
    /// <summary>UTF-8 without a byte order mark, matching what
    /// File.WriteAllText produces, so a file written before this existed and
    /// one written after are byte-identical for the same content.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="contents"/>,
    /// durably. Creates the directory if it is missing. Throws what the
    /// underlying file operations throw - every caller here already sits
    /// inside its own try, and a save that silently did nothing would be
    /// worse than one that says so.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        string? directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string temp = path + ".tmp";

        // FileStream rather than File.WriteAllText for exactly one reason:
        // File.WriteAllText offers no way to reach Flush(true).
        using (var stream = new FileStream(
                   temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var writer = new StreamWriter(stream, Utf8NoBom, 1024, leaveOpen: true))
            {
                writer.Write(contents);
                writer.Flush();
            }

            // The line the whole class is for.
            stream.Flush(flushToDisk: true);
        }

        // Move over the top rather than delete-then-move: the rename is the
        // atomic step, and a delete first would open a window where the file
        // does not exist at all.
        File.Move(temp, path, overwrite: true);
    }
}
