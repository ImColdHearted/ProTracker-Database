using Avalonia.Controls;
using Avalonia.Interactivity;
using ProTracker.Companion.Views.Pages;

namespace ProTracker.Companion.Views;

public partial class MainView : UserControl
{
    private string activeTab = "bosses";

    /// <summary>§423. One Map Explorer for the life of the app: its view
    /// model listens to the spawn and pin services, so a page per visit
    /// would be a listener per visit - and the picture keeps its place and
    /// its card between tabs, which is what a person expects of a map.</summary>
    private static MapsPage? mapsPage;

    public MainView()
    {
        InitializeComponent();

        Nav.Changed += Render;

        // A failed bootstrap is a page, not a crash: it says what went wrong
        // where the user can read it and copy it.
        if (Bootstrap.StartupError is not null)
        {
            activeTab = "data";
            Nav.Root(new DataPage(), "Data");
        }
        else
        {
            Nav.Root(new BossesPage(), "Boss Database");
        }
    }

    private void Render()
    {
        Host.Content = Nav.Current;
        TitleText.Text = Nav.Title;
        BackButton.IsVisible = Nav.CanGoBack;

        foreach (Button tab in new[] { TabBosses, TabPokedex, TabSearch, TabMaps, TabBattle, TabData })
            tab.Classes.Set("active", (string?)tab.Tag == activeTab);
    }

    private void Back_Click(object? sender, RoutedEventArgs e) => Nav.Back();

    private void Tab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
            return;

        activeTab = tag;

        switch (tag)
        {
            case "bosses": Nav.Root(new BossesPage(), "Boss Database"); break;
            case "pokedex": Nav.Root(new PokedexPage(), "Pokédex Areas"); break;
            case "search": Nav.Root(new SearchPage(), "Search"); break;
            case "maps": Nav.Root(mapsPage ??= new MapsPage(), "Map Explorer"); break;
            case "battle": Nav.Root(new SimulatorPage(), "Simulator"); break;
            case "data": Nav.Root(new DataPage(), "Data"); break;
        }
    }
}
