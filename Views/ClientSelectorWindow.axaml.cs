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

                // §105: taking a profile from another running tracker window
                // asks first, through the app's existing confirm dialog -
                // same Request*/Func hook shape MainWindowViewModel uses, and
                // for the same reason (showing a child window needs a Window
                // reference a ViewModel shouldn't hold). §110 gave the same
                // hook a second job: confirming a move of a running client
                // onto a slot that has none.
                vm.ConfirmAsync = message => ConfirmDialogWindow.ShowAsync(this, message);
            }
        };
    }
}
