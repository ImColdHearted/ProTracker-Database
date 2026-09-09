using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;
using Foot_Tracker.Services.Simulator;
using Foot_Tracker.Tracking.Capture;
using PokemonSim.Data;
using PokemonSim.Simulation;
using Serilog;
using SkiaSharp;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// §162. The screenshot importer: capture the PRO client (through the same
/// cross-platform backend the hunt uses), let the auto-detector find the
/// summary card - or drag a box around it - and scan. The pixel work is CardOcrService, the text work is the engine's
/// CardImportParser; this class holds the picture, the selection, and the
/// parsed preview the Add button confirms. Every successful scan also
/// lands in the Pokemon storage, so the team builder's "From Storage"
/// button can re-offer it forever. §165: capture runs the scan itself
/// whenever the card is auto-detected - one click does the whole read; the
/// Scan button stays for drag-box corrections. §221: the Browse button is
/// gone from the window, so an import is always a live read of the
/// player's own client. Browse_Click still exists in the code-behind,
/// unwired - the handler was left in place, only the button was taken out. §198: Add
/// no longer closes this window. It banks the Pokemon, clears the preview
/// and leaves the window open, so a boxful can be scanned in one sitting;
/// the whole batch goes back to the team builder when Done is pressed.
/// </summary>
public sealed partial class SimulatorImportViewModel : ViewModelBase, IDisposable
{
    readonly ISpeciesSource speciesSource = new TrackerSpeciesSource();

    // §225. How long a live capture keeps looking. §165/§166 already read
    // every hard field several ways, but all of those reads share one
    // frame, so a card caught mid-render or under a drifting tooltip
    // poisons all of them at once. These give the read new pixels to vote
    // on. Budget-driven rather than count-driven on purpose: the promise
    // made to the user is a couple of seconds, so a slow machine
    // contributes fewer frames instead of taking longer than it said.
    const int SweepBudgetMs = 2500;

    // Minimum wait before each extra grab. Two captures taken back to back
    // are the same picture, which would buy nothing but OCR time.
    const int SweepIntervalMs = 400;

    // Hard ceiling regardless of how fast the machine is - past about this
    // many frames of the same still card there is nothing left to learn.
    const int SweepMaxFrames = 4;

    SKBitmap? source;
    CardFrame? frame;

    [ObservableProperty] private Bitmap? screenshot;
    [ObservableProperty] private bool hasImage;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private string statusText =
        "Open your Pokemon's summary card in PRO, then capture the client.";

    [ObservableProperty] private string selectionText = "";

    [ObservableProperty] private bool hasPreview;
    [ObservableProperty] private Bitmap? previewSprite;
    [ObservableProperty] private string previewHeadline = "";
    [ObservableProperty] private string previewMoves = "";
    [ObservableProperty] private string previewSpread = "";
    [ObservableProperty] private string previewNotes = "";

    // §198: the running tally of this sitting, and a button label that
    // says the truth once the team is full.
    [ObservableProperty] private string addedSummary = "";
    [ObservableProperty] private bool hasAdded;
    [ObservableProperty] private string addButtonText = "Add to Team";

    /// <summary>The last successful scan - what Add confirms.</summary>
    public ImportedPokemon? Current { get; private set; }

    /// <summary>§198: every Pokemon added while this window has been open,
    /// in the order they were added. The team builder reads this when the
    /// window closes.</summary>
    public List<ImportedPokemon> Added { get; } = new();

    /// <summary>§198: how many team slots were free when this window
    /// opened, counted down as each Add banks one. It changes nothing
    /// about what happens - every scan is stored either way - it only lets
    /// the status line and the Add button say where the next one lands.
    /// </summary>
    public int TeamSpaceRemaining { get; set; }

    /// <summary>The user's drawn box in BITMAP coordinates (the window's
    /// code-behind converts from display coordinates), or null for the
    /// auto-detector's whole-image search.</summary>
    public SKRectI? Selection { get; private set; }

