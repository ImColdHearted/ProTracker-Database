using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class ExcludeStatsWindow : Window
{
    public ExcludeStatsWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ExcludeStatsViewModel vm)
            {
                vm.SavedSuccessfully += () => Close(true);
            }
        };
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
