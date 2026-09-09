using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Foot_Tracker.Models;
using Foot_Tracker.Tracking;
using Foot_Tracker.Tracking.Capture;
using Foot_Tracker.ViewModels;
using SkiaSharp;

namespace Foot_Tracker.Views;

/// <summary>
/// §135. Draw the battle window by hand on the tracker's own capture of the
/// PRO window, check the box against the same title OCR and bar test the
/// trackers use, and save it per client as a fallback for automatic
/// detection - see BattleWindowLocator.TryLocate for how a saved box is used
/// (only when the automatic scan finds nothing) and ManualBattleBounds for
/// why it is stored in pixels.
///
/// The picture is literal on purpose. It is the same CaptureSelectedWindowPng
/// call every tracker makes, decoded once for the checks (SkiaSharp) and once
/// for display (Avalonia), scaled uniformly to fit the screen and nothing
/// else. That makes this window double as "what does the tracker see": a
/// black frame, the wrong window, or a game window partly off the screen
/// appears here exactly as the detectors receive it, which is the one view a
/// screenshot of the main window can never give.
///
/// Coordinates: the overlay canvas is laid out at the displayed size and the
/// frame is a fixed <c>scale</c> factor smaller or larger, so a canvas point
/// divided by <c>scale</c> is a frame pixel. No centring offsets - the image
/// is given the displayed size explicitly rather than left to Stretch.
/// </summary>
public partial class ScreenBoundariesWindow : Window
{
    private const double MaxDisplayWidth = 1000;
    private const double MaxDisplayHeight = 620;

    // A drag narrower than this in frame pixels is a click, not a box.
    private const int MinBoxWidth = 100;

    private readonly MainWindowViewModel? viewModel;

    private readonly Image frameImage;
    private readonly Grid frameGrid;
    private readonly Canvas overlay;
    private readonly Rectangle autoRect;
    private readonly Rectangle savedRect;
    private readonly Rectangle selectionRect;
    private readonly TextBlock resultText;
    private readonly Button useAutoButton;
    private readonly Button saveButton;

    private SKBitmap? frame;
    private double scale = 1;

    private bool dragging;
    private Point dragStart;

    private SKRectI? selection;
    private SKRectI? autoBounds;

    // Set while a capture or an OCR check is running off-thread, so a
    // Refresh cannot dispose the frame a check is still reading.
    private bool busy;

    public ScreenBoundariesWindow()
    {
        InitializeComponent();

        frameImage = this.FindControl<Image>("FrameImage")!;
        frameGrid = this.FindControl<Grid>("FrameGrid")!;
        overlay = this.FindControl<Canvas>("Overlay")!;
        autoRect = this.FindControl<Rectangle>("AutoRect")!;
        savedRect = this.FindControl<Rectangle>("SavedRect")!;
        selectionRect = this.FindControl<Rectangle>("SelectionRect")!;
        resultText = this.FindControl<TextBlock>("ResultText")!;
        useAutoButton = this.FindControl<Button>("UseAutoButton")!;
        saveButton = this.FindControl<Button>("SaveButton")!;

        Closed += (_, _) =>
        {
            // A check may still be reading the frame off-thread if the window
            // is closed mid-check; leave that one to the finalizer rather
            // than pull it out from under native code.
            if (!busy)
                frame?.Dispose();

            frame = null;
        };
    }

    public ScreenBoundariesWindow(MainWindowViewModel mainViewModel) : this()
    {
        viewModel = mainViewModel;

        Opened += async (_, _) => await CaptureFrameAsync();
    }

    // ------------------------------------------------------------------
    // Capture
    // ------------------------------------------------------------------

