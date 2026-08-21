using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Foot_Tracker.Services;
using Foot_Tracker.Views;

namespace Foot_Tracker;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Load persisted data, same as the WinForms constructor did.
        PokemonSpriteService.Load();
        CounterpartSpriteService.Load();
        BossCooldownService.Load();

        // Push the saved appearance settings into the app's resource dictionary.
        ThemeManager.Apply();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new ViewModels.MainWindowViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
