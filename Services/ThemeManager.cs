using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Foot_Tracker.Models;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// Ported from WinForms. The original walked the Form's control tree and set
    /// ForeColor/BackColor on every control directly. Avalonia is resource/style driven,
    /// so instead we push the current theme's colors into the Application's resource
    /// dictionary as brushes. Every view then binds with:
    ///   Background="{DynamicResource ThemeBackgroundBrush}"
    ///   Foreground="{DynamicResource ThemeTextBrush}"
    ///   BorderBrush="{DynamicResource ThemeBorderBrush}"
    /// and updates automatically whenever Reload() is called - no control-tree walk needed.
    /// </summary>
    public static class ThemeManager
    {
        public static AppearanceSettings Current { get; private set; } =
            AppearanceSettingsRepository.Load();

        public const string BackgroundBrushKey = "ThemeBackgroundBrush";
        public const string TextBrushKey = "ThemeTextBrush";
        public const string BorderBrushKey = "ThemeBorderBrush";

        public static void Reload()
        {
            Current = AppearanceSettingsRepository.Load();
            Apply();
        }

        /// <summary>Call once at startup (e.g. from App.axaml.cs OnFrameworkInitializationCompleted).</summary>
        public static void Apply()
        {
            var app = Application.Current;
            if (app is null)
                return;

            app.Resources[BackgroundBrushKey] =
                BuildBackgroundBrush();

            app.Resources[TextBrushKey] =
                new SolidColorBrush(Current.TextColor);

            app.Resources[BorderBrushKey] =
                new SolidColorBrush(Current.BorderColor);
        }

        public static string GetBackgroundPath(string backgroundId)
        {
            if (string.IsNullOrWhiteSpace(backgroundId))
                return string.Empty;

            return Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Assets",
                "Custom",
                $"{backgroundId}.png");
        }

        /// <summary>
        /// Builds the actual background brush: the preset/custom gradient image when
        /// one exists on disk, otherwise a flat SolidColorBrush fallback (either the
        /// preset's base color or the user's custom picked color). Previously this
        /// always returned a SolidColorBrush and loaded the image into an unused
        /// resource - presets never actually showed their gradient art.
        /// </summary>
        private static IBrush BuildBackgroundBrush()
        {
            string backgroundPath = GetCurrentBackgroundPath();

            if (!string.IsNullOrWhiteSpace(backgroundPath) && File.Exists(backgroundPath))
            {
                return new ImageBrush(new Bitmap(backgroundPath))
                {
                    Stretch = Stretch.UniformToFill
                };
            }

            return new SolidColorBrush(Current.BackgroundColor);
        }

        private static string GetCurrentBackgroundPath()
        {
            if (Current.UseCustomBackground)
            {
                // "Custom" covers both a user-picked image and a plain custom color -
                // only the former has a file on disk to load.
                return !string.IsNullOrWhiteSpace(Current.CustomBackgroundPath) &&
                       File.Exists(Current.CustomBackgroundPath)
                    ? Current.CustomBackgroundPath
                    : string.Empty;
            }

            return GetBackgroundPath(Current.BackgroundId);
        }
    }
}