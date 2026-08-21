using Avalonia.Controls;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class ClientSelectorWindow : Window
{
    public ClientSelectorWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ClientSelectorViewModel vm)
            {
                vm.Confirmed += () => Close(true);
            }
        };
    }
}
