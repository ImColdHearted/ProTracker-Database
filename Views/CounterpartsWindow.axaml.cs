using Avalonia.Controls;
using Avalonia.Input;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class CounterpartsWindow : Window
{
    public CounterpartsWindow()
    {
        InitializeComponent();
    }

    /// <summary>Call right after construction, before Show()/ShowDialog(). Replaces
    /// the WinForms constructor parameter: new Counterparts(groupName).</summary>
    public void LoadGroup(string groupName)
    {
        var vm = new CounterpartsViewModel();
        vm.Load(groupName);
        DataContext = vm;
    }

    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: CounterpartCardItem card } && DataContext is CounterpartsViewModel vm)
        {
            vm.SelectCardCommand.Execute(card);
        }
    }
}
