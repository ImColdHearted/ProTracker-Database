using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>§280. One map's encounter table - see MapDetailViewModel.</summary>
public partial class MapDetailWindow : Window
{
    public MapDetailWindow()
    {
        InitializeComponent();
    }

    public MapDetailWindow(string mapName) : this()
    {
        DataContext = new MapDetailViewModel(mapName);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
