using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
// §190: SetTextAsync lives in ClipboardExtensions since the Avalonia 12
// clipboard rework - without this using, IClipboard itself has no text method
// (CS1061). Same note as AdminConsoleWindow.axaml.cs.
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Foot_Tracker.Services;
using Foot_Tracker.Views;
using Serilog;

namespace Foot_Tracker;

public partial class App : Application
{
    public override void Initialize()
    {
        Program.StartupStage = "loading application styles (App.axaml)";
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            // Load persisted data, same as the WinForms constructor did. The
            // stage markers feed Program.cs's crash logging - see its
            // StartupStage remarks and MIGRATION_GUIDE.md §86 for the real
            // "does not load, no error shown" macOS report behind them.
            Program.StartupStage =
                "loading Pokemon data (SharedPokemonLibrary/Data/Pokemon, next to the executable)";
            PokemonSpriteService.Load();

            Program.StartupStage = "loading counterpart sprite data";
            CounterpartSpriteService.Load();

            Program.StartupStage = "loading boss cooldown history";
            BossCooldownService.Load();

            // §277: the per-boss win/loss record, loaded with the cooldowns it
            // sits beside - both are per client and both are read by the Boss
            // Database the moment it opens.
            BossRecordService.Load();

            // §385: the user's own font files, registered before the
            // appearance is applied so a saved font resolves on the first
            // paint rather than after a restart.
            Program.StartupStage = "loading user fonts";
            UserFontService.Load();

            // Push the saved appearance settings into the app's resource dictionary.
            Program.StartupStage = "applying saved appearance settings";
            ThemeManager.Apply();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // §189: every non-modal window is unowned now (see
                // WindowRegistry.ShowUnowned) so that minimizing the tracker
                // does not minimize them too. The default shutdown mode waits
                // for the LAST window to close, which with unowned windows
                // would leave the app running behind an orphaned Boss
                // Cooldowns after the tracker itself was closed. Tying
                // shutdown to the main window keeps closing the tracker
                // meaning what it has always meant. Compact Mode hides the
                // main window rather than closing it, so it is unaffected.
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

                Program.StartupStage = "creating the main window";
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new ViewModels.MainWindowViewModel()
                };
            }

            // §190: startup worked, so any startup-error.txt on this machine
            // is from an earlier run and must not travel in the next report.
            Program.ClearStartupErrorFile();

            Program.StartupStage = "running";
        }
        catch (Exception ex) when (
            ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // A startup step above failed - most plausibly one of the data
            // loads (PokemonSpriteService.Load throws if
            // SharedPokemonLibrary/Data/Pokemon/pokemon-species.json isn't
            // sitting next to the executable, e.g. an app copied out of its
            // publish folder without its data folders, or a partially
            // extracted zip). Before this catch existed, that killed the
            // process before ANY window appeared: "the app does not load,"
            // with the only trace in a log file nobody knows exists. The
            // Avalonia framework is fully initialized by the time this method
            // runs, so showing a plain error window here is safe - and a
            // visible explanation beats a silent exit. The full exception
            // still goes to the crash log either way, stamped with the stage
            // marker saying which step died.
            Log.Fatal(
                ex,
                "Pro Tracker could not start (stage: {StartupStage}).",
                Program.StartupStage);

            // §190: and to one plain text file with a fixed name, written
            // with File.WriteAllText and nothing else. The user this came
            // from was told to send the newest protracker-*.log and found it
            // empty, because the unclean shutdown that emptied the file the
            // app choked on had emptied the log too. A file the window can
            // name exactly, written outside the logging pipeline, is what
            // that report needed.
            Program.WriteStartupErrorFile(Program.StartupStage, ex);

            desktop.MainWindow = BuildStartupErrorWindow(ex);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Built in plain code rather than a .axaml view on purpose: this window
    // exists for the case where startup is already broken, so it should depend
    // on as little of the app's own machinery as possible - no ViewModel, no
    // theme resources beyond what FluentTheme itself provides, no bindings.
    private static Window BuildStartupErrorWindow(Exception ex)
    {
        // §190: the same text the crash file holds, so what gets copied and
        // what gets sent are the same thing.
        string details = Program.BuildStartupErrorText(Program.StartupStage, ex);

        // §190: a status line the three buttons write into. There is nowhere
        // else on this window to say what happened, and a button that appears
        // to do nothing is the complaint that started this section.
        var status = new TextBlock
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            IsVisible = false
        };

        var copyButton = new Button { Content = "Copy details" };
        var logsButton = new Button { Content = "Open the logs folder" };
        var reportButton = new Button { Content = "Save a report" };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { copyButton, logsButton, reportButton }
        };

        var content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Pro Tracker could not start.",
                    FontSize = 18,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "It failed while: " + Program.StartupStage,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = ex.GetType().Name + ": " + ex.Message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new TextBlock
                {
                    // §190: this used to say "send the newest protracker-*.log
                    // from that folder" and nothing else, which is exactly
                    // what failed for the user whose report prompted this -
                    // their log was empty. It names one file now, and the
                    // buttons below mean nobody has to find it by hand.
                    Text =
                        (string.IsNullOrEmpty(Program.StartupErrorFilePath)
                            ? "The full details could not be written to a file, so " +
                              "please use Copy details below and paste them to the developer."
                            : "The full details were written to:\n" +
                              Program.StartupErrorFilePath) +
                        "\n\nSave a report below collects that file, the logs and the " +
                        "settings into one zip - it is the Report a Problem button, for " +
                        "when the app will not open far enough to have one. If this app " +
                        "was copied or moved, make sure the whole publish folder was kept " +
                        "together - the executable needs its SharedPokemonLibrary and " +
                        "DataFiles folders sitting next to it.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                buttons,
                status,
                // A read-only TextBox rather than SelectableTextBlock, and core
                // controls only throughout - this window must not itself become
                // a startup risk, so it sticks to the most basic, stable
                // Avalonia API surface that lets the full error be copied out.
                new TextBox
                {
                    Text = ex.ToString(),
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    FontSize = 11,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                }
            }
        };

        var window = new Window
        {
            Title = "Pro Tracker - startup error",
            Width = 680,
            Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new ScrollViewer
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Content = content
            }
        };

        // §190: every handler reports into the status line and swallows its
        // own failure. Nothing on this window may throw - it is the window
        // that exists because something already did.
        void Report(string message)
        {
            status.Text = message;
            status.IsVisible = true;
        }

        copyButton.Click += async (_, _) =>
        {
            try
            {
                IClipboard? clipboard = TopLevel.GetTopLevel(copyButton)?.Clipboard;

                if (clipboard is null)
                {
                    Report("This system has no clipboard available - select the text below and copy it instead.");
                    return;
                }

                await clipboard.SetTextAsync(details);
                Report("Copied. Paste it wherever you are sending the report.");
            }
            catch (Exception copyError)
            {
                Report("Could not copy: " + copyError.Message);
            }
        };

        logsButton.Click += (_, _) =>
        {
            try
            {
                string folder = string.IsNullOrWhiteSpace(Program.LogFolderPath)
                    ? Path.GetDirectoryName(Program.StartupErrorFilePath) ?? string.Empty
                    : Program.LogFolderPath;

                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    Report("There is no log folder on this machine - use Copy details instead.");
                    return;
                }

                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
            catch (Exception openError)
            {
                Report("Could not open the folder: " + openError.Message);
            }
        };

        reportButton.Click += async (_, _) =>
        {
            reportButton.IsEnabled = false;
            Report("Building the report...");

            try
            {
                // Deliberately not the whole bundle: the diagnostics and data
                // health categories read state the app never got far enough
                // to build, and hunting data is off here for the same reason
                // it is off everywhere else.
                string zip = await SupportBundleService.CreateAsync(new SupportBundleService.BundleOptions
                {
                    IncludeEnvironment = true,
                    IncludeLogs = true,
                    IncludeDiagnostics = false,
                    IncludeDataHealth = false,
                    IncludeSettings = true,
                    IncludeHuntingData = false
                });

                Report("Report saved to:\n" + zip);
            }
            catch (Exception bundleError)
            {
                // The bundle reads a dozen files and any of them can be the
                // reason the app would not start, so the fallback writes the
                // one thing that is definitely available: this window's own
                // text.
                try
                {
                    string downloads = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

                    Directory.CreateDirectory(downloads);

                    string path = Path.Combine(
                        downloads, $"ProTracker-startup-error-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

                    File.WriteAllText(path, details);
                    Report("A full report could not be built (" + bundleError.Message +
                           "), so the details alone were saved to:\n" + path);
                }
                catch (Exception writeError)
                {
                    Report("Could not save a report: " + writeError.Message +
                           ". Use Copy details instead.");
                }
            }
            finally
            {
                reportButton.IsEnabled = true;
            }
        };

        return window;
    }
}
