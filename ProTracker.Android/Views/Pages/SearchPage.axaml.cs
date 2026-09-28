using Avalonia.Controls;
using Avalonia.Input;
using Foot_Tracker.ViewModels;

namespace ProTracker.Companion.Views.Pages;

public partial class SearchPage : UserControl
{
    public SearchPage()
    {
        InitializeComponent();

        var vm = new SearchViewModel();
        DataContext = vm;

        // The desktop opens a window per kind (§280, §288). Here each kind
        // has a page. A Pokémon opens its Pokédex areas rather than the
        // encounter history: in the field, "where do I find it" is the
        // question, and the history is a desktop-sized table.
        vm.OpenResult = item =>
        {
            switch (item.Kind)
            {
                case SearchResultKind.Pokemon:
                    Nav.Push(PokedexPage.ForSpecies(item.Key), item.Title);
                    break;

                case SearchResultKind.Map:
                    Nav.Push(PokedexPage.ForMap(item.Key), item.Title);
                    break;

                case SearchResultKind.Player:
                    Nav.Push(new PvpDetailPage(item.Key), item.Title);
                    break;

                // §423: no Boss case - the desktop's Search stopped listing
                // bosses (they are found through the Map Explorer, which
                // the Maps tab now carries), and the kind left the enum.
            }
        };

        AttachedToVisualTree += (_, _) => QueryBox.Focus();
    }

    private void Result_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: SearchResultItem item } && DataContext is SearchViewModel vm)
            vm.OpenCommand.Execute(item);
    }
}
