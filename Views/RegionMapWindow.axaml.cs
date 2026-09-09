using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class RegionMapWindow : Window
{
    public RegionMapWindow()
    {
        InitializeComponent();
    }

    /// <summary>Call right after construction, before Show()/ShowDialog().</summary>
    public void LoadRegion(string regionFolder, string title)
    {
        Title = title;
        var vm = new RegionMapViewModel();
        vm.Load(regionFolder);
        DataContext = vm;
    }

    private void RouteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RouteButtonItem route } && DataContext is RegionMapViewModel vm)
        {
            vm.SelectRouteCommand.Execute(route);
        }
    }
}