    private async Task CaptureFrameAsync()
    {
        if (busy)
            return;

        busy = true;

        try
        {
            ClearSelection();
            autoBounds = null;
            autoRect.IsVisible = false;
            savedRect.IsVisible = false;
            useAutoButton.IsEnabled = false;

            IWindowCaptureService capture = WindowCaptureServiceFactory.Instance;

            byte[]? png = await Task.Run(() =>
            {
                // The main window's own auto-detection normally has a client
                // bound long before this window opens. If it has not - PRO
                // was started after the tracker and the 5-second tick has
                // not fired yet - bind the first window the same way it
                // would, without touching profiles or session data.
                if (!capture.HasSelectedClient)
                {
                    IReadOnlyList<ClientWindowInfo> clients = capture.FindClientWindows("PROClient");

                    if (clients.Count > 0)
                        capture.SelectWindow(clients[0].Handle);
                }

                return capture.HasSelectedClient ? capture.CaptureSelectedWindowPng() : null;
            });

            frame?.Dispose();
            frame = null;
            frameImage.Source = null;

            if (png is null || png.Length == 0)
            {
                string reason = string.IsNullOrWhiteSpace(capture.LastError)
                    ? "no PRO client window is open, or it could not be captured"
                    : capture.LastError;

                ShowEmptyFrame($"No capture: {reason}. Open PRO, then click Refresh capture.");
                return;
            }

            SKBitmap? decoded = ImageOps.DecodePng(png);

            if (decoded is null)
            {
                ShowEmptyFrame(
                    $"The capture came back ({png.Length} bytes) but could not be decoded as an image. " +
                    "Click Report a Problem so the raw capture can be looked at.");
                return;
            }

            frame = decoded;

            using (var stream = new MemoryStream(png))
            {
                frameImage.Source = new Bitmap(stream);
            }

            scale = Math.Min(1.0, Math.Min(MaxDisplayWidth / frame.Width, MaxDisplayHeight / frame.Height));

            double displayWidth = Math.Round(frame.Width * scale);
            double displayHeight = Math.Round(frame.Height * scale);

            frameImage.Width = displayWidth;
            frameImage.Height = displayHeight;
            overlay.Width = displayWidth;
            overlay.Height = displayHeight;
            frameGrid.Width = displayWidth;
            frameGrid.Height = displayHeight;

            // What the automatic scan finds on THIS frame - deliberately the
            // scan alone, without the manual fallback, or a saved box would
            // show up here dressed as an automatic result.
            SKBitmap current = frame;

            SKRectI auto = SKRectI.Empty;
            bool autoFound = await Task.Run(() => BattleWindowLocator.TryLocateAutomatically(current, out auto));

            autoBounds = autoFound ? auto : null;

            if (autoFound)
            {
                PlaceRect(autoRect, auto);
                autoRect.IsVisible = true;
                useAutoButton.IsEnabled = true;
            }

            int brightness = ImageOps.MeanBrightness(frame);

            string frameLine =
                $"Captured {frame.Width}x{frame.Height}, mean brightness {brightness}/255" +
                (brightness >= 0 && brightness <= 6
                    ? " - this frame is BLANK. Whatever is drawn here cannot help: the tracker is being handed an empty image. Click Report a Problem."
                    : ".");

            string autoLine = autoFound
                ? $" Automatic detection found the battle window at ({auto.Left},{auto.Top}) {auto.Width}x{auto.Height} on this frame (green box) - if that is right, you do not need manual boundaries."
                : " Automatic detection found no battle window on this frame" +
                  (viewModel?.ManualBattleBounds is null
                      ? " (expected if no battle is open right now)."
                      : ".");

            string savedLine = string.Empty;

            if (viewModel?.ManualBattleBounds is ManualBattleBounds saved)
            {
                // Placed the way the locator places it: re-centred for a
                // frame of a different size, exactly where it was drawn
                // otherwise (see ManualBattleBounds.LeftIn).
                SKRectI savedBox = ImageOps.MakeRect(
                    saved.LeftIn(frame.Width), saved.TopIn(frame.Height), saved.Width, saved.Height);
                SKRectI clamped = ImageOps.Intersect(savedBox, ImageOps.MakeRect(0, 0, frame.Width, frame.Height));

                if (!ImageOps.IsEmpty(clamped))
                {
                    PlaceRect(savedRect, clamped);
                    savedRect.IsVisible = true;
                }

                bool fits = clamped.Width >= saved.Width * 0.9 && clamped.Height >= saved.Height * 0.9;

                savedLine =
                    $" A box is already saved for this client: {saved.Width}x{saved.Height} at ({saved.X},{saved.Y}), " +
                    $"drawn on a {saved.FrameWidth}x{saved.FrameHeight} frame (orange dashed box" +
                    (saved.FrameWidth == frame.Width && saved.FrameHeight == frame.Height
                        ? ")"
                        : $", re-centred for this {frame.Width}x{frame.Height} frame)") +
                    (fits ? "." : " - it does NOT fit the current frame, so it is not being used; draw it again or clear it.");
            }

            resultText.Text = frameLine + autoLine + savedLine;
        }
        catch (Exception ex)
        {
            ShowEmptyFrame($"Could not capture the PRO window: {ex.GetBaseException().Message}");
        }
        finally
        {
            busy = false;
        }
    }

    private void ShowEmptyFrame(string message)
    {
        frameImage.Width = 640;
        frameImage.Height = 120;
        overlay.Width = 640;
        overlay.Height = 120;
        frameGrid.Width = 640;
        frameGrid.Height = 120;

        resultText.Text = message;
        saveButton.IsEnabled = false;
    }

    // ------------------------------------------------------------------
    // Drawing
    // ------------------------------------------------------------------

    private void Overlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (frame is null || busy)
            return;

        if (!e.GetCurrentPoint(overlay).Properties.IsLeftButtonPressed)
            return;

        dragging = true;
        dragStart = ClampToOverlay(e.GetPosition(overlay));

        e.Pointer.Capture(overlay);

        selectionRect.IsVisible = false;
        selection = null;
        saveButton.IsEnabled = false;
    }

    private void Overlay_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!dragging)
            return;

        Point current = ClampToOverlay(e.GetPosition(overlay));

        double left = Math.Min(dragStart.X, current.X);
        double top = Math.Min(dragStart.Y, current.Y);
        double width = Math.Abs(current.X - dragStart.X);

        // Live preview already shows the height the box WILL get, so what
        // the player sees while dragging is what gets saved.
        double height = width / BattleWindowLocator.BattleAspectRatio;

        Canvas.SetLeft(selectionRect, left);
        Canvas.SetTop(selectionRect, top);
        selectionRect.Width = width;
        selectionRect.Height = Math.Min(height, Math.Max(0, overlay.Height - top));
        selectionRect.IsVisible = width > 2;
    }

    private async void Overlay_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!dragging)
            return;

        dragging = false;
        e.Pointer.Capture(null);

        if (frame is null)
            return;

        Point end = ClampToOverlay(e.GetPosition(overlay));

        int left = (int)Math.Round(Math.Min(dragStart.X, end.X) / scale);
        int top = (int)Math.Round(Math.Min(dragStart.Y, end.Y) / scale);
        int width = (int)Math.Round(Math.Abs(end.X - dragStart.X) / scale);

        if (width < MinBoxWidth)
        {
            selectionRect.IsVisible = false;
            resultText.Text =
                $"That box is only {width} pixels wide - a battle window is several hundred. " +
                "Drag from the top-left corner of the dark title bar to its right edge.";
            return;
        }

        SetSelection(BuildBox(left, top, width));

        await EvaluateSelectionAsync(heightWasDerived: true);
    }

    private Point ClampToOverlay(Point p) =>
        new(Math.Clamp(p.X, 0, overlay.Width), Math.Clamp(p.Y, 0, overlay.Height));

    /// <summary>The player supplies left, top and width; the height follows
    /// PRO's fixed battle-window aspect, exactly as BattleWindowLocator's own
    /// BuildCandidate derives it - so a saved box has the same proportions
    /// every detector region was measured against. Clamped to the frame.</summary>
    private SKRectI BuildBox(int left, int top, int width)
    {
        int frameWidth = frame!.Width;
        int frameHeight = frame.Height;

        left = Math.Clamp(left, 0, Math.Max(0, frameWidth - 1));
        top = Math.Clamp(top, 0, Math.Max(0, frameHeight - 1));
        width = Math.Min(width, frameWidth - left);

        int height = (int)Math.Round(width / BattleWindowLocator.BattleAspectRatio);
        height = Math.Min(height, frameHeight - top);

        return ImageOps.MakeRect(left, top, width, height);
    }

    private void SetSelection(SKRectI box)
    {
        selection = box;
        PlaceRect(selectionRect, box);
        selectionRect.IsVisible = true;
        saveButton.IsEnabled = true;
    }

    private void ClearSelection()
    {
        selection = null;
        selectionRect.IsVisible = false;
        saveButton.IsEnabled = false;
    }

    private void PlaceRect(Rectangle rect, SKRectI box)
    {
        Canvas.SetLeft(rect, box.Left * scale);
        Canvas.SetTop(rect, box.Top * scale);
        rect.Width = box.Width * scale;
        rect.Height = box.Height * scale;
    }

    // ------------------------------------------------------------------
    // Checking
    // ------------------------------------------------------------------

    /// <summary>Runs the two tests a saved box will face, on this frame: the
    /// bar test BattleWindowLocator applies before it accepts a manual box,
    /// and the title OCR the encounter detector reads from it. Both off the
    /// UI thread - the OCR shares the engine lock with the live trackers.</summary>
    private async Task EvaluateSelectionAsync(bool heightWasDerived)
    {
        if (frame is null || selection is null || busy)
            return;

        busy = true;

        try
        {
            SKBitmap current = frame;
            SKRectI box = selection.Value;

            bool holds = false;
            string titleText = string.Empty;
            bool looksLikeTitle = false;
            string? matchedPokemon = null;
            string? error = null;

            await Task.Run(() =>
            {
                try
                {
                    holds = BattleWindowLocator.BoxHoldsBattleWindow(current, box);

                    EncounterDetector.TryReadBattleTitle(
                        current, box, out titleText, out looksLikeTitle, out matchedPokemon);
                }
                catch (Exception ex)
                {
                    error = ex.GetBaseException().Message;
                }
            });

            string boxLine =
                $"Box: {box.Width}x{box.Height} at ({box.Left},{box.Top})" +
                (heightWasDerived
                    ? $" - height set from the width (PRO's battle window is {BattleWindowLocator.BattleAspectRatio:0.00}:1)."
                    : ".");

            if (error is not null)
            {
                resultText.Text = boxLine + $" The check itself failed: {error}. You can still save the box.";
                return;
            }

            string titleLine = string.IsNullOrWhiteSpace(titleText)
                ? " No battle title could be read in this box."
                : $" Title read: '{titleText}'" +
                  (matchedPokemon is { Length: > 0 } ? $" - Pokemon: {matchedPokemon}." : ".");

            string verdict = (holds, looksLikeTitle) switch
            {
                (true, true) =>
                    " This box works on this frame: the tracker recognises the battle window in it and reads the title. Save it.",
                (true, false) =>
                    " The tracker recognises a battle-window bar here but no title was read - if no battle is open, open one and click Refresh capture to confirm the box before saving.",
                (false, true) =>
                    " The title reads, but the bar test the tracker applies before using a manual box does NOT pass on this frame, so the saved box may not engage. Save it if you like, then click Report a Problem so this frame can be looked at.",
                _ =>
                    " Nothing is recognised in this box on this frame. If a wild battle is open, move the box so the dark title bar sits along its top edge; if not, open one and click Refresh capture."
            };

            resultText.Text = boxLine + titleLine + verdict;
        }
        finally
        {
            busy = false;
        }
    }

    // ------------------------------------------------------------------
    // Buttons
    // ------------------------------------------------------------------

    private async void RefreshButton_Click(object? sender, RoutedEventArgs e) =>
        await CaptureFrameAsync();

    private async void UseAutoButton_Click(object? sender, RoutedEventArgs e)
    {
        if (frame is null || autoBounds is null || busy)
            return;

        SetSelection(autoBounds.Value);

        await EvaluateSelectionAsync(heightWasDerived: false);
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (frame is null || selection is null || viewModel is null)
            return;

        SKRectI box = selection.Value;

        viewModel.SaveManualBattleBounds(new ManualBattleBounds
        {
            X = box.Left,
            Y = box.Top,
            Width = box.Width,
            Height = box.Height,
            FrameWidth = frame.Width,
            FrameHeight = frame.Height
        });

        Close(true);
    }

    private void ClearButton_Click(object? sender, RoutedEventArgs e)
    {
        if (viewModel is null)
            return;

        viewModel.SaveManualBattleBounds(null);

        savedRect.IsVisible = false;
        resultText.Text = "Saved boundaries cleared - the tracker is back to automatic detection only.";
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}
