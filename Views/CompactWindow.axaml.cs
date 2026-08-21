using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

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
        _owner?.Show();
        _owner?.Activate();
        Close();
    }
}
