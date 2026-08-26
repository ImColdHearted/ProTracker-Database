using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class CompactWindow : Window
{
    private readonly Window? _owner;

    public CompactWindow()
    {
        InitializeComponent();
    }

    public CompactWindow(Window owner) : this()
    {
        _owner = owner;
    }

    private void DragHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        // Hand dialog ownership back to MainWindow before it's shown again - see
        // MainWindowViewModel.ActiveWindow.
        if (_owner?.DataContext is MainWindowViewModel vm)
            vm.ActiveWindow = _owner;

        _owner?.Show();
        _owner?.Activate();
        Close();
    }
}