using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// §173. The custom opponent editor dialog. A successful save closes with
/// true so the Simulator knows to reload its list; Cancel closes with
/// false - the same bool? contract every other Simulator dialog uses.
/// </summary>
public partial class CustomBossWindow : Window
{
    bool savedWired;

    public CustomBossWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (!savedWired && DataContext is CustomBossViewModel viewModel)
            {
                savedWired = true;
                viewModel.Saved += () => Close(true);

                // §199: every slot's Choose/Browse buttons come back here,
                // because the dialogs belong to the window and the slot
                // view models are built before there is one.
                foreach (CustomBossSlotViewModel slot in viewModel.Slots)
                    slot.RequestSprite = browse => PickSpriteAsync(browse);
            }
        };
    }

    /// <summary>§199. Choose opens the counterpart-skin picker; Browse
    /// opens a file dialog. Either way the answer is a path the roster can
    /// store, or null when the author changed their mind.</summary>
    async Task<string?> PickSpriteAsync(bool browse)
    {
        if (!browse)
        {
            var pickerVm = new SimulatorSpritePickerViewModel();
            var picker = new SimulatorSpritePickerWindow { DataContext = pickerVm };

            bool picked = await picker.ShowDialog<bool?>(this) == true;

            return picked ? pickerVm.SelectedPath : null;
        }

        try
        {
            // §187: a platform whose file dialog is not available says so
            // rather than looking like a dialog that did nothing.
            if (!StorageProvider.CanOpen)
            {
                Serilog.Log.Error(
                    "Custom opponent editor: this platform's open dialog is unavailable " +
                    "(StorageProvider.CanOpen is false) - on Linux this usually means " +
                    "xdg-desktop-portal is not installed or not running");

                Warn("This system has no file picker - type or paste the path into the box instead.");
                return null;
            }

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Pick a sprite",
                AllowMultiple = false,
                FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
            });

            IStorageFile? file = files.FirstOrDefault();
            string? path = file?.TryGetLocalPath();

            if (string.IsNullOrWhiteSpace(path))
            {
                if (file != null)
                    Warn("That file has no path this app can open - copy it into the library folder first.");

                return null;
            }

            return ToPortablePath(path);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Custom opponent editor: the sprite file picker failed.");
            Warn("The file picker failed - see today's log.");
            return null;
        }
    }

    /// <summary>A file inside the application folder is stored the way the
    /// counterparts catalog stores its own - relative, forward-slashed -
    /// so the roster still resolves on someone else's machine, and on a
    /// Linux user's. Anything outside keeps its absolute path, which only
    /// works on the machine it was chosen on; that is the author's choice
    /// to make and the box shows them exactly what was stored.</summary>
    static string ToPortablePath(string absolutePath)
    {
        string root = AppContext.BaseDirectory;

        string full = Path.GetFullPath(absolutePath);
        string baseFull = Path.GetFullPath(root);

        if (!full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase))
            return full;

        return full.Substring(baseFull.Length)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.DirectorySeparatorChar, '/');
    }

    void Warn(string message)
    {
        if (DataContext is CustomBossViewModel viewModel)
            viewModel.Status = message;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
