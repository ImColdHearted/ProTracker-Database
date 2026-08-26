using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class CustomGradientWindow : Window
{
    public CustomGradientWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is CustomGradientViewModel vm)
            {
                vm.Applied += () => Close(true);
            }
        };
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
