using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>§397. One map's spawn page - see SpawnMapViewModel.</summary>
public partial class SpawnMapWindow : Window
{
    public SpawnMapWindow()
    {
        InitializeComponent();
    }

    public SpawnMapWindow(string mapName) : this()
    {
        var vm = new SpawnMapViewModel(mapName);
        DataContext = vm;
        Closed += (_, _) => vm.Dispose();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