    /// <summary>OCR name dictionary: the full game move list plus the
    /// engine's own (either alone would misname moves the other knows).</summary>
    static List<string> BuildMoveDictionary()
    {
        MoveDex.EnsureLoaded();

        return MoveDex.AllNames()
            .Concat(MoveLookupService.AllMoves.Select(m => m.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>§198: called by the window once TeamSpaceRemaining has
    /// been handed over, so the Add button is labelled correctly before
    /// the first scan rather than after the first add.</summary>
    public void Begin()
    {
        RefreshAddedSummary();
    }

    public void SetSelection(SKRectI? selection)
    {
        Selection = selection;

        SelectionText = selection == null
            ? ""
            : $"Box: {selection.Value.Width} x {selection.Value.Height} px - Scan reads inside it.";
    }

    /// <summary>A new picture from either source. Runs the auto-detector
    /// right away so the user sees whether the card was spotted.</summary>
    public void LoadImage(byte[] imageBytes)
    {
        try
        {
            SKBitmap? decoded = SKBitmap.Decode(imageBytes);

            if (decoded == null)
            {
                StatusText = "That file could not be read as an image.";
                return;
            }

            source?.Dispose();
            source = decoded;

            Screenshot = new Bitmap(new MemoryStream(imageBytes));
            HasImage = true;
            SetSelection(null);
            HasPreview = false;
            Current = null;

            frame = CardOcrService.FindCard(source);

            StatusText = frame != null
                ? "Summary card found - reading it..."
                : "No summary card auto-detected - drag a box around the card, then press Scan.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Card importer: could not load an image.");
            StatusText = "That image could not be loaded.";
        }
    }

    [RelayCommand]
    private async Task Capture()
    {
        if (Busy)
            return;

        Busy = true;
        StatusText = "Capturing the PRO client...";

        bool captured = false;

        try
        {
            // The worker only computes - every property write happens back
            // here on the UI thread.
            (byte[]? png, string? error) = await Task.Run(CaptureClientPng);

            if (png == null)
            {
                StatusText = error ?? "The PRO client window could not be captured.";
                return;
            }

            LoadImage(png);
            captured = true;
        }
        finally
        {
            Busy = false;
        }

        // §165: capture IS the auto-detect - when the card was spotted the
        // scan runs in the same click. (After the Busy guard is released;
        // the scan takes it again itself.) §225: and only this path sweeps
        // for more frames, because only this path has a live client behind
        // it - the Scan button re-reads a picture already taken, and the
        // browse path has no client at all.
        if (captured && frame != null)
            await ScanCoreAsync(sweepLiveFrames: true);
    }

    /// <summary>§165: the browse path's version of the same one-click
    /// flow - load the picture and, when the card is auto-detected, scan
    /// without another press.</summary>
    public async Task LoadAndAutoScanAsync(byte[] imageBytes)
    {
        LoadImage(imageBytes);

        if (frame != null)
            await Scan();
    }

    static (byte[]? Png, string? Error) CaptureClientPng()
    {
        try
        {
            IWindowCaptureService service = WindowCaptureServiceFactory.Instance;

            if (!service.IsAvailable)
                return (null, $"Window capture is not available on this system ({service.PlatformName}).");

            bool temporary = false;

            if (!service.HasSelectedClient)
            {
                IReadOnlyList<ClientWindowInfo> windows = service.FindClientWindows("PROClient");

                if (windows.Count == 0)
                    return (null, "No PRO client window found - is the game running and not minimized?");

                service.SelectWindow(windows[0].Handle);
                temporary = true;
            }

            try
            {
                byte[]? png = service.CaptureSelectedWindowPng();

                return (png, png == null
                    ? service.LastError ?? "The PRO client window could not be captured."
                    : null);
            }
            finally
            {
                // A client bound by the tracker stays bound; one picked
                // here just for this capture is released again.
                if (temporary)
                    service.ClearSelectedWindow();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Card importer: capture failed.");
            return (null, "The capture failed - see today's log.");
        }
    }

    /// <summary>§225. Keeps grabbing the client while the budget lasts,
    /// adding each new frame's reads to the same candidate lists §166
    /// already votes across.
    ///
    /// Three things it deliberately does not do. A frame whose capture
    /// fails is skipped rather than ending the sweep - the client being
    /// minimised part way through should cost the extra ballots, not the
    /// import. A frame the card cannot be found in is skipped as well. And
    /// the first frame's geometry is never reused on a later bitmap: if the
    /// window moved, those coordinates point at the wrong pixels, and
    /// reading them would feed the vote confident nonsense instead of
    /// nothing, which is worse than the miss it replaced.</summary>
    static async Task SweepAsync(CardOcrTexts texts, IProgress<string> progress)
    {
        long started = Environment.TickCount64;
        int added = 0;

        for (int attempt = 0; attempt < SweepMaxFrames; attempt++)
        {
            // Checked before the wait, so the sweep can never start a frame
            // it has no time left for.
            if (Environment.TickCount64 - started >= SweepBudgetMs)
                break;

            await Task.Delay(SweepIntervalMs).ConfigureAwait(false);

            (byte[]? png, string? _) = CaptureClientPng();

            if (png == null)
                continue;

            using SKBitmap? extra = SKBitmap.Decode(png);

            if (extra == null)
                continue;

            CardFrame? target = CardOcrService.FindCard(extra);

            if (target == null)
                continue;

            CardOcrService.AppendCandidates(extra, target, texts);
            added++;

            progress.Report(
                $"Double-checking the digits - {added + 1} reads of the card so far...");
        }

        Log.Information(
            "Card importer: the multi-frame sweep added {Added} extra frame(s) in {Elapsed}ms.",
            added,
            Environment.TickCount64 - started);
    }

    /// <summary>The Scan button, and the browse path's auto-scan: re-read
    /// the picture that is already loaded. No sweep - there is no live
    /// client to take another frame from, and re-reading the same bitmap
    /// four times would only spend OCR to reach the same answer.</summary>
    [RelayCommand]
    private async Task Scan()
    {
        await ScanCoreAsync(sweepLiveFrames: false);
    }

    private async Task ScanCoreAsync(bool sweepLiveFrames)
    {
        if (Busy || source == null)
            return;

        SKBitmap bitmap = source;
        SKRectI? selection = Selection;

        Busy = true;
        HasPreview = false;
        Current = null;
        StatusText = sweepLiveFrames
            ? "Reading the card - give it a couple of seconds while the digits are double-checked..."
            : "Reading the card...";

        // Constructed here, on the UI thread, so its callback comes back
        // here too - the worker below still writes no property directly.
        var progress = new Progress<string>(text => StatusText = text);

        try
        {
            ImportedPokemon? imported = await Task.Run(async () =>
            {
                CardFrame? target = selection != null
                    ? CardOcrService.FindCard(bitmap, selection)
                    : frame ?? CardOcrService.FindCard(bitmap);

                if (target == null)
                    return null;

                frame = target;

                CardOcrTexts texts = CardOcrService.ReadCard(bitmap, target);

                // §225. More frames of the same card, pooled into the same
                // candidate lists §166 already votes across.
                if (sweepLiveFrames)
                    await SweepAsync(texts, progress).ConfigureAwait(false);

                ImportedPokemon parsed = CardImportParser.Parse(
                    texts, speciesSource, BuildMoveDictionary(), AbilityLookupService.AllNames);

                // §164: the golden S badge on the title ball marks a shiny.
                parsed.IsShiny = CardOcrService.DetectShinyBadge(bitmap, target);

                return parsed;
            });

            if (imported == null)
            {
                StatusText = Selection != null
                    ? "No summary card found inside the box - draw it around the whole card."
                    : "No summary card found - drag a box around the card and scan again.";
                return;
            }

            if (string.IsNullOrEmpty(imported.SpeciesName))
            {
                StatusText = "The card was found but the species name did not read - try a cleaner screenshot.";
                return;
            }

            Current = imported;
            ShowPreview(imported);
            StatusText = "Check the preview, then Add to Team.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Card importer: the scan failed.");
            StatusText = "The scan failed - see today's log.";
        }
        finally
        {
            Busy = false;
        }
    }

    void ShowPreview(ImportedPokemon imported)
    {
        PreviewHeadline =
            (imported.IsShiny ? "Shiny " : "") +
            $"{imported.SpeciesName}  Lv. {imported.Level}  {imported.NatureName}" +
            (string.IsNullOrWhiteSpace(imported.AbilityName) ? "" : $"  -  {imported.AbilityName}");

        PreviewMoves = imported.MoveNames.Count > 0
            ? string.Join("  /  ", imported.MoveNames)
            : "(no moves read)";

        // §167: shown in the card's own row order so the preview verifies
        // against the game at a glance.
        PreviewSpread = imported.CardOrderSpread;

        PreviewNotes = imported.Notes.Count > 0
            ? string.Join("\n", imported.Notes.Select(n => "- " + n))
            : "Every field verified cleanly.";

        HasPreview = true;
        PreviewSprite = null;

        string species = imported.SpeciesName;
        bool shiny = imported.IsShiny;

        _ = Task.Run(() =>
        {
            Bitmap? sprite = shiny
                ? PokemonSpriteService.GetShinyEncounterSprite(species)
                : PokemonSpriteService.GetEncounterSprite(species);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (Current?.SpeciesName == species)
                    PreviewSprite = sprite;
            });
        });
    }

