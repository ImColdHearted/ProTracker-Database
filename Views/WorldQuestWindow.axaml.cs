using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>§233. The live World Quest tracker - the window itself does
/// nothing but hold the view model and start and stop it, because everything
/// it does (fetch the quest, watch the client, count catches) has to keep
/// running while the player is looking at the game rather than at this.</summary>
public partial class WorldQuestWindow : Window
{
    private readonly WorldQuestViewModel model = new();

    public WorldQuestWindow()
    {
        InitializeComponent();
        DataContext = model;
    }

    /// <summary>The fetch and the watch start once the window is actually on
    /// screen, not in the constructor - so the window appears immediately and
    /// says what it is doing while the events server is asked.</summary>
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        await model.StartAsync();
    }

    /// <summary>Stops both timers. Without this the watch would keep grabbing
    /// the PRO client every second and a half for the rest of the session.</summary>
    protected override void OnClosed(EventArgs e)
    {
        model.Dispose();
        base.OnClosed(e);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
