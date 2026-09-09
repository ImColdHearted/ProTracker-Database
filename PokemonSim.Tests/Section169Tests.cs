using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using PokemonSim.Observation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>Section 169: the observation archive - one file in, one
    /// file out, and an import that will not be talked into writing
    /// anywhere but the observation folder. Every test works in its own
    /// throwaway directories.</summary>
    public class Section169Tests : IDisposable
    {
        readonly string root = Path.Combine(
            Path.GetTempPath(), "protracker-archive-tests", Guid.NewGuid().ToString("N"));

        string Source => Path.Combine(root, "source");
        string Target => Path.Combine(root, "target");
        string ArchivePath => Path.Combine(root, "bundle.zip");

        public Section169Tests()
        {
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Target);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { /* best effort */ }
        }

        /// <summary>A battle file with the given number of decision records
        /// plus one final record, shaped like the observer's own output.</summary>
        void WriteBattleFile(string name, int decisions)
        {
            var text = new StringBuilder();

            for (int i = 0; i < decisions; i++)
                text.Append("{\"Record\":\"decision\",\"Turn\":").Append(i + 1).Append("}\n");

            text.Append("{\"Record\":\"final\",\"Turns\":").Append(decisions).Append("}\n");

            File.WriteAllText(Path.Combine(Source, name), text.ToString());
        }

        [Fact]
        public void Export_ThenImport_CarriesEveryFileAndRecord()
        {
            WriteBattleFile("obs-a.jsonl", 3);
            WriteBattleFile("obs-b.jsonl", 5);

            // Anything that is not an observation file stays behind.
            File.WriteAllText(Path.Combine(Source, "observer-settings.json"), "{}");

            ObservationExportResult exported = ObservationArchive.Export(Source, ArchivePath);

            Assert.Equal(2, exported.BattleFiles);
            Assert.Equal(8L, exported.DecisionRecords);
            Assert.True(File.Exists(ArchivePath));

            ObservationImportResult imported = ObservationArchive.Import(ArchivePath, Target);

            Assert.Equal(2, imported.Added);
            Assert.Equal(0, imported.SkippedExisting);
            Assert.Equal(8L, imported.DecisionRecords);

            Assert.Equal(
                File.ReadAllText(Path.Combine(Source, "obs-a.jsonl")),
                File.ReadAllText(Path.Combine(Target, "obs-a.jsonl")));

            Assert.False(File.Exists(Path.Combine(Target, "observer-settings.json")));
        }

        [Fact]
        public void ImportingTheSameBundleTwice_AddsNothingTheSecondTime()
        {
            WriteBattleFile("obs-a.jsonl", 2);
            ObservationArchive.Export(Source, ArchivePath);

            ObservationArchive.Import(ArchivePath, Target);
            ObservationImportResult again = ObservationArchive.Import(ArchivePath, Target);

            Assert.Equal(0, again.Added);
            Assert.Equal(1, again.SkippedExisting);
            Assert.Single(Directory.GetFiles(Target, "obs-*.jsonl"));
        }

        [Fact]
        public void Import_RefusesEntriesThatAreNotPlainObservationFiles()
        {
            using (var stream = new FileStream(ArchivePath, FileMode.Create))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                // A traversal attempt, a nested path, and a wrong extension.
                foreach (string bad in new[] { "../obs-escape.jsonl", "nested/obs-inner.jsonl", "obs-notes.txt" })
                {
                    using var writer = new StreamWriter(archive.CreateEntry(bad).Open());
                    writer.Write("{\"Record\":\"decision\"}\n");
                }

                using var good = new StreamWriter(archive.CreateEntry("obs-good.jsonl").Open());
                good.Write("{\"Record\":\"decision\"}\n");
            }

            ObservationImportResult result = ObservationArchive.Import(ArchivePath, Target);

            Assert.Equal(1, result.Added);
            Assert.Equal(3, result.Rejected);
            Assert.Equal(new[] { "obs-good.jsonl" },
                Directory.GetFiles(Target).Select(Path.GetFileName).ToArray());
            Assert.False(File.Exists(Path.Combine(root, "obs-escape.jsonl")));
        }

        [Fact]
        public void SafeNameRule_TakesBareObservationNamesOnly()
        {
            Assert.True(ObservationArchive.IsSafeObservationName("obs-20260902-abc.jsonl"));

            Assert.False(ObservationArchive.IsSafeObservationName("../obs-a.jsonl"));
            Assert.False(ObservationArchive.IsSafeObservationName("dir/obs-a.jsonl"));
            Assert.False(ObservationArchive.IsSafeObservationName("dir\\obs-a.jsonl"));
            Assert.False(ObservationArchive.IsSafeObservationName("obs-a.txt"));
            Assert.False(ObservationArchive.IsSafeObservationName("settings.json"));
            Assert.False(ObservationArchive.IsSafeObservationName(""));
        }

        [Fact]
        public void TheManifest_DescribesWhatTheBundleHolds()
        {
            WriteBattleFile("obs-a.jsonl", 4);
            ObservationArchive.Export(Source, ArchivePath);

            ObservationArchiveManifest? manifest = ObservationArchive.ReadManifest(ArchivePath);

            Assert.NotNull(manifest);
            Assert.Equal(ObservationSchema.Version, manifest!.Schema);
            Assert.Equal(ObservationSchema.MechanicsVersion, manifest.Mechanics);
            Assert.Equal(1, manifest.BattleFiles);
            Assert.Equal(4L, manifest.DecisionRecords);
            Assert.True(manifest.UncompressedBytes > 0);
            Assert.True(DateTime.TryParse(manifest.CreatedUtc, out _));
        }

        [Fact]
        public void Measure_CountsOnlyObservationFiles()
        {
            WriteBattleFile("obs-a.jsonl", 1);
            WriteBattleFile("obs-b.jsonl", 1);
            File.WriteAllText(Path.Combine(Source, "observer-settings.json"), "{}");

            (int files, long bytes) = ObservationArchive.Measure(Source);

            Assert.Equal(2, files);
            Assert.True(bytes > 0);
            Assert.Equal((0, 0L), ObservationArchive.Measure(Path.Combine(root, "does-not-exist")));
        }

        [Fact]
        public void ExportingAnEmptyFolder_StillProducesAReadableBundle()
        {
            ObservationExportResult exported = ObservationArchive.Export(Source, ArchivePath);

            Assert.Equal(0, exported.BattleFiles);
            Assert.Equal(0L, exported.DecisionRecords);
            Assert.NotNull(ObservationArchive.ReadManifest(ArchivePath));
            Assert.Equal(0, ObservationArchive.Import(ArchivePath, Target).Added);
            Assert.False(File.Exists(ArchivePath + ".partial"));
        }
    }
}