using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

public sealed record BackgroundChoiceOption(string Id, Bitmap? Thumbnail);

/// <summary>
/// Ported from AppearanceForm.cs. WinForms' ColorDialog / OpenFileDialog become
/// Avalonia's built-in ColorPicker control and IStorageProvider file picker
/// (wired up from AppearanceWindow.axaml.cs, since file/color pickers need a
/// TopLevel reference that a ViewModel shouldn't hold directly).
///
/// Background has three modes, all sharing AppearanceSettings.UseCustomBackground:
///   - Preset gradient (UseCustomBackground = false, BackgroundId = preset name)
///   - Custom image (UseCustomBackground = true, CustomBackgroundPath set)
///   - Custom solid color (UseCustomBackground = true, CustomBackgroundPath empty,
///     CustomBackgroundColorArgb set from the BackgroundCustomColor slider)
/// </summary>
public sealed partial class AppearanceViewModel : ViewModelBase
{
    private static readonly string[] PresetIds =
        ["Midnight", "Blood", "Slate", "Pride", "Pink", "Violet"];

    private readonly AppearanceSettings _workingSettings = AppearanceSettingsRepository.Load();

    public List<BackgroundChoiceOption> BackgroundChoices { get; }

    [ObservableProperty] private string? selectedBackgroundId;
    [ObservableProperty] private Bitmap? previewBackgroundImage;
    [ObservableProperty] private IBrush previewBackgroundBrush = Brushes.Black;
    [ObservableProperty] private Color textColor;
    [ObservableProperty] private Color borderColor;
    [ObservableProperty] private Color backgroundCustomColor;
    [ObservableProperty] private string previewTitleText = "Pro Tracker & Database";
    [ObservableProperty] private string? saveError;
    [ObservableProperty] private bool hasSaveError;

    partial void OnSaveErrorChanged(string? value) => HasSaveError = !string.IsNullOrEmpty(value);

    private string? _pendingCustomImagePath;

    /// <summary>Raised when Save completes successfully - the View closes itself.</summary>
    public event Action? SavedSuccessfully;

    public AppearanceViewModel()
    {
        BackgroundChoices = PresetIds
            .Select(id => new BackgroundChoiceOption(id, LoadThumbnail(id)))
            .ToList();

        TextColor = _workingSettings.TextColor;
        BorderColor = _workingSettings.BorderColor;
        BackgroundCustomColor = AppearanceSettings.FromArgbInt(_workingSettings.CustomBackgroundColorArgb);
        SelectedBackgroundId = _workingSettings.UseCustomBackground ? null : _workingSettings.BackgroundId;

        RefreshPreview();
    }

    private static Bitmap? LoadThumbnail(string backgroundId)
    {
        string path = ThemeManager.GetBackgroundPath(backgroundId);
        return File.Exists(path) ? new Bitmap(path) : null;
    }

    [RelayCommand]
    private void ChooseBackground(string backgroundId)
    {
        if (string.IsNullOrWhiteSpace(backgroundId))
            return;

        SelectedBackgroundId = backgroundId;
        _workingSettings.BackgroundId = backgroundId;
        _workingSettings.UseCustomBackground = false;
        _workingSettings.CustomBackgroundPath = string.Empty;
        _pendingCustomImagePath = null;

        RefreshPreview();
    }

    /// <summary>Called by AppearanceWindow.axaml.cs after IStorageProvider returns a file.</summary>
    public void SetCustomBackground(string filePath)
    {
        _pendingCustomImagePath = filePath;
        _workingSettings.UseCustomBackground = true;
        _workingSettings.CustomBackgroundPath = string.Empty;
        SelectedBackgroundId = null;

        RefreshPreview();
    }

    /// <summary>Switches to a plain custom-color background, using whatever the
    /// BackgroundCustomColor slider is currently set to.</summary>
    [RelayCommand]
    private void UseCustomColorBackground()
    {
        SelectedBackgroundId = null;
        _workingSettings.UseCustomBackground = true;
        _workingSettings.CustomBackgroundPath = string.Empty;
        _pendingCustomImagePath = null;
        _workingSettings.CustomBackgroundColorArgb = AppearanceSettings.ToArgbInt(BackgroundCustomColor);

        RefreshPreview();
    }

    [RelayCommand]
    private void ClearBackground()
    {
        _workingSettings.UseCustomBackground = false;
        _workingSettings.CustomBackgroundPath = string.Empty;
        _workingSettings.BackgroundId = "Pride";
        _pendingCustomImagePath = null;
        SelectedBackgroundId = "Pride";

        RefreshPreview();
    }

    partial void OnTextColorChanged(Color value)
    {
        _workingSettings.TextColorArgb = AppearanceSettings.ToArgbInt(value);
    }

    partial void OnBorderColorChanged(Color value)
    {
        _workingSettings.BorderColorArgb = AppearanceSettings.ToArgbInt(value);
    }

    partial void OnBackgroundCustomColorChanged(Color value)
    {
        _workingSettings.CustomBackgroundColorArgb = AppearanceSettings.ToArgbInt(value);

        // Live-update the preview while dragging the slider, but only once the
        // user has actually switched into "custom color" mode - otherwise moving
        // the slider before clicking "Use Custom Color" would prematurely
        // override whatever preset/image is currently selected.
        bool isActiveCustomColorMode =
            _workingSettings.UseCustomBackground &&
            string.IsNullOrWhiteSpace(_pendingCustomImagePath) &&
            string.IsNullOrWhiteSpace(_workingSettings.CustomBackgroundPath);

        if (isActiveCustomColorMode)
        {
            RefreshPreview();
        }
    }

    private void RefreshPreview()
    {
        string? imagePath = _workingSettings.UseCustomBackground
            ? _pendingCustomImagePath
            : ThemeManager.GetBackgroundPath(_workingSettings.BackgroundId);

        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            PreviewBackgroundImage = new Bitmap(imagePath);
            PreviewBackgroundBrush = Brushes.Transparent;
            return;
        }

        // No image (preset with no gradient art, or custom-color mode) - fall back
        // to a flat brush so the preview never just goes blank.
        PreviewBackgroundImage = null;
        PreviewBackgroundBrush = new SolidColorBrush(_workingSettings.BackgroundColor);
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            if (_workingSettings.UseCustomBackground &&
                !string.IsNullOrWhiteSpace(_pendingCustomImagePath))
            {
                _workingSettings.CustomBackgroundPath =
                    AppearanceSettingsRepository.SaveCustomBackground(_pendingCustomImagePath);
            }

            AppearanceSettingsRepository.Save(_workingSettings);
            ThemeManager.Reload();

            SavedSuccessfully?.Invoke();
        }
        catch (Exception ex)
        {
            SaveError = $"The appearance settings could not be saved.\n\n{ex.Message}";
        }
    }
}