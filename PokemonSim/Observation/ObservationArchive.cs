using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace PokemonSim.Observation
{
    /// <summary>§169. What an archive says about itself - written into every
    /// export so a bundle picked up months later still names its schema, its
    /// engine mechanics generation and its size.</summary>
    public sealed class ObservationArchiveManifest
    {
        public int Schema { get; set; } = ObservationSchema.Version;
        public string Mechanics { get; set; } = ObservationSchema.MechanicsVersion;
        public string CreatedUtc { get; set; } = "";
        public int BattleFiles { get; set; }
        public long DecisionRecords { get; set; }
        public long UncompressedBytes { get; set; }
    }

    public sealed class ObservationExportResult
    {
        public required string ArchivePath { get; init; }
        public int BattleFiles { get; init; }
        public long DecisionRecords { get; init; }
        public long ArchiveBytes { get; init; }
    }

    public sealed class ObservationImportResult
    {
        public int Added { get; init; }
        public int SkippedExisting { get; init; }
        public int Rejected { get; init; }
        public long DecisionRecords { get; init; }
    }

    /// <summary>
    /// §169. Moving observation data around as ONE file: every obs-*.jsonl
    /// in the observation folder zipped together with a manifest, and the
    /// same bundle merged back in on another machine (or after a Clear).
    /// This is the training corpus - the input the ai-lab trainer turns
    /// into pokemon_ai.onnx - not something a published build carries, so
    /// nothing here touches the shipped model.
    ///
    /// Import is deliberately paranoid about what it will unpack: only
    /// entries whose plain file name matches obs-*.jsonl are taken, path
    /// separators and traversal are rejected outright, and a name already
    /// present is skipped rather than overwritten - so importing the same
    /// bundle twice is a no-op instead of a duplication.
    /// </summary>
    public static class ObservationArchive
    {
        public const string ManifestEntryName = "manifest.json";
        public const string FilePattern = "obs-*.jsonl";

        /// <summary>(battle files, bytes on disk) currently in the folder.</summary>
        public static (int Files, long Bytes) Measure(string observationRoot)
        {
            if (!Directory.Exists(observationRoot))
                return (0, 0);

            string[] files = Directory.GetFiles(observationRoot, FilePattern);
            long bytes = 0;

            foreach (string file in files)
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    // A battle still writing - its size is not worth failing over.
                }
            }

            return (files.Length, bytes);
        }

        /// <summary>Zips every observation file plus a manifest into
        /// destinationZipPath (replacing an existing archive there).</summary>
        public static ObservationExportResult Export(
            string observationRoot, string destinationZipPath, CancellationToken cancellationToken = default)
        {
            string[] files = Directory.Exists(observationRoot)
                ? Directory.GetFiles(observationRoot, FilePattern).OrderBy(f => f, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();

            string? parent = Path.GetDirectoryName(destinationZipPath);

            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            long records = 0, uncompressed = 0;
            int written = 0;

            // Build beside the target, then move into place, so a cancelled
            // or failed export never leaves a half-written archive behind.
            string temporary = destinationZipPath + ".partial";

            if (File.Exists(temporary))
                File.Delete(temporary);

            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    foreach (string file in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            // Read first: a file the lab is still writing
                            // is skipped rather than sinking the export.
                            byte[] content = File.ReadAllBytes(file);

                            ZipArchiveEntry entry = archive.CreateEntry(
                                Path.GetFileName(file), CompressionLevel.Optimal);

                            using (Stream target = entry.Open())
                                target.Write(content, 0, content.Length);

                            records += CountDecisions(content);
                            uncompressed += content.Length;
                            written++;
                        }
                        catch (IOException)
                        {
                            // Locked by a running battle - skip it.
                        }
                    }

                    var manifest = new ObservationArchiveManifest
                    {
                        CreatedUtc = DateTime.UtcNow.ToString("O"),
                        BattleFiles = written,
                        DecisionRecords = records,
                        UncompressedBytes = uncompressed
                    };

                    ZipArchiveEntry manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);

                    using (var writer = new StreamWriter(manifestEntry.Open()))
                        writer.Write(JsonSerializer.Serialize(manifest));
                }

                if (File.Exists(destinationZipPath))
                    File.Delete(destinationZipPath);

                File.Move(temporary, destinationZipPath);
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }

            return new ObservationExportResult
            {
                ArchivePath = destinationZipPath,
                BattleFiles = written,
                DecisionRecords = records,
                ArchiveBytes = new FileInfo(destinationZipPath).Length
            };
        }

        /// <summary>Merges an exported archive into the observation folder.
        /// Files already there keep what they have.</summary>
        public static ObservationImportResult Import(
            string archiveZipPath, string observationRoot, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(observationRoot);

            int added = 0, skipped = 0, rejected = 0;
            long records = 0;

            using var archive = ZipFile.OpenRead(archiveZipPath);

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.Equals(entry.FullName, ManifestEntryName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!IsSafeObservationName(entry.FullName))
                {
                    rejected++;
                    continue;
                }

                string destination = Path.Combine(observationRoot, entry.Name);

                if (File.Exists(destination))
                {
                    skipped++;
                    continue;
                }

                using (Stream source = entry.Open())
                using (var memory = new MemoryStream())
                {
                    source.CopyTo(memory);
                    byte[] content = memory.ToArray();

                    File.WriteAllBytes(destination, content);
                    records += CountDecisions(content);
                }

                added++;
            }

            return new ObservationImportResult
            {
                Added = added,
                SkippedExisting = skipped,
                Rejected = rejected,
                DecisionRecords = records
            };
        }

        /// <summary>The manifest of an archive, or null when it carries
        /// none (an older or hand-made bundle).</summary>
        public static ObservationArchiveManifest? ReadManifest(string archiveZipPath)
        {
            try
            {
                using var archive = ZipFile.OpenRead(archiveZipPath);

                ZipArchiveEntry? entry = archive.GetEntry(ManifestEntryName);

                if (entry == null)
                    return null;

                using var reader = new StreamReader(entry.Open());

                return JsonSerializer.Deserialize<ObservationArchiveManifest>(reader.ReadToEnd());
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                return null;
            }
        }

        /// <summary>An entry may become a file in the observation folder
        /// only when it is a bare obs-*.jsonl name - no directories, no
        /// traversal, no rooted paths.</summary>
        public static bool IsSafeObservationName(string entryName)
        {
            if (string.IsNullOrWhiteSpace(entryName))
                return false;

            if (entryName.Contains('/') || entryName.Contains('\\') ||
                entryName.Contains("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(entryName))
            {
                return false;
            }

            string name = Path.GetFileName(entryName);

            return name.Length > 0 &&
                   name == entryName &&
                   name.StartsWith("obs-", StringComparison.Ordinal) &&
                   name.EndsWith(".jsonl", StringComparison.Ordinal);
        }

        static long CountDecisions(byte[] content)
        {
            // The same marker ObservationStore counts by, over bytes so no
            // hundred-megabyte string ever materializes.
            ReadOnlySpan<byte> marker = "\"Record\":\"decision\""u8;
            ReadOnlySpan<byte> span = content;

            long found = 0;
            int index;

            while ((index = span.IndexOf(marker)) >= 0)
            {
                found++;
                span = span[(index + marker.Length)..];
            }

            return found;
        }

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // Nothing more to do - the caller is already failing.
            }
        }
    }
}