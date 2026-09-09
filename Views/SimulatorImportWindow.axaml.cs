using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Foot_Tracker.ViewModels;
using SkiaSharp;

namespace Foot_Tracker.Views;

/// <summary>
/// §162. Code-behind for the screenshot importer: the file browser, the
/// dialog result, and the drag-a-box overlay - pointer positions on the
/// displayed (letterboxed, uniformly scaled) image are converted back to
/// bitmap pixels for the view model, which is the only coordinate space
/// the OCR side ever sees.
/// </summary>
public partial class SimulatorImportWindow : Window
{
    // Resolved by name, the same way SimulatorWindow finds its log list -
    // no reliance on the generated-field name provider.
    readonly Panel imagePanel;
    readonly Border selectionBox;

    Point dragStart;
    bool dragging;

    public SimulatorImportWindow()
    {
        InitializeComponent();

        imagePanel = this.FindControl<Panel>("ImagePanel")!;
        selectionBox = this.FindControl<Border>("SelectionBox")!;

        imagePanel.PointerPressed += Image_PointerPressed;
        imagePanel.PointerMoved += Image_PointerMoved;
        imagePanel.PointerReleased += Image_PointerReleased;

        // §198: Add no longer closes this window - it banks the scan and
        // waits for the next card, so the wiring that turned the view
        // model's Confirmed event into Close(true) is gone (and so is the
        // event). Done closes it; the caller reads the view model's Added
        // list, which is filled whichever way the window is dismissed.
    }

    SimulatorImportViewModel? ViewModel => DataContext as SimulatorImportViewModel;

    async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Pick a PRO screenshot",
                AllowMultiple = false,
                FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
            });

            IStorageFile? file = files.FirstOrDefault();

            if (file == null || ViewModel == null)
                return;

            await using Stream stream = await file.OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);

            HideSelectionBox();

            // §165: browsing scans automatically when the card is found.
            await ViewModel.LoadAndAutoScanAsync(memory.ToArray());
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Card importer: the file picker failed.");
        }
    }

    // §198: what was Cancel. Every Add is already committed to storage by
    // the time this is pressed, so there is nothing left to cancel - this
    // just ends the sitting.
    void Done_Click(object? sender, RoutedEventArgs e) => Close(true);

    void ClearBox_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.SetSelection(null);
        HideSelectionBox();
    }

    void HideSelectionBox()
    {
        selectionBox.IsVisible = false;
    }

    // ---- the drag-a-box overlay ----

    void Image_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { HasImage: true })
            return;

        dragStart = e.GetPosition(imagePanel);
        dragging = true;

        selectionBox.IsVisible = false;
    }

    void Image_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!dragging)
            return;

        Point current = e.GetPosition(imagePanel);
        Rect box = new Rect(dragStart, current).Normalize();

        Canvas.SetLeft(selectionBox, box.X);
        Canvas.SetTop(selectionBox, box.Y);
        selectionBox.Width = box.Width;
        selectionBox.Height = box.Height;
        selectionBox.IsVisible = box.Width > 4 && box.Height > 4;
    }

    void Image_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!dragging)
            return;

        dragging = false;

        Point current = e.GetPosition(imagePanel);
        Rect box = new Rect(dragStart, current).Normalize();

        SKRectI? selection = ToBitmapRect(box);

        if (selection == null || selection.Value.Width < 40 || selection.Value.Height < 30)
        {
            // A click (or a sliver) clears the box back to auto-detect.
            ViewModel?.SetSelection(null);
            HideSelectionBox();
            return;
        }

        ViewModel?.SetSelection(selection);
    }

    /// <summary>Display coordinates -> bitmap pixels. The Image control
    /// stretches uniformly and centers, so the mapping is one scale factor
    /// plus a letterbox offset on each axis.</summary>
    SKRectI? ToBitmapRect(Rect displayRect)
    {
        if (ViewModel?.Screenshot == null)
            return null;

        PixelSize pixels = ViewModel.Screenshot.PixelSize;
        Rect panel = imagePanel.Bounds;

        if (pixels.Width <= 0 || pixels.Height <= 0 ||
            panel.Width <= 0 || panel.Height <= 0)
        {
            return null;
        }

        double scale = Math.Min(panel.Width / pixels.Width, panel.Height / pixels.Height);

        if (scale <= 0)
            return null;

        double offsetX = (panel.Width - pixels.Width * scale) / 2;
        double offsetY = (panel.Height - pixels.Height * scale) / 2;

        int x0 = (int)Math.Floor((displayRect.X - offsetX) / scale);
        int y0 = (int)Math.Floor((displayRect.Y - offsetY) / scale);
        int x1 = (int)Math.Ceiling((displayRect.Right - offsetX) / scale);
        int y1 = (int)Math.Ceiling((displayRect.Bottom - offsetY) / scale);

        x0 = Math.Clamp(x0, 0, pixels.Width - 1);
        y0 = Math.Clamp(y0, 0, pixels.Height - 1);
        x1 = Math.Clamp(x1, 1, pixels.Width);
        y1 = Math.Clamp(y1, 1, pixels.Height);

        return x1 <= x0 || y1 <= y0 ? null : new SKRectI(x0, y0, x1, y1);
    }
}
