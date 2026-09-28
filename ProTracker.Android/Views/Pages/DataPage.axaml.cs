using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Foot_Tracker.Services;
using Serilog;

namespace ProTracker.Companion.Views.Pages;

public partial class DataPage : UserControl
{
    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProTracker");

    public DataPage()
    {
        InitializeComponent();

        VersionText.Text = "Companion " + (typeof(DataPage).Assembly.GetName().Version?.ToString(3) ?? "?");
        DataFolderText.Text = "Data: " + DataRoot;
        BundleFolderText.Text = "Bundle: " + AppContext.BaseDirectory;

        if (Bootstrap.StartupError is not null)
        {
            ErrorPanel.IsVisible = true;
            ErrorText.Text = Bootstrap.StartupError;
        }
    }

    private async void Import_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            TopLevel? top = TopLevel.GetTopLevel(this);

            if (top is null)
                return;

            IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "ProTracker data zip",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Zip") { Patterns = new[] { "*.zip" }, MimeTypes = new[] { "application/zip" } } },
            });

            if (files.Count == 0)
                return;

            ImportStatus.Text = "Importing…";

            int count;

            await using (Stream stream = await files[0].OpenReadAsync())
            {
                count = await Task.Run(() => Unpack(stream));
            }

            // The per-client stores read their files once; tell them the
            // files changed.
            Bootstrap.LoadServices();
            BossRecordService.ReloadForActiveClient();
            PvpOpponentService.ReloadForActiveClient();
            HuntLogService.ReloadForActiveClient();

            ImportStatus.Text = $"Imported {DisplayNumber.Count(count)} files. Every tab now reads the new copy.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Import failed.");
            ImportStatus.Text = "Import failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Unpacks the zip into the phone's ProTracker folder. The zip is
    /// whatever the PC's Compress made of %LocalAppData%\ProTracker, so its
    /// entries may or may not start with "ProTracker/"; both land in the same
    /// place. Entries that would escape the folder are refused - a zip is
    /// untrusted input even when the user made it.
    /// </summary>
    internal static int Unpack(Stream zipStream)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        string root = Path.GetFullPath(DataRoot + Path.DirectorySeparatorChar);
        int written = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string relative = entry.FullName.Replace('\\', '/');

            if (relative.StartsWith("ProTracker/", StringComparison.OrdinalIgnoreCase))
                relative = relative["ProTracker/".Length..];

            if (relative.Length == 0 || relative.EndsWith('/'))
                continue;

            // Logs and lock files belong to the machine that made them.
            if (relative.StartsWith("Logs/", StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string destination = Path.GetFullPath(Path.Combine(root, relative));

            if (!destination.StartsWith(root, StringComparison.Ordinal))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using Stream source = entry.Open();
            using FileStream target = File.Create(destination);
            source.CopyTo(target);
            written++;
        }

        return written;
    }
}
