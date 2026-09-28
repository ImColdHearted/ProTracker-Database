using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Foot_Tracker.Services;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// §366. The picture behind a shared appearance, shown on demand.
///
/// No view model: this window has one input, one output and no commands -
/// it is handed a gallery card, it fetches that card's image and draws it.
/// ReportDescriptionWindow is the same shape and the same size.
///
/// The fetch goes through CommunityThemeService.DownloadImageBytesAsync,
/// NOT DownloadBackgroundAsync. That distinction is the point: looking at a
/// picture must not write one into the user's appearance folder. See that
/// method's remarks.
/// </summary>
public partial class SharedImageWindow : Window
{
    private CommunityThemeItem? item;

    private bool closed;

    public SharedImageWindow()
    {
        InitializeComponent();

        Closed += (_, _) => closed = true;
    }

    public SharedImageWindow(CommunityThemeItem source) : this()
    {
        item = source;

        Title = source.Name;
        NameText.Text = source.Name;

        SizeText.Text = string.IsNullOrWhiteSpace(source.ImageSizeNote)
            ? "Shared by " + source.Creator
            : $"{source.ImageSizeNote} - shared by {source.Creator}";

        // §345's reasoning, unchanged: a constructor cannot await, and an
        // un-awaited task started from one can outlive the window that
        // started it. The load happens on Opened instead.
        Opened += async (_, _) => await LoadAsync();
    }

    private async System.Threading.Tasks.Task LoadAsync()
    {
        if (item is null)
            return;

        byte[]? bytes = await CommunityThemeService.DownloadImageBytesAsync(item.Source);

        // The window can be closed while the picture is still coming down.
        if (closed)
            return;

        if (bytes is null || bytes.Length == 0)
        {
            StatusText.Text =
                "That picture could not be downloaded. The appearance can still be applied - " +
                "its colours and font do not depend on it.";
            return;
        }

        try
        {
            using var stream = new MemoryStream(bytes);

            // An animated GIF shows its first frame here. The gallery is
            // judging a background, and the first frame is what a background
            // looks like before anything moves; animating it would mean a
            // second decoder in a window that exists to answer one question.
            Picture.Source = new Bitmap(stream);
            StatusText.IsVisible = false;
        }
        catch (Exception ex)
        {
            StatusText.Text = "That picture could not be displayed - " + ex.Message;
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
