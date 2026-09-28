using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Foot_Tracker.Models;
using Foot_Tracker.Services;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>§397. One region's Spawns page - see SpawnRegionViewModel. A
/// map's row opens SpawnMapWindow through the registry, one per map, so a
/// second click on the same link brings the open window forward rather
/// than opening a twin.</summary>
public partial class SpawnRegionWindow : Window
{
    public SpawnRegionWindow()
    {
        InitializeComponent();
    }

    public SpawnRegionWindow(string region) : this()
    {
        var vm = new SpawnRegionViewModel(region);
        DataContext = vm;

        // The copy in hand is showing already; the fetch fills in behind it.
        Opened += async (_, _) => await vm.RefreshAsync();
        Closed += (_, _) => vm.Dispose();
    }

    private void MapRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: SpawnMapLink link })
            return;

        string map = link.Map;

        WindowRegistry.ShowOrActivate(
            this,
            () => new SpawnMapWindow(map),
            contentKey: SpawnMap.KeyFor(map));
    }

    private async void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SpawnRegionViewModel vm)
            await vm.RefreshAsync();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
