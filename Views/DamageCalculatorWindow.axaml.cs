using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class DamageCalculatorWindow : Window
{
    public DamageCalculatorWindow()
    {
        InitializeComponent();
        DataContext = new DamageCalculatorViewModel();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
