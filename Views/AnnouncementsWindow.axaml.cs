using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Foot_Tracker.Views;

/// <summary>§239. The window itself does nothing but hold the view model.
/// The Open button and its browser handoff went with the forum reader: an
/// announcement is now the whole post rather than a summary of one, so there
/// is nothing left to go and read elsewhere.</summary>
public partial class AnnouncementsWindow : Window
{
    public AnnouncementsWindow()
    {
        InitializeComponent();
        DataContext = new Foot_Tracker.ViewModels.AnnouncementsViewModel();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