    /// <summary>§198: bank the scan and stay open. The window used to
    /// close here, which made importing a box a matter of reopening this
    /// dialog once per Pokemon.</summary>
    [RelayCommand]
    private void Confirm()
    {
        if (Current == null)
            return;

        ImportedPokemon added = Current;
        bool replaced = false;
        bool stored = true;

        try
        {
            (_, replaced) = SimulatorPokemonStorage.Upsert(added);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Card importer: storage save failed.");
            stored = false;
        }

        Added.Add(added);

        string savedAs = !stored
            ? "could not be written to storage (see today's log)"
            : replaced
                ? "updated in storage"
                : "saved to storage";

        if (TeamSpaceRemaining > 0)
        {
            TeamSpaceRemaining--;

            StatusText =
                $"{added.SpeciesName} {savedAs} and joins the team. " +
                "Scan the next one, or press Done.";
        }
        else
        {
            StatusText =
                $"{added.SpeciesName} {savedAs}. The team is already full, so it waits there - " +
                "swap it in with a card's Replace from Storage. Scan the next one, or press Done.";
        }

        RefreshAddedSummary();

        // The preview is cleared so the next card starts from a blank one
        // and Add cannot bank the same Pokemon twice.
        Current = null;
        HasPreview = false;
        PreviewSprite = null;
    }

    void RefreshAddedSummary()
    {
        HasAdded = Added.Count > 0;

        AddButtonText = TeamSpaceRemaining > 0 ? "Add to Team" : "Add to Storage";

        if (Added.Count == 0)
        {
            AddedSummary = "";
            return;
        }

        // Long sittings list the most recent few rather than growing a
        // paragraph down the side of the window.
        const int Shown = 8;

        IEnumerable<string> names = Added.Select(a => a.SpeciesName);

        AddedSummary = Added.Count <= Shown
            ? $"Added this sitting ({Added.Count}): {string.Join(", ", names)}"
            : $"Added this sitting ({Added.Count}): {string.Join(", ", names.Skip(Added.Count - Shown))}" +
              $" (and {Added.Count - Shown} before those)";
    }

    public void Dispose()
    {
        source?.Dispose();
        source = null;
    }
}
