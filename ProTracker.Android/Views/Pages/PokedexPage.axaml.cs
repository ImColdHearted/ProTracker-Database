using Avalonia.Controls;
using Avalonia.Input;
using Foot_Tracker.Models;
using ProTracker.Companion.ViewModels;

namespace ProTracker.Companion.Views.Pages;

public partial class PokedexPage : UserControl
{
    private readonly PokedexPageViewModel vm = new();

    public PokedexPage()
    {
        InitializeComponent();
        DataContext = vm;
    }

    public static PokedexPage ForSpecies(string species)
    {
        var page = new PokedexPage();
        page.vm.Query = species;
        page.vm.ShowSpecies(species);
        return page;
    }

    public static PokedexPage ForMap(string mapName)
    {
        var page = new PokedexPage();
        page.vm.ShowMap(mapName);
        return page;
    }

    private void Species_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: PokedexEntry entry })
            vm.SelectCommand.Execute(entry);
    }
}
