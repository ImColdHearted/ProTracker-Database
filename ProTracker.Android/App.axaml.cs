using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ProTracker.Companion.Views;

namespace ProTracker.Companion;

// Spelt out: the Android SDK imports Android.App everywhere, and its
// Application would otherwise tie with Avalonia's (§296).
public partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Bootstrap.LoadServices();

        // A phone is a single view, not a desktop of windows; MainView is
        // the whole screen and Nav decides what fills it.
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
            single.MainView = new MainView();

        base.OnFrameworkInitializationCompleted();
    }
}
