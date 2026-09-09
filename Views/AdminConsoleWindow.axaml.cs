using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
// SetTextAsync lives in ClipboardExtensions since the Avalonia 12 clipboard
// rework - without this using, IClipboard itself has no text method (CS1061).
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// The §101 Admin Console shell - constructs its own AdminConsoleViewModel
/// (same self-contained pattern as PreviouslyBattledUsersWindow), wires the
/// screenshot picker the OCR inspector needs, and starts/stops the 1-second
/// status refresh with the window's own lifetime so a closed console costs
/// the app nothing. Only reachable from AdminActionsWindow, which itself
/// sits behind Admin Login; the view model additionally shows its locked
/// notice if this process has not authenticated.
/// </summary>
public partial class AdminConsoleWindow : Window
{
    public AdminConsoleWindow()
    {
        InitializeComponent();

        var vm = new AdminConsoleViewModel();
        DataContext = vm;

        // §150/§153: the events-server sign-in (an admin login or the
        // master token), asked for once per run - the same window Create
        // Event and Remove Event use.
        vm.RequestAdminSignIn = () => AdminTokenWindow.ShowAsync(this);

        vm.RequestScreenshotPaths = async multiple =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = multiple ? "Inspect Screenshots" : "Inspect Screenshot",
                AllowMultiple = multiple,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("PNG Screenshot") { Patterns = new[] { "*.png" } }
                }
            });

            return files
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .ToArray();
        };

        // §103 "Screenshot Tracker": renders the app's MAIN window (the
        // tracker itself) to a PNG in the Diagnostics folder - the same
        // second half Report a Problem captures - and hands the bitmap back
        // for the inspector's preview pane. View-side because rendering a
        // Window needs the visual tree.
        vm.RequestTrackerScreenshot = async () =>
        {
            Window? mainWindow =
                (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

            if (mainWindow is null)
                return null;

            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProTracker", "Diagnostics");

            Directory.CreateDirectory(folder);

            string path = Path.Combine(folder, $"tracker-window-{DateTime.Now:yyyyMMdd-HHmmss}.png");

            var pixelSize = new PixelSize(
                Math.Max(1, (int)(mainWindow.Bounds.Width * mainWindow.RenderScaling)),
                Math.Max(1, (int)(mainWindow.Bounds.Height * mainWindow.RenderScaling)));

            var dpi = new Vector(96 * mainWindow.RenderScaling, 96 * mainWindow.RenderScaling);

            using (var rendered = new RenderTargetBitmap(pixelSize, dpi))
            {
                rendered.Render(mainWindow);

                await using var stream = File.Create(path);
                rendered.Save(stream);
            }

            return (path, new Bitmap(path));
        };

        // §169: the Battle Lab's training-corpus dialogs.
        vm.RequestArchiveSavePath = async suggestedName =>
        {
            IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Observation Data",
                SuggestedFileName = suggestedName,
                DefaultExtension = "zip",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Observation Archive") { Patterns = new[] { "*.zip" } }
                }
            });

            return file?.TryGetLocalPath();
        };

        vm.RequestArchiveOpenPath = async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Observation Data",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Observation Archive") { Patterns = new[] { "*.zip" } }
                }
            });

            return files.FirstOrDefault()?.TryGetLocalPath();
        };

        vm.RequestModelPath = async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Install Trained Model",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("ONNX Model") { Patterns = new[] { "*.onnx" } }
                }
            });

            return files.FirstOrDefault()?.TryGetLocalPath();
        };

        Opened += (_, _) => vm.StartRefresh();

        Closed += (_, _) =>
        {
            vm.StopRefresh();
            vm.Dispose();
        };
    }

    private async void CopySummaryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AdminConsoleViewModel vm || Clipboard is null)
            return;

        try
        {
            await Clipboard.SetTextAsync(vm.BuildDiagnosticSummary());
        }
        catch
        {
            // Clipboard access can fail on some platforms - nothing worth
            // interrupting the console over.
        }
    }
}
