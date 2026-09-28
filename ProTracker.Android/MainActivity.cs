using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;

namespace ProTracker.Companion;

/// <summary>
/// §295/§296. The one Android activity. Avalonia draws the whole app into
/// it; the app itself is started by ProTrackerApplication (Avalonia 12
/// starts from the Android Application object, not the activity), so the
/// only Android-specific thing left here is the back button, which pops
/// the page stack instead of leaving the app.
/// </summary>
[Activity(
    Label = "Pro Tracker",
    Theme = "@style/ProTrackerTheme.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Avalonia 12 routes both the legacy OnBackPressed and the API 33+
        // predictive-back callback through this one event.
        BackRequested += OnBackRequested;
    }

    private static void OnBackRequested(object? sender, AndroidBackRequestedEventArgs e)
    {
        if (Nav.Back())
            e.Handled = true;
    }
}
