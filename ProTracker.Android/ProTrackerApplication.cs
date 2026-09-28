using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace ProTracker.Companion;

/// <summary>
/// §296. Avalonia 12 starts the Avalonia application from the Android
/// Application object, not from the activity: the process creates this
/// once, before any activity, and the app builder is configured here. That
/// is also the right place for the bootstrap - the bundle unpack and the
/// base-directory redirect happen before Avalonia exists, exactly once per
/// process, however many times the activity is recreated by a rotation.
/// </summary>
[global::Android.App.Application]
public class ProTrackerApplication : AvaloniaAndroidApplication<App>
{
    protected ProTrackerApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CreateAppBuilder()
    {
        // Android's own Application.OnCreate has run by now, so
        // Application.Context (the files directory, the asset manager) is
        // there for the bootstrap to use.
        Bootstrap.Run();

        return base.CreateAppBuilder();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
