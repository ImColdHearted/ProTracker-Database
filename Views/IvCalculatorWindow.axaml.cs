using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class IvCalculatorWindow : Window
{
    public IvCalculatorWindow()
    {
        InitializeComponent();
        DataContext = new IvCalculatorViewModel();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
