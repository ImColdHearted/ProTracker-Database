using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class WorldQuestCalculatorWindow : Window
{
    public WorldQuestCalculatorWindow()
    {
        InitializeComponent();
        DataContext = new WorldQuestCalculatorViewModel();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
