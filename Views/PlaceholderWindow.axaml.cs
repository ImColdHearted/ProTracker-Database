using Avalonia.Controls;

namespace Foot_Tracker.Views;

public partial class PlaceholderWindow : Window
{
    public PlaceholderWindow()
    {
        InitializeComponent();
    }

    public PlaceholderWindow(string title) : this()
    {
        Title = title;
        this.FindControl<TextBlock>("TitleText")!.Text = title;
    }
}
