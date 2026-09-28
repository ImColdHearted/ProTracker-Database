using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Ported from AppearanceForm.cs. WinForms' ColorDialog / OpenFileDialog become
/// Avalonia's built-in ColorPicker control and IStorageProvider file picker
/// (wired up from AppearanceWindow.axaml.cs, since file/color pickers need a
/// TopLevel reference that a ViewModel shouldn't hold directly).
///
/// Background has four modes. The first three share AppearanceSettings.UseCustomBackground:
///   - The built-in look (UseCustomBackground = false, BackgroundId = Slate).
///     §104 removed the six-preset picker; Slate is the only built-in left, so
///     this mode is now reached through Reset rather than a chooser.
///   - Custom image (UseCustomBackground = true, CustomBackgroundPath set)
///   - Custom solid color (UseCustomBackground = true, CustomBackgroundPath empty,
///     CustomBackgroundColorArgb set from the BackgroundCustomColor slider)
/// A fourth mode, custom gradient (see SetCustomGradient), is gated by its own
/// UseCustomGradient flag instead since it needs 2-4 colors and a direction
/// rather than the single color custom-color mode stores - only one of
/// UseCustomBackground/UseCustomGradient is ever true at a time, enforced by
/// every command below clearing the other mode's flags/paths.
///
/// §381. The same view model now drives the visual editor: the settings it
/// edits, the way they are written through to _workingSettings, and Apply,
/// Save, Load, Reset and Revert are all as they were. What is added is the
/// editor's STATE - which element of the canvas is selected, what the
/// inspector shows, the undo history, the asset library - and every piece of
/// it resolves to the existing properties above. There is no second
/// appearance model.
/// </summary>
public sealed partial class AppearanceViewModel : ViewModelBase
{
    // A curated, cross-platform-safe list rather than enumerating installed system
    // fonts - Avalonia falls back gracefully (not an exception) if a named font
    // isn't present on a given machine, so listing a few common ones is safe even
    // if they don't all resolve identically on every OS. "Inter" is guaranteed
    // present everywhere since it ships via the Avalonia.Fonts.Inter package.
    // §385: the user's own font files follow these and the bundled ones, and
    // a shared theme may name one - a receiver without it sees Inter.
    private static readonly string[] SystemFontFamilyNames =
        ["Default", "Inter", "Arial", "Segoe UI", "Consolas", "Comic Sans MS", "Times New Roman", "Courier New"];

    private readonly AppearanceSettings _workingSettings = AppearanceSettingsRepository.Load();

    // Read by AppearanceWindow.axaml.cs when opening the Create Gradient
    // dialog, so reopening it picks up whatever gradient (or the model's
    // defaults) is already stored instead of always resetting to scratch.
    public IReadOnlyList<Color> GradientColorsSeed => _workingSettings.CustomGradientColors;
    public string GradientDirectionSeed => _workingSettings.CustomGradientDirection;

    // Custom bundled fonts (Assets/Fonts/, registered in ThemeManager.CustomFontCatalog)
    // show up here automatically - no changes needed in this file when adding one.
    // §385: an ObservableCollection, because a font added in the editor joins
    // the list at once; RefreshFontOptions rebuilds it.
    public ObservableCollection<string> FontFamilyOptions { get; } =
        new(SystemFontFamilyNames.Concat(ThemeManager.CustomFontCatalog.Keys).Concat(UserFontService.FamilyNames));

    public IReadOnlyList<string> FontSizeOptions { get; } =
        ThemeManager.FontSizeCatalog.Keys.ToList();

    // §394. The menu bar's font pickers offer one more entry, first: the
    // empty name the settings keep for "follows Statistics", which a
    // ComboBox has to show as something. The family list mirrors
    // FontFamilyOptions (RefreshFontOptions keeps it so); the size list is
    // the catalog behind the same entry.
    public const string SameAsStatistics = "Same as Statistics";

    public ObservableCollection<string> MenuFontFamilyOptions { get; } =
        new(new[] { SameAsStatistics }.Concat(SystemFontFamilyNames).Concat(ThemeManager.CustomFontCatalog.Keys).Concat(UserFontService.FamilyNames));

    public IReadOnlyList<string> MenuFontSizeOptions { get; } =
        new[] { SameAsStatistics }.Concat(ThemeManager.FontSizeCatalog.Keys).ToList();

    [ObservableProperty] private string menuFontFamily = SameAsStatistics;
    [ObservableProperty] private string menuFontSize = SameAsStatistics;

    partial void OnMenuFontFamilyChanged(string value)
    {
        _workingSettings.MenuFontFamilyName = string.IsNullOrWhiteSpace(value) || value == SameAsStatistics ? string.Empty : value;
        RefreshPreviewFonts();
        Track(nameof(MenuFontFamily));
    }

    partial void OnMenuFontSizeChanged(string value)
    {
        _workingSettings.MenuFontSizeName = string.IsNullOrWhiteSpace(value) || value == SameAsStatistics ? string.Empty : value;
        RefreshPreviewFonts();
        Track(nameof(MenuFontSize));
    }

    /// <summary>§394. What the pickers show for a stored menu font: the
    /// name, or the "follows Statistics" entry for the empty one.</summary>
    private static string MenuFontFamilyDisplay(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? SameAsStatistics : stored;

    private static string MenuFontSizeDisplay(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? SameAsStatistics : ThemeManager.NormalizeFontSizeName(stored);

    [ObservableProperty] private Bitmap? previewBackgroundImage;
    [ObservableProperty] private IBrush previewBackgroundBrush = Brushes.Black;
    [ObservableProperty] private Color backgroundCustomColor;

    // §349. Four sections, five controls each. The single TextColor,
    // BorderColor, SelectedFontFamily and SelectedFontSize that used to live
    // here are gone from this window - Statistics holds what they meant, and
    // everything that is not one of the other three regions follows it.
    [ObservableProperty] private Color spriteBoxTextColor;
    [ObservableProperty] private Color spriteBoxBorderColor;
    [ObservableProperty] private string spriteBoxFontFamily = "Default";
    [ObservableProperty] private string spriteBoxFontSize = ThemeManager.DefaultFontSizeName;

    [ObservableProperty] private Color encountersTextColor;
    [ObservableProperty] private Color encountersBorderColor;
    [ObservableProperty] private string encountersFontFamily = "Default";
    [ObservableProperty] private string encountersFontSize = ThemeManager.DefaultFontSizeName;

    [ObservableProperty] private Color statsTextColor;
    [ObservableProperty] private Color statsBorderColor;
    [ObservableProperty] private string statsFontFamily = "Default";
    [ObservableProperty] private string statsFontSize = ThemeManager.DefaultFontSizeName;

    [ObservableProperty] private Color buttonTextColor;
    [ObservableProperty] private Color buttonBorderColor;
    [ObservableProperty] private string buttonFontFamily = "Default";
    [ObservableProperty] private string buttonFontSize = ThemeManager.DefaultFontSizeName;

    // §380. See AppearanceSettings.BoldHeadings.
    [ObservableProperty] private bool boldHeadings = true;

    // §387. The sprite row panel's two colours.
    [ObservableProperty] private Color spriteRowBackgroundColor;
    [ObservableProperty] private Color spriteRowBorderColor;

    partial void OnSpriteRowBackgroundColorChanged(Color value)
    {
        _workingSettings.SpriteRowBackgroundColorArgb = AppearanceSettings.ToArgbInt(value);
        RefreshPanelBrushes();
        RefreshBorderPreview();
        Track(nameof(SpriteRowBackgroundColor));
    }

    partial void OnSpriteRowBorderColorChanged(Color value)
    {
        _workingSettings.SpriteRowBorderColorArgb = AppearanceSettings.ToArgbInt(value);
        RefreshBorderPreview();
        Track(nameof(SpriteRowBorderColor));
    }

    // §393. The menu bar's two colours. Transparent is "automatic" - see
    // ThemeManager.EffectiveMenuTextColor - which is what the canvas shows
    // through MenuPreviewTextBrush, resolved against the working background.
    [ObservableProperty] private Color menuTextColor;
    [ObservableProperty] private Color menuHighlightColor;

    partial void OnMenuTextColorChanged(Color value)
    {
        _workingSettings.MenuTextColorArgb = AppearanceSettings.ToArgbInt(value);
        RefreshMenuPreview();
        Track(nameof(MenuTextColor));
    }

    partial void OnMenuHighlightColorChanged(Color value)
    {
        _workingSettings.MenuHighlightColorArgb = AppearanceSettings.ToArgbInt(value);
        RefreshMenuPreview();
        Track(nameof(MenuHighlightColor));
    }

    [RelayCommand]
    private void UseAutomaticMenuText()
    {
        MenuTextColor = Colors.Transparent;
        StatusText = "The menu text follows the background again - black over a light one, white over a dark one.";
    }

    [RelayCommand]
    private void UseAutomaticMenuHighlight()
    {
        MenuHighlightColor = Colors.Transparent;
        StatusText = "The menu highlight is the faint one again.";
    }

    // What the canvas paints the strip with, and the notes under the two
    // pickers saying what "automatic" came to.
    [ObservableProperty] private IBrush menuPreviewTextBrush = Brushes.White;
    [ObservableProperty] private IBrush menuPreviewHighlightBrush = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
    [ObservableProperty] private string menuTextNote = string.Empty;
    [ObservableProperty] private string menuHighlightNote = string.Empty;
    [ObservableProperty] private bool isMenuTextAutomatic = true;
    [ObservableProperty] private bool isMenuHighlightAutomatic = true;

    private void RefreshMenuPreview()
    {
        bool light;

        try
        {
            light = ThemeManager.BackgroundIsLightBehindMenu(_workingSettings, CurrentPicturePath());
        }
        catch
        {
            light = false;
        }

        Color text = ThemeManager.EffectiveMenuTextColor(_workingSettings, light);
        Color highlight = ThemeManager.EffectiveMenuHighlightColor(_workingSettings, light);

        MenuPreviewTextBrush = new SolidColorBrush(text);
        MenuPreviewHighlightBrush = new SolidColorBrush(highlight);
        IsMenuTextAutomatic = _workingSettings.MenuTextColor.A == 0;
        IsMenuHighlightAutomatic = _workingSettings.MenuHighlightColor.A == 0;

        string behind = light ? "light" : "dark";
        string automatic = light ? "black" : "white";
        MenuTextNote = IsMenuTextAutomatic
            ? $"Automatic  ·  {automatic}, because the strip behind the menu reads as {behind}. Pick a colour to set your own; a transparent colour is automatic."
            : "Your colour, whatever the background. Automatic returns it to black or white by the background.";
        MenuHighlightNote = IsMenuHighlightAutomatic
            ? "Automatic  ·  a faint highlight behind the item the pointer is on, or that is open."
            : "Your colour behind the item the pointer is on, or that is open.";
    }

    [ObservableProperty] private FontFamily previewFontFamily = ThemeManager.BuildFontFamily("Default");
    // §133: named, not written out. These two were "Default" - a name the
    // catalog no longer has - and the second called BuildFontSize with it
    // from a FIELD INITIALISER, so the throw would have happened while
    // constructing the view model, taking the whole Appearance window with
    // it rather than showing a wrong size.
    [ObservableProperty] private double previewFontSize =
        ThemeManager.BuildFontSize(ThemeManager.DefaultFontSizeName);
    [ObservableProperty] private Color spriteBoxBackgroundColor;
    [ObservableProperty] private Color buttonColor;
    [ObservableProperty] private Color statsBackgroundColor;
    [ObservableProperty] private Color encountersBackgroundColor;
    [ObservableProperty] private Color headerBackgroundColor;
    [ObservableProperty] private string previewTitleText = "Pro Tracker & Database";
    [ObservableProperty] private string? saveError;
    [ObservableProperty] private bool hasSaveError;

    // §140. Which client's look this window edits, said plainly at the foot
    // of the window - above all when no client is detected yet, because the
    // edits then go to Client 1's file (see Save below for why that is now
    // allowed).
    [ObservableProperty] private string clientNote = string.Empty;

    partial void OnSaveErrorChanged(string? value) => HasSaveError = !string.IsNullOrEmpty(value);

    partial void OnSpriteBoxTextColorChanged(Color value)
    {
        _workingSettings.SpriteBoxTextColorArgb = AppearanceSettings.ToArgbInt(value);
        Track(nameof(SpriteBoxTextColor));
    }

    partial void OnSpriteBoxBorderColorChanged(Color value)
    {
        _workingSettings.SpriteBoxBorderColorArgb = AppearanceSettings.ToArgbInt(value);
        Track(nameof(SpriteBoxBorderColor));
    }

    // §388. The sprite font is the sprite font and nothing else. §351 made
    // this setter write the encounter table's font too, because the old
    // window had one Font control for the pair; the editor gives the table
    // its own TYPOGRAPHY section, so the pair is apart again - the settings,
    // the theme file, the Worker and ThemeManager always kept them separate.
    partial void OnSpriteBoxFontFamilyChanged(string value)
    {
        _workingSettings.SpriteBoxFontFamilyName = value;
        RefreshPreviewFonts();
        Track(nameof(SpriteBoxFontFamily));
    }

    partial void OnSpriteBoxFontSizeChanged(string value)
    {
        _workingSettings.SpriteBoxFontSizeName = value;
        RefreshPreviewFonts();
        Track(nameof(SpriteBoxFontSize));
    }

    partial void OnEncountersTextColorChanged(Color value)
    {
        _workingSettings.EncountersTextColorArgb = AppearanceSettings.ToArgbInt(value);
        Track(nameof(EncountersTextColor));
    }

    partial void OnEncountersBorderColorChanged(Color value)
    {
        _workingSettings.EncountersBorderColorArgb = AppearanceSettings.ToArgbInt(value);
        Track(nameof(EncountersBorderColor));
    }

    partial void OnEncountersFontFamilyChanged(string value)
    {
        _workingSettings.EncountersFontFamilyName = value;
        RefreshPreviewFonts();
        Track(nameof(EncountersFontFamily));
    }

    partial void OnEncountersFontSizeChanged(string value)
    {
        _workingSettings.EncountersFontSizeName = value;
        RefreshPreviewFonts();
        Track(nameof(EncountersFontSize));
    }

    partial void OnStatsTextColorChanged(Color value)
    {
        _workingSettings.StatsTextColorArgb = AppearanceSettings.ToArgbInt(value);
        Track(nameof(StatsTextColor));
    }

    partial void OnStatsBorderColorChanged(Color value)
    {
        _workingSettings.StatsBorderColorArgb = AppearanceSettings.ToArgbInt(value);
        Track(nameof(StatsBorderColor));
    }

    partial void OnStatsFontFamilyChanged(string value)
    {
        _workingSettings.StatsFontFamilyName = value;
        RefreshPreviewFonts();
        Track(nameof(StatsFontFamily));
    }

    partial void OnStatsFontSizeChanged(string value)
    {
        _workingSettings.StatsFontSizeName = value;
        RefreshPreviewFonts();
        Track(nameof(StatsFontSize));
    }

    partial void OnButtonTextColorChanged(Color value)
    {
        _workingSettings.ButtonTextColorArgb = AppearanceSettings.ToArgbInt(value);
        Track(nameof(ButtonTextColor));
    }

    partial void OnButtonBorderColorChanged(Color value)
    {
        _workingSettings.ButtonBorderColorArgb = AppearanceSettings.ToArgbInt(value);
        Track(nameof(ButtonBorderColor));
    }

    partial void OnButtonFontFamilyChanged(string value)
    {
        _workingSettings.ButtonFontFamilyName = value;
        RefreshPreviewFonts();
        Track(nameof(ButtonFontFamily));
    }

    partial void OnButtonFontSizeChanged(string value)
    {
        _workingSettings.ButtonFontSizeName = value;
        RefreshPreviewFonts();
        Track(nameof(ButtonFontSize));
    }

    partial void OnBoldHeadingsChanged(bool value)
    {
        _workingSettings.BoldHeadings = value;
        RefreshPreviewFonts();
        Track(nameof(BoldHeadings));
    }

    partial void OnSpriteBoxBackgroundColorChanged(Color value)
    {
        _workingSettings.SpriteBoxBackgroundColorArgb = AppearanceSettings.ToArgbInt(value);
        RefreshPanelBrushes();
        Track(nameof(SpriteBoxBackgroundColor));
    }

    // §104: the preview mocks the main window's own layout, so Button Color,
    // Stats Background and Encounters Background all demonstrate themselves
    // there - these handlers only need to write the value through.
    partial void OnButtonColorChanged(Color value)
    {
        _workingSettings.ButtonColorArgb = AppearanceSettings.ToArgbInt(value);
        Track(nameof(ButtonColor));
    }

    // §209. Transparent means "decide it for me" rather than "no header" -
    // see AppearanceSettings.HeaderBackgroundColorArgb.
    partial void OnHeaderBackgroundColorChanged(Color value)
    {
        _workingSettings.HeaderBackgroundColorArgb = AppearanceSettings.ToArgbInt(value);
        Track(nameof(HeaderBackgroundColor));
    }

    partial void OnStatsBackgroundColorChanged(Color value)
    {
        _workingSettings.StatsBackgroundColorArgb = AppearanceSettings.ToArgbInt(value);
        RefreshPanelBrushes();
        Track(nameof(StatsBackgroundColor));
    }

    partial void OnEncountersBackgroundColorChanged(Color value)
    {
        _workingSettings.EncountersBackgroundColorArgb = AppearanceSettings.ToArgbInt(value);
        RefreshPanelBrushes();
        Track(nameof(EncountersBackgroundColor));
    }

    private string? _pendingCustomImagePath;

    public AppearanceViewModel()
    {
        // §381. Nothing done while the pickers are being filled in is an
        // edit, so the history sleeps until the constructor is done.
        _suspendHistory = true;

        SpriteBoxTextColor = _workingSettings.SpriteBoxTextColor;
        SpriteBoxBorderColor = _workingSettings.SpriteBoxBorderColor;
        SpriteBoxFontFamily = _workingSettings.SpriteBoxFontFamilyName;
        SpriteBoxFontSize = ThemeManager.NormalizeFontSizeName(_workingSettings.SpriteBoxFontSizeName);
        EncountersTextColor = _workingSettings.EncountersTextColor;
        EncountersBorderColor = _workingSettings.EncountersBorderColor;
        EncountersFontFamily = _workingSettings.EncountersFontFamilyName;
        EncountersFontSize = ThemeManager.NormalizeFontSizeName(_workingSettings.EncountersFontSizeName);
        StatsTextColor = _workingSettings.StatsTextColor;
        StatsBorderColor = _workingSettings.StatsBorderColor;
        StatsFontFamily = _workingSettings.StatsFontFamilyName;
        StatsFontSize = ThemeManager.NormalizeFontSizeName(_workingSettings.StatsFontSizeName);
        ButtonTextColor = _workingSettings.ButtonTextColor;
        ButtonBorderColor = _workingSettings.ButtonBorderColor;
        ButtonFontFamily = _workingSettings.ButtonFontFamilyName;
        ButtonFontSize = ThemeManager.NormalizeFontSizeName(_workingSettings.ButtonFontSizeName);
        BoldHeadings = _workingSettings.BoldHeadings;
        SpriteBoxBorderWidth = _workingSettings.SpriteBoxBorderWidth;
        SpriteBoxCornerRadius = _workingSettings.SpriteBoxCornerRadius;
        EncountersBorderWidth = _workingSettings.EncountersBorderWidth;
        EncountersCornerRadius = _workingSettings.EncountersCornerRadius;
        StatsBorderWidth = _workingSettings.StatsBorderWidth;
        StatsCornerRadius = _workingSettings.StatsCornerRadius;
        ButtonBorderWidth = _workingSettings.ButtonBorderWidth;
        ButtonCornerRadius = _workingSettings.ButtonCornerRadius;
        StatsFrameInset = _workingSettings.StatsFrameInset;
        EncountersFrameInset = _workingSettings.EncountersFrameInset;
        SpriteBoxFrameInset = _workingSettings.SpriteBoxFrameInset;
        SpriteRowBackgroundColor = _workingSettings.SpriteRowBackgroundColor;
        SpriteRowBorderColor = _workingSettings.SpriteRowBorderColor;
        SpriteRowBorderWidth = _workingSettings.SpriteRowBorderWidth;
        SpriteRowCornerRadius = _workingSettings.SpriteRowCornerRadius;
        SpriteRowFrameInset = _workingSettings.SpriteRowFrameInset;
        StatsFrameOverhang = _workingSettings.StatsFrameOverhang;
        EncountersFrameOverhang = _workingSettings.EncountersFrameOverhang;
        SpriteBoxFrameOverhang = _workingSettings.SpriteBoxFrameOverhang;
        SpriteRowFrameOverhang = _workingSettings.SpriteRowFrameOverhang;
        MenuTextColor = _workingSettings.MenuTextColor;
        MenuHighlightColor = _workingSettings.MenuHighlightColor;
        MenuFontFamily = MenuFontFamilyDisplay(_workingSettings.MenuFontFamilyName);
        MenuFontSize = MenuFontSizeDisplay(_workingSettings.MenuFontSizeName);
        HeaderBackgroundColor = _workingSettings.HeaderBackgroundColor;
        BackgroundCustomColor = AppearanceSettings.FromArgbInt(_workingSettings.CustomBackgroundColorArgb);
        // §133: normalized above, so a saved legacy name selects a real
        // entry instead of leaving the dropdown blank - a ComboBox whose
        // SelectedItem is not in its ItemsSource shows nothing at all.
        SpriteBoxBackgroundColor = _workingSettings.SpriteBoxBackgroundColor;
        ButtonColor = _workingSettings.ButtonColor;
        StatsBackgroundColor = _workingSettings.StatsBackgroundColor;
        EncountersBackgroundColor = _workingSettings.EncountersBackgroundColor;

        int activeClient = SessionPersistenceService.ActiveClientNumber;

        ClientNote = activeClient > 0
            ? $"Editing {ClientNamesService.GetDisplayName(activeClient)}'s appearance."
            : $"No PRO client detected yet - this look is saved as {ClientNamesService.GetDisplayName(1)}'s appearance and applies right away.";

        LoadPreviewSprites();
        RefreshPreviewFonts();
        RefreshPanelBrushes();
        RefreshBorderPreview();
        RefreshPreview();
        ReloadAssets();

        SelectedCategory = Categories[0];

        _lastSnapshot = Snapshot();
        _suspendHistory = false;
        RefreshHistoryFlags();
    }

    /// <summary>Called by AppearanceWindow.axaml.cs after IStorageProvider returns a file.</summary>
    public void SetCustomBackground(string filePath)
    {
        _pendingCustomImagePath = filePath;
        _workingSettings.UseCustomBackground = true;
        _workingSettings.CustomBackgroundPath = string.Empty;
        _workingSettings.UseCustomGradient = false;

        RefreshPreview();
        Track("Background");
    }

    /// <summary>Called by AppearanceWindow.axaml.cs after CustomGradientWindow
    /// returns a confirmed Apply - see CustomGradientViewModel. Takes 2-4
    /// colors (whatever CustomGradientViewModel.Colors held at Apply time).</summary>
    public void SetCustomGradient(IReadOnlyList<Color> colors, string direction)
    {
        _workingSettings.UseCustomBackground = false;
        _workingSettings.CustomBackgroundPath = string.Empty;
        _pendingCustomImagePath = null;
        _workingSettings.UseCustomGradient = true;
        _workingSettings.CustomGradientColorArgbs = colors.Select(AppearanceSettings.ToArgbInt).ToList();
        _workingSettings.CustomGradientDirection = direction;

        RefreshPreview();
        Track("Background");
    }

    /// <summary>Switches to a plain custom-color background, using whatever the
    /// BackgroundCustomColor slider is currently set to.</summary>
    [RelayCommand]
    private void UseCustomColorBackground()
    {
        _workingSettings.UseCustomBackground = true;
        _workingSettings.CustomBackgroundPath = string.Empty;
        _workingSettings.UseCustomGradient = false;
        _pendingCustomImagePath = null;
        _workingSettings.CustomBackgroundColorArgb = AppearanceSettings.ToArgbInt(BackgroundCustomColor);

        RefreshPreview();
        Track("Background");
    }

    /// <summary>§103 "Reset": puts every control back to the application
    /// defaults (a fresh AppearanceSettings). Nothing is saved until Apply -
    /// closing the window still walks away from even a reset.</summary>
    [RelayCommand]
    private void ResetToDefaults()
    {
        LoadFrom(new AppearanceSettings(), null, "Reset");
        StatusText = "Every option is back at the application defaults. Nothing is saved until Apply.";
    }

    /// <summary>§209 "Revert": re-reads the saved appearance from disk,
    /// discarding unsaved edits.
    ///
    /// This used to be what the Load button did. §209 gave Load to theme
    /// files, so the command stays - it is the only way back to what you had
    /// before you started fiddling, since Reset goes to the app defaults
    /// instead. §381: it is an undoable step like any other.</summary>
    [RelayCommand]
    private void LoadSaved()
    {
        LoadFrom(AppearanceSettingsRepository.Load(), null, "Revert");
        StatusText = "Back to the appearance you last applied.";
    }

    /// <summary>Fills every picker from <paramref name="source"/>. The history
    /// is suspended while the pickers change, so the whole load is one step
    /// under <paramref name="historyKey"/> - or no step at all when the key is
    /// null, which is how Undo and Redo restore a snapshot without recording
    /// the restore as a fresh edit.</summary>
    private void LoadFrom(AppearanceSettings source, string? pendingImagePath, string? historyKey)
    {
        bool wasSuspended = _suspendHistory;
        _suspendHistory = true;

        // Background mode first (these fields aren't observable - the
        // observable properties below write THROUGH to _workingSettings via
        // their own partial handlers, so ordering matters only for these).
        _workingSettings.BackgroundId = source.BackgroundId;
        _workingSettings.UseCustomBackground = source.UseCustomBackground;
        _workingSettings.CustomBackgroundPath = source.CustomBackgroundPath;
        _workingSettings.UseCustomGradient = source.UseCustomGradient;
        _workingSettings.CustomGradientColorArgbs = new List<int>(source.CustomGradientColorArgbs);
        _workingSettings.CustomGradientDirection = source.CustomGradientDirection;
        _pendingCustomImagePath = pendingImagePath;

        // §382. The panel pictures are plain paths in the settings, so a
        // snapshot, a Reset and a Revert all carry them here.
        _workingSettings.StatsBackgroundImagePath = source.StatsBackgroundImagePath;
        _workingSettings.EncountersBackgroundImagePath = source.EncountersBackgroundImagePath;
        _workingSettings.SpriteBoxBackgroundImagePath = source.SpriteBoxBackgroundImagePath;

        // §384. Frames are paths too; widths, corners and corner sizes go
        // through their pickers, which write through like the colours.
        _workingSettings.StatsFrameImagePath = source.StatsFrameImagePath;
        _workingSettings.EncountersFrameImagePath = source.EncountersFrameImagePath;
        _workingSettings.SpriteBoxFrameImagePath = source.SpriteBoxFrameImagePath;
        _workingSettings.SpriteRowBackgroundImagePath = source.SpriteRowBackgroundImagePath;
        _workingSettings.SpriteRowFrameImagePath = source.SpriteRowFrameImagePath;

        SpriteBoxTextColor = source.SpriteBoxTextColor;
        SpriteBoxBorderColor = source.SpriteBoxBorderColor;
        SpriteBoxFontFamily = source.SpriteBoxFontFamilyName;
        SpriteBoxFontSize = ThemeManager.NormalizeFontSizeName(source.SpriteBoxFontSizeName);
        EncountersTextColor = source.EncountersTextColor;
        EncountersBorderColor = source.EncountersBorderColor;
        EncountersFontFamily = source.EncountersFontFamilyName;
        EncountersFontSize = ThemeManager.NormalizeFontSizeName(source.EncountersFontSizeName);
        StatsTextColor = source.StatsTextColor;
        StatsBorderColor = source.StatsBorderColor;
        StatsFontFamily = source.StatsFontFamilyName;
        StatsFontSize = ThemeManager.NormalizeFontSizeName(source.StatsFontSizeName);
        ButtonTextColor = source.ButtonTextColor;
        ButtonBorderColor = source.ButtonBorderColor;
        ButtonFontFamily = source.ButtonFontFamilyName;
        ButtonFontSize = ThemeManager.NormalizeFontSizeName(source.ButtonFontSizeName);
        BoldHeadings = source.BoldHeadings;
        SpriteBoxBorderWidth = source.SpriteBoxBorderWidth;
        SpriteBoxCornerRadius = source.SpriteBoxCornerRadius;
        EncountersBorderWidth = source.EncountersBorderWidth;
        EncountersCornerRadius = source.EncountersCornerRadius;
        StatsBorderWidth = source.StatsBorderWidth;
        StatsCornerRadius = source.StatsCornerRadius;
        ButtonBorderWidth = source.ButtonBorderWidth;
        ButtonCornerRadius = source.ButtonCornerRadius;
        StatsFrameInset = source.StatsFrameInset;
        EncountersFrameInset = source.EncountersFrameInset;
        SpriteBoxFrameInset = source.SpriteBoxFrameInset;
        SpriteRowBackgroundColor = source.SpriteRowBackgroundColor;
        SpriteRowBorderColor = source.SpriteRowBorderColor;
        SpriteRowBorderWidth = source.SpriteRowBorderWidth;
        SpriteRowCornerRadius = source.SpriteRowCornerRadius;
        SpriteRowFrameInset = source.SpriteRowFrameInset;
        StatsFrameOverhang = source.StatsFrameOverhang;
        EncountersFrameOverhang = source.EncountersFrameOverhang;
        SpriteBoxFrameOverhang = source.SpriteBoxFrameOverhang;
        SpriteRowFrameOverhang = source.SpriteRowFrameOverhang;
        MenuTextColor = source.MenuTextColor;
        MenuHighlightColor = source.MenuHighlightColor;
        MenuFontFamily = MenuFontFamilyDisplay(source.MenuFontFamilyName);
        MenuFontSize = MenuFontSizeDisplay(source.MenuFontSizeName);
        BackgroundCustomColor = AppearanceSettings.FromArgbInt(source.CustomBackgroundColorArgb);
        SpriteBoxBackgroundColor = source.SpriteBoxBackgroundColor;
        ButtonColor = source.ButtonColor;
        StatsBackgroundColor = source.StatsBackgroundColor;
        EncountersBackgroundColor = source.EncountersBackgroundColor;
        HeaderBackgroundColor = source.HeaderBackgroundColor;

        _suspendHistory = wasSuspended;

        SaveError = null;
        RefreshPreviewFonts();
        RefreshPanelBrushes();
        RefreshBorderPreview();
        RefreshPreview();

        if (historyKey is not null)
            Track(historyKey);
    }

    /// <summary>Drops a custom image/color/gradient and goes back to the one
    /// built-in background (§104: Slate, formerly one preset among six). Kept
    /// because with the preset picker gone this is the only way back to the
    /// built-in look that doesn't also reset every color the way
    /// ResetToDefaults does.</summary>
    [RelayCommand]
    private void ClearBackground()
    {
        _workingSettings.UseCustomBackground = false;
        _workingSettings.CustomBackgroundPath = string.Empty;
        _workingSettings.UseCustomGradient = false;
        _workingSettings.BackgroundId = AppearanceSettings.DefaultBackgroundId;
        _pendingCustomImagePath = null;

        RefreshPreview();
        Track("Background");
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

        Track(nameof(BackgroundCustomColor));
    }

    private void RefreshPreview()
    {
        try
        {
            if (_workingSettings.UseCustomGradient)
            {
                // Bypasses the image-path lookup below entirely - a gradient has
                // no file on disk, just colors and a direction.
                PreviewBackgroundImage = null;
                PreviewBackgroundBrush = ThemeManager.BuildGradientBrush(
                    _workingSettings.CustomGradientColors,
                    _workingSettings.CustomGradientDirection);
                return;
            }

            string? imagePath = CurrentPicturePath();

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
        finally
        {
            RefreshBackgroundModeText();
            RefreshAssetUsage();

            // §393: an automatic menu colour follows the background.
            RefreshMenuPreview();
        }
    }

    // ------------------------------------------------------------ §209

    /// <summary>§209. Set by AppearanceWindow.axaml.cs. Asks the user where to
    /// write a theme file, or which one to read - the same reason
    /// SelectCustomImageButton_Click lives in code-behind, that a picker needs
    /// a TopLevel a view model should not hold.</summary>
    public Func<string, Task<string?>>? RequestThemeSavePath { get; set; }

    public Func<Task<string?>>? RequestThemeOpenPath { get; set; }

    /// <summary>§209. What just happened, at the foot of the window beside the
    /// client note. Cleared by the next action.</summary>
    [ObservableProperty] private string statusText = string.Empty;

    [ObservableProperty] private bool hasStatus;

    partial void OnStatusTextChanged(string value) => HasStatus = !string.IsNullOrEmpty(value);

    /// <summary>§209. Writes this client's appearance file and repaints the
    /// whole app, WITHOUT closing - which is the point of having an Apply at
    /// all. Press it as often as you like; each press is a fresh save.
    ///
    /// This is what the Save button did before §209, minus the close. Save now
    /// means "write a file I can send somebody".</summary>
    [RelayCommand]
    private void Apply()
    {
        if (!Persist())
            return;

        StatusText = "Applied.";
    }

    /// <summary>§209. Exports the portable subset of the CURRENT edits - not
    /// the last applied ones - so a theme can be sent without being adopted
    /// first. Colours, gradient, font and font size travel; the picture
    /// background does not, because the image is a file on this disk.</summary>
    [RelayCommand]
    private async Task SaveThemeFile()
    {
        if (RequestThemeSavePath is null)
            return;

        SaveError = null;
        StatusText = string.Empty;

        string suggested = "my-theme" + AppearanceSettingsRepository.ThemeFileExtension;

        string? path = await RequestThemeSavePath(suggested);

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            AppearanceSettingsRepository.ExportTheme(
                _workingSettings, path, Path.GetFileNameWithoutExtension(path));

            StatusText = $"Theme saved to {Path.GetFileName(path)}. The picture background is not included - colours, gradient and font are.";
        }
        catch (Exception ex)
        {
            SaveError = $"The theme file could not be written.\n\n{ex.Message}";
        }
    }

    /// <summary>§209. Reads a shared theme into the pickers and the preview and
    /// stops there. Nothing is written until Apply, so a theme you dislike
    /// costs you nothing - closing the window walks away and your own look is
    /// untouched.
    ///
    /// Merged onto the current settings rather than replacing them, so the
    /// picture background and everything else the file does not carry survive.</summary>
    [RelayCommand]
    private async Task LoadThemeFile()
    {
        if (RequestThemeOpenPath is null)
            return;

        SaveError = null;
        StatusText = string.Empty;

        string? path = await RequestThemeOpenPath();

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            LoadFrom(AppearanceSettingsRepository.ImportTheme(path, _workingSettings), _pendingCustomImagePath, "Load");

            StatusText = $"Loaded {Path.GetFileName(path)}. Nothing is saved yet - press Apply to keep it.";
        }
        catch (Exception ex)
        {
            SaveError = $"That theme could not be loaded.\n\n{ex.Message}";
        }
    }

    /// <summary>§209. The write itself, shared by Apply. True when it worked.</summary>
    private bool Persist()
    {
        // Per-client appearance is keyed by SessionPersistenceService.AppearanceClientNumber,
        // which falls back to "1" before this tracker window has locked onto a real PRO
        // client (see that property's own remarks). §85 refused to save in that state,
        // because the file written would be appearance-client1.json even when this
        // window was about to become client 2/3/4, and nothing moves it afterwards.
        // §140 lifts that refusal by request - players who open Appearance before the
        // game hit that wall every time - and accepts the consequence it guarded
        // against: with no client detected, Apply writes Client 1's appearance, which
        // is the look this window is already showing (ThemeManager loads by the same
        // fallback) and the one it keeps if client 1 is what locks in. A window that
        // later locks onto client 2/3/4 switches to that client's own file, as it
        // always did, and ClientNote at the foot of the window says which file the
        // Apply button writes before it is pressed.
        try
        {
            if (_workingSettings.UseCustomBackground &&
                !string.IsNullOrWhiteSpace(_pendingCustomImagePath))
            {
                _workingSettings.CustomBackgroundPath =
                    AppearanceSettingsRepository.SaveCustomBackground(_pendingCustomImagePath);
            }

            // §382. Each panel picture becomes its per-client copy, the way
            // the window background does, so the library can lose the file
            // later without the applied look losing it. A path whose file
            // has gone is dropped rather than saved.
            _workingSettings.StatsBackgroundImagePath =
                PersistPanelPicture(_workingSettings.StatsBackgroundImagePath, "stats");
            _workingSettings.EncountersBackgroundImagePath =
                PersistPanelPicture(_workingSettings.EncountersBackgroundImagePath, "table");
            _workingSettings.SpriteBoxBackgroundImagePath =
                PersistPanelPicture(_workingSettings.SpriteBoxBackgroundImagePath, "sprites");
            _workingSettings.StatsFrameImagePath =
                PersistPanelPicture(_workingSettings.StatsFrameImagePath, "stats-frame");
            _workingSettings.EncountersFrameImagePath =
                PersistPanelPicture(_workingSettings.EncountersFrameImagePath, "table-frame");
            _workingSettings.SpriteBoxFrameImagePath =
                PersistPanelPicture(_workingSettings.SpriteBoxFrameImagePath, "sprites-frame");
            _workingSettings.SpriteRowBackgroundImagePath =
                PersistPanelPicture(_workingSettings.SpriteRowBackgroundImagePath, "sprite-row");
            _workingSettings.SpriteRowFrameImagePath =
                PersistPanelPicture(_workingSettings.SpriteRowFrameImagePath, "sprite-row-frame");

            AppearanceSettingsRepository.Save(_workingSettings);
            ThemeManager.Reload();

            SaveError = null;

            return true;
        }
        catch (Exception ex)
        {
            SaveError = $"The appearance settings could not be saved.\n\n{ex.Message}";
            return false;
        }
    }

    private static string PersistPanelPicture(string path, string slot)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return string.Empty;

        return AppearanceSettingsRepository.SavePanelImage(path, slot);
    }

    // ============================================================ §382
    // Panel pictures: a picture behind the statistics panel and one behind
    // the encounter table, drawn over the window background and under the
    // panel's fill. The canvas paints them through the same
    // ThemeManager.BuildPanelBrush the real window uses.
    // ============================================================

    [ObservableProperty] private IBrush statsPreviewBrush = Brushes.Transparent;
    [ObservableProperty] private IBrush encountersPreviewBrush = Brushes.Transparent;
    [ObservableProperty] private IBrush spriteBoxPreviewBrush = Brushes.Transparent;
    [ObservableProperty] private IBrush spriteRowPreviewBrush = Brushes.Transparent;

    [ObservableProperty] private string statsPictureText = "No picture";
    [ObservableProperty] private string encountersPictureText = "No picture";
    [ObservableProperty] private string spriteBoxPictureText = "No picture";
    [ObservableProperty] private string spriteRowPictureText = "No picture";

    [ObservableProperty] private bool hasStatsPicture;
    [ObservableProperty] private bool hasEncountersPicture;
    [ObservableProperty] private bool hasSpriteBoxPicture;
    [ObservableProperty] private bool hasSpriteRowPicture;

    private void RefreshPanelBrushes()
    {
        StatsPreviewBrush = ThemeManager.BuildPanelBrush(StatsBackgroundColor, _workingSettings.StatsBackgroundImagePath);
        EncountersPreviewBrush = ThemeManager.BuildPanelBrush(EncountersBackgroundColor, _workingSettings.EncountersBackgroundImagePath);
        SpriteBoxPreviewBrush = ThemeManager.BuildPanelBrush(SpriteBoxBackgroundColor, _workingSettings.SpriteBoxBackgroundImagePath);
        SpriteRowPreviewBrush = ThemeManager.BuildPanelBrush(SpriteRowBackgroundColor, _workingSettings.SpriteRowBackgroundImagePath);

        HasStatsPicture = ThemeManager.HasPanelPicture(_workingSettings.StatsBackgroundImagePath);
        HasEncountersPicture = ThemeManager.HasPanelPicture(_workingSettings.EncountersBackgroundImagePath);
        HasSpriteBoxPicture = ThemeManager.HasPanelPicture(_workingSettings.SpriteBoxBackgroundImagePath);
        HasSpriteRowPicture = ThemeManager.HasPanelPicture(_workingSettings.SpriteRowBackgroundImagePath);

        StatsPictureText = HasStatsPicture
            ? $"Picture  ·  {DescribePicture(_workingSettings.StatsBackgroundImagePath)}"
            : "No picture  ·  the fill alone";
        EncountersPictureText = HasEncountersPicture
            ? $"Picture  ·  {DescribePicture(_workingSettings.EncountersBackgroundImagePath)}"
            : "No picture  ·  the fill alone";
        SpriteBoxPictureText = HasSpriteBoxPicture
            ? $"Picture  ·  {DescribePicture(_workingSettings.SpriteBoxBackgroundImagePath)}"
            : "No picture  ·  the fill alone";
        SpriteRowPictureText = HasSpriteRowPicture
            ? $"Picture  ·  {DescribePicture(_workingSettings.SpriteRowBackgroundImagePath)}"
            : "No picture  ·  the fill alone";

        RefreshAssetUsage();
    }

    /// <summary>Sets or clears (with null) a panel's picture. The path is a
    /// library file until Apply copies it beside the window background.</summary>
    private void SetPanelPicture(AppearanceElementKind panel, string? path)
    {
        string value = path ?? string.Empty;

        switch (panel)
        {
            case AppearanceElementKind.Statistics:
                _workingSettings.StatsBackgroundImagePath = value;
                break;
            case AppearanceElementKind.EncounterTable:
                _workingSettings.EncountersBackgroundImagePath = value;
                break;
            case AppearanceElementKind.SpriteBoxes:
                _workingSettings.SpriteBoxBackgroundImagePath = value;
                break;
            case AppearanceElementKind.SpriteRow:
                _workingSettings.SpriteRowBackgroundImagePath = value;
                break;
            default:
                return;
        }

        RefreshPanelBrushes();
        RefreshBorderPreview();
        Track($"{panel}Picture");
    }

    [RelayCommand]
    private void RemoveSpriteRowPicture()
    {
        SetPanelPicture(AppearanceElementKind.SpriteRow, null);
        StatusText = "The sprite row panel shows its fill alone. Press Apply to keep it.";
    }

    [RelayCommand]
    private void RemoveSpriteBoxPicture()
    {
        SetPanelPicture(AppearanceElementKind.SpriteBoxes, null);
        StatusText = "The sprite boxes show their fill alone. Press Apply to keep it.";
    }

    [RelayCommand]
    private void RemoveStatsPicture()
    {
        SetPanelPicture(AppearanceElementKind.Statistics, null);
        StatusText = "The statistics panel shows its fill alone. Press Apply to keep it.";
    }

    [RelayCommand]
    private void RemoveEncountersPicture()
    {
        SetPanelPicture(AppearanceElementKind.EncounterTable, null);
        StatusText = "The encounter table shows its fill alone. Press Apply to keep it.";
    }

    // ------------------------------------------------------------ §384
    // Where a library click lands. A picker rather than the selected
    // element alone, because a panel now takes two pictures - one behind
    // it and one round it - and the click has to say which.

    public IReadOnlyList<AssetTargetOption> AssetTargetOptions { get; } =
    [
        new(AssetTargetKind.Window, "Window background", "Window"),
        new(AssetTargetKind.StatsPicture, "Statistics panel  ·  picture behind", "Stats"),
        new(AssetTargetKind.StatsFrame, "Statistics panel  ·  frame", "Stats frame"),
        new(AssetTargetKind.TablePicture, "Encounter table  ·  picture behind", "Table"),
        new(AssetTargetKind.TableFrame, "Encounter table  ·  frame", "Table frame"),
        new(AssetTargetKind.SpritesPicture, "Sprite boxes  ·  picture behind", "Sprites"),
        new(AssetTargetKind.SpritesFrame, "Sprite boxes  ·  frame", "Sprite frame"),
        new(AssetTargetKind.SpriteRowPicture, "Sprite row panel  ·  picture behind", "Row"),
        new(AssetTargetKind.SpriteRowFrame, "Sprite row panel  ·  frame", "Row frame")
    ];

    [ObservableProperty] private AssetTargetOption? selectedAssetTarget;

    [ObservableProperty] private string assetTargetText = "Click a picture to use it as the window background";

    private AssetTargetKind CurrentTarget => SelectedAssetTarget?.Kind ?? AssetTargetKind.Window;

    partial void OnSelectedAssetTargetChanged(AssetTargetOption? value)
    {
        RefreshAssetTargetText();
        RefreshAssetUsage();
    }

    /// <summary>Selecting an element on the canvas points the picker at that
    /// element's picture-behind; anything without one points it at the
    /// window. The user can then move it to a frame.</summary>
    private void DefaultAssetTargetFor(AppearanceElementKind element)
    {
        AssetTargetKind kind = element switch
        {
            AppearanceElementKind.Statistics => AssetTargetKind.StatsPicture,
            AppearanceElementKind.EncounterTable => AssetTargetKind.TablePicture,
            AppearanceElementKind.SpriteBoxes => AssetTargetKind.SpritesPicture,
            AppearanceElementKind.SpriteRow => AssetTargetKind.SpriteRowPicture,
            _ => AssetTargetKind.Window
        };

        SelectedAssetTarget = AssetTargetOptions.First(o => o.Kind == kind);
    }

    private void RefreshAssetTargetText()
    {
        AssetTargetText = CurrentTarget switch
        {
            AssetTargetKind.StatsPicture => "Click a picture to put it behind the statistics panel",
            AssetTargetKind.TablePicture => "Click a picture to put it behind the encounter table",
            AssetTargetKind.SpritesPicture => "Click a picture to put it behind the sprite boxes",
            AssetTargetKind.StatsFrame => "Click a picture to frame the statistics panel",
            AssetTargetKind.TableFrame => "Click a picture to frame the encounter table",
            AssetTargetKind.SpritesFrame => "Click a picture to frame each sprite box",
            AssetTargetKind.SpriteRowPicture => "Click a picture to put it behind the whole sprite row",
            AssetTargetKind.SpriteRowFrame => "Click a picture to frame the sprite row panel",
            _ => "Click a picture to use it as the window background"
        };
    }

    /// <summary>The picture a target is showing, if it is a file: the
    /// window's per CurrentPicturePath, a panel's per its path.</summary>
    private string? PicturePathFor(AssetTargetKind target)
    {
        string path = target switch
        {
            AssetTargetKind.StatsPicture => _workingSettings.StatsBackgroundImagePath,
            AssetTargetKind.TablePicture => _workingSettings.EncountersBackgroundImagePath,
            AssetTargetKind.SpritesPicture => _workingSettings.SpriteBoxBackgroundImagePath,
            AssetTargetKind.StatsFrame => _workingSettings.StatsFrameImagePath,
            AssetTargetKind.TableFrame => _workingSettings.EncountersFrameImagePath,
            AssetTargetKind.SpritesFrame => _workingSettings.SpriteBoxFrameImagePath,
            AssetTargetKind.SpriteRowPicture => _workingSettings.SpriteRowBackgroundImagePath,
            AssetTargetKind.SpriteRowFrame => _workingSettings.SpriteRowFrameImagePath,
            _ => string.Empty
        };

        if (target == AssetTargetKind.Window)
            return CurrentPicturePath();

        return ThemeManager.HasPanelPicture(path) ? path : null;
    }

    // ============================================================ §386
    // Sizes: what the main window's panels measure right now, beside each
    // Picture… button, so a user making a picture knows what covers it;
    // and every picture's own size wherever its name is shown.
    // ============================================================

    [ObservableProperty] private string windowSizeText = string.Empty;
    [ObservableProperty] private string statsSizeText = string.Empty;
    [ObservableProperty] private string encountersSizeText = string.Empty;
    [ObservableProperty] private string spriteBoxSizeText = string.Empty;
    [ObservableProperty] private string spriteRowSizeText = string.Empty;

    /// <summary>Called by AppearanceWindow.axaml.cs when the editor opens,
    /// with what the tracker window behind it measures. Device pixels: a
    /// picture is pixels, and on a 150% display a 220-wide panel is 330 of
    /// them. The stats panel measures 0x0 while hidden, which is said.</summary>
    public void SetMeasuredSizes(AppearancePanelSizes sizes)
    {
        double scale = sizes.Scaling > 0 && double.IsFinite(sizes.Scaling) ? sizes.Scaling : 1;

        static string Pixels(Size size, double scale) =>
            size.Width <= 0 || size.Height <= 0
                ? "not showing"
                : $"{Math.Round(size.Width * scale)} × {Math.Round(size.Height * scale)} px";

        string at = Math.Abs(scale - 1) > 0.01 ? $"  at {scale:P0}" : string.Empty;

        WindowSizeText = Pixels(sizes.Window, scale) + at;
        StatsSizeText = Pixels(sizes.Stats, scale) + at;
        EncountersSizeText = Pixels(sizes.Table, scale) + at;

        string box = Pixels(sizes.SpriteBox, scale);

        if (sizes.TargetSpriteBox > 0 && Math.Abs(sizes.TargetSpriteBox - sizes.SpriteBox.Width) > 0.5)
        {
            double target = Math.Round(sizes.TargetSpriteBox * scale);
            box += $"  ·  targets {target} × {target}";
        }

        SpriteBoxSizeText = box + at;
        SpriteRowSizeText = Pixels(sizes.SpriteRow, scale) + at;
    }

    private readonly Dictionary<string, (long Length, DateTime Written, string Dimensions)> _dimensions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"1920 × 1080" for a picture, read from its header and
    /// remembered by file size and write time; empty when unreadable.</summary>
    private string DimensionsOf(string path)
    {
        try
        {
            var info = new FileInfo(path);

            if (_dimensions.TryGetValue(path, out var cached) &&
                cached.Length == info.Length &&
                cached.Written == info.LastWriteTimeUtc)
            {
                return cached.Dimensions;
            }

            string dimensions = AppearanceAssetLibrary.TryReadImageSize(path, out int width, out int height)
                ? $"{width} × {height}"
                : string.Empty;

            _dimensions[path] = (info.Length, info.LastWriteTimeUtc, dimensions);
            return dimensions;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>The file name with its size: "forest.png · 1920 × 1080".</summary>
    private string DescribePicture(string path)
    {
        string dimensions = DimensionsOf(path);

        return dimensions.Length > 0
            ? $"{Path.GetFileName(path)}  ·  {dimensions}"
            : Path.GetFileName(path);
    }

    // ============================================================ §385
    // The user's own fonts: files added from the editor, loaded live by
    // UserFontService, named in the settings by the family the font
    // declares. Nothing here is persisted but the files.
    // ============================================================

    /// <summary>The families the user has added, for the Typography view.</summary>
    public ObservableCollection<string> UserFonts { get; } = new(UserFontService.FamilyNames);

    [ObservableProperty] private bool hasUserFonts = UserFontService.FamilyNames.Count > 0;

    public string FontsFolder => UserFontService.FontsFolder;

    /// <summary>Rebuilds the dropdowns' list in place - in place, so the
    /// four ComboBoxes bound to it keep their selection.</summary>
    private void RefreshFontOptions()
    {
        var wanted = SystemFontFamilyNames
            .Concat(ThemeManager.CustomFontCatalog.Keys)
            .Concat(UserFontService.FamilyNames)
            .ToList();

        for (int i = FontFamilyOptions.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(FontFamilyOptions[i]))
                FontFamilyOptions.RemoveAt(i);
        }

        foreach (string name in wanted)
        {
            if (!FontFamilyOptions.Contains(name))
                FontFamilyOptions.Add(name);
        }

        // §394: the menu's list, the same way, behind its first entry.
        for (int i = MenuFontFamilyOptions.Count - 1; i >= 1; i--)
        {
            if (!wanted.Contains(MenuFontFamilyOptions[i]))
                MenuFontFamilyOptions.RemoveAt(i);
        }

        foreach (string name in wanted)
        {
            if (!MenuFontFamilyOptions.Contains(name))
                MenuFontFamilyOptions.Add(name);
        }

        UserFonts.Clear();

        foreach (string name in UserFontService.FamilyNames)
            UserFonts.Add(name);

        HasUserFonts = UserFonts.Count > 0;
    }

    /// <summary>Called by AppearanceWindow.axaml.cs after the font picker
    /// returns. Each file is copied into the Fonts folder and loaded; the
    /// last family added is not selected anywhere - the user picks it in a
    /// Font box, where it now appears - because which of the three sections
    /// it was for is theirs to say.</summary>
    public void ImportFonts(IReadOnlyList<string> paths)
    {
        SaveError = null;
        var added = new List<string>();

        foreach (string path in paths)
        {
            try
            {
                string family = UserFontService.Add(path);

                if (!added.Contains(family))
                    added.Add(family);
            }
            catch (Exception ex)
            {
                SaveError = $"{Path.GetFileName(path)} could not be added.\n\n{ex.Message}";
            }
        }

        RefreshFontOptions();

        if (added.Count == 0)
            return;

        StatusText = added.Count == 1
            ? $"{added[0]} added. Pick it in a Font box - the sprite boxes, the encounter table, Statistics, Buttons, or the menu bar."
            : $"{string.Join(", ", added)} added. Pick them in the Font boxes.";
    }

    [RelayCommand]
    private void RemoveUserFont(string? family)
    {
        if (string.IsNullOrWhiteSpace(family))
            return;

        SaveError = null;

        var usedBy = new List<string>();

        // §388: the table has its own font, so it is its own line here - a
        // font only the table uses must not come out from under it.
        if (string.Equals(SpriteBoxFontFamily, family, StringComparison.OrdinalIgnoreCase)) usedBy.Add("the sprite boxes");
        if (string.Equals(EncountersFontFamily, family, StringComparison.OrdinalIgnoreCase)) usedBy.Add("the encounter table");
        if (string.Equals(StatsFontFamily, family, StringComparison.OrdinalIgnoreCase)) usedBy.Add("Statistics");
        if (string.Equals(ButtonFontFamily, family, StringComparison.OrdinalIgnoreCase)) usedBy.Add("Buttons");
        if (string.Equals(MenuFontFamily, family, StringComparison.OrdinalIgnoreCase)) usedBy.Add("the menu bar");

        if (usedBy.Count > 0)
        {
            StatusText = $"{family} is in use by {string.Join(" and ", usedBy)} - choose another font there first.";
            return;
        }

        try
        {
            if (UserFontService.Remove(family))
                StatusText = $"{family} removed from the Fonts folder. It keeps working until the app restarts.";
        }
        catch (Exception ex)
        {
            SaveError = $"{family} could not be removed.\n\n{ex.Message}";
        }

        RefreshFontOptions();
    }

    // ============================================================ §384
    // Borders: width and corners per section, a frame picture round the
    // statistics panel, the table and each sprite box. The canvas draws
    // them through the same clamps and the same NineSliceFrame the real
    // window uses.
    // ============================================================

    public double MaxBorderWidth => ThemeManager.MaxBorderWidth;
    public double MaxCornerRadius => ThemeManager.MaxCornerRadius;
    public double MinFrameInset => ThemeManager.MinFrameInset;
    public double MaxFrameInset => ThemeManager.MaxFrameInset;

    [ObservableProperty] private double spriteBoxBorderWidth = 1;
    [ObservableProperty] private double spriteBoxCornerRadius;
    [ObservableProperty] private double encountersBorderWidth = 1;
    [ObservableProperty] private double encountersCornerRadius;
    [ObservableProperty] private double statsBorderWidth = 1;
    [ObservableProperty] private double statsCornerRadius;
    [ObservableProperty] private double buttonBorderWidth = 1;
    [ObservableProperty] private double buttonCornerRadius = 3;
    [ObservableProperty] private double statsFrameInset = 24;
    [ObservableProperty] private double encountersFrameInset = 24;
    [ObservableProperty] private double spriteBoxFrameInset = 24;
    [ObservableProperty] private double spriteRowBorderWidth = 1;
    [ObservableProperty] private double spriteRowCornerRadius;
    [ObservableProperty] private double spriteRowFrameInset = 24;

    // §393. How far each frame reaches past its panel.
    public double MaxFrameOverhang => ThemeManager.MaxFrameOverhang;

    [ObservableProperty] private double statsFrameOverhang;
    [ObservableProperty] private double encountersFrameOverhang;
    [ObservableProperty] private double spriteBoxFrameOverhang;
    [ObservableProperty] private double spriteRowFrameOverhang;

    partial void OnStatsFrameOverhangChanged(double value)
    {
        _workingSettings.StatsFrameOverhang = ThemeManager.ClampFrameOverhang(value);
        RefreshBorderPreview();
        Track(nameof(StatsFrameOverhang));
    }

    partial void OnEncountersFrameOverhangChanged(double value)
    {
        _workingSettings.EncountersFrameOverhang = ThemeManager.ClampFrameOverhang(value);
        RefreshBorderPreview();
        Track(nameof(EncountersFrameOverhang));
    }

    partial void OnSpriteBoxFrameOverhangChanged(double value)
    {
        _workingSettings.SpriteBoxFrameOverhang = ThemeManager.ClampFrameOverhang(value);
        RefreshBorderPreview();
        Track(nameof(SpriteBoxFrameOverhang));
    }

    partial void OnSpriteRowFrameOverhangChanged(double value)
    {
        _workingSettings.SpriteRowFrameOverhang = ThemeManager.ClampFrameOverhang(value);
        RefreshBorderPreview();
        Track(nameof(SpriteRowFrameOverhang));
    }

    // The canvas frames' margins - negative, the way the window's are.
    [ObservableProperty] private Thickness statsFramePreviewOverhang;
    [ObservableProperty] private Thickness tableFramePreviewOverhang;
    [ObservableProperty] private Thickness spriteBoxFramePreviewOverhang;
    [ObservableProperty] private Thickness spriteRowFramePreviewOverhang;

    partial void OnSpriteRowBorderWidthChanged(double value)
    {
        _workingSettings.SpriteRowBorderWidth = ThemeManager.ClampBorderWidth(value);
        RefreshBorderPreview();
        Track(nameof(SpriteRowBorderWidth));
    }

    partial void OnSpriteRowCornerRadiusChanged(double value)
    {
        _workingSettings.SpriteRowCornerRadius = ThemeManager.ClampCornerRadius(value);
        RefreshBorderPreview();
        Track(nameof(SpriteRowCornerRadius));
    }

    partial void OnSpriteRowFrameInsetChanged(double value)
    {
        _workingSettings.SpriteRowFrameInset = ThemeManager.ClampFrameInset(value);
        RefreshBorderPreview();
        Track(nameof(SpriteRowFrameInset));
    }

    partial void OnSpriteBoxBorderWidthChanged(double value)
    {
        _workingSettings.SpriteBoxBorderWidth = ThemeManager.ClampBorderWidth(value);
        RefreshBorderPreview();
        Track(nameof(SpriteBoxBorderWidth));
    }

    partial void OnSpriteBoxCornerRadiusChanged(double value)
    {
        _workingSettings.SpriteBoxCornerRadius = ThemeManager.ClampCornerRadius(value);
        RefreshBorderPreview();
        Track(nameof(SpriteBoxCornerRadius));
    }

    partial void OnEncountersBorderWidthChanged(double value)
    {
        _workingSettings.EncountersBorderWidth = ThemeManager.ClampBorderWidth(value);
        RefreshBorderPreview();
        Track(nameof(EncountersBorderWidth));
    }

    partial void OnEncountersCornerRadiusChanged(double value)
    {
        _workingSettings.EncountersCornerRadius = ThemeManager.ClampCornerRadius(value);
        RefreshBorderPreview();
        Track(nameof(EncountersCornerRadius));
    }

    partial void OnStatsBorderWidthChanged(double value)
    {
        _workingSettings.StatsBorderWidth = ThemeManager.ClampBorderWidth(value);
        RefreshBorderPreview();
        Track(nameof(StatsBorderWidth));
    }

    partial void OnStatsCornerRadiusChanged(double value)
    {
        _workingSettings.StatsCornerRadius = ThemeManager.ClampCornerRadius(value);
        RefreshBorderPreview();
        Track(nameof(StatsCornerRadius));
    }

    partial void OnButtonBorderWidthChanged(double value)
    {
        _workingSettings.ButtonBorderWidth = ThemeManager.ClampBorderWidth(value);
        RefreshBorderPreview();
        Track(nameof(ButtonBorderWidth));
    }

    partial void OnButtonCornerRadiusChanged(double value)
    {
        _workingSettings.ButtonCornerRadius = ThemeManager.ClampCornerRadius(value);
        RefreshBorderPreview();
        Track(nameof(ButtonCornerRadius));
    }

    partial void OnStatsFrameInsetChanged(double value)
    {
        _workingSettings.StatsFrameInset = ThemeManager.ClampFrameInset(value);
        RefreshBorderPreview();
        Track(nameof(StatsFrameInset));
    }

    partial void OnEncountersFrameInsetChanged(double value)
    {
        _workingSettings.EncountersFrameInset = ThemeManager.ClampFrameInset(value);
        RefreshBorderPreview();
        Track(nameof(EncountersFrameInset));
    }

    partial void OnSpriteBoxFrameInsetChanged(double value)
    {
        _workingSettings.SpriteBoxFrameInset = ThemeManager.ClampFrameInset(value);
        RefreshBorderPreview();
        Track(nameof(SpriteBoxFrameInset));
    }

    // What the canvas binds to: the clamped values as the types the window
    // binds to, the frame pictures, and the layout a frame changes.
    [ObservableProperty] private Thickness spriteBoxPreviewBorderThickness = new(1);
    [ObservableProperty] private CornerRadius spriteBoxPreviewCornerRadius;
    [ObservableProperty] private Thickness encountersPreviewBorderThickness = new(1);
    [ObservableProperty] private Thickness encountersPreviewHeaderLine = new(0, 0, 0, 1);
    [ObservableProperty] private CornerRadius encountersPreviewCornerRadius;
    [ObservableProperty] private Thickness statsPreviewBorderThickness = new(1);
    [ObservableProperty] private CornerRadius statsPreviewCornerRadius;
    [ObservableProperty] private Thickness buttonPreviewBorderThickness = new(1);
    [ObservableProperty] private CornerRadius buttonPreviewCornerRadius = new(3);
    [ObservableProperty] private Thickness spriteRowPreviewBorderThickness;
    [ObservableProperty] private CornerRadius spriteRowPreviewCornerRadius;
    [ObservableProperty] private Thickness spriteRowPreviewPadding;

    [ObservableProperty] private IImage? statsFrameImage;
    [ObservableProperty] private IImage? tableFrameImage;
    [ObservableProperty] private IImage? spriteBoxFrameImage;
    [ObservableProperty] private IImage? spriteRowFrameImage;

    [ObservableProperty] private Thickness statsPreviewPadding = new(10);
    [ObservableProperty] private Thickness tablePreviewMargin;
    [ObservableProperty] private Thickness spriteImagePreviewMargin = new(CanvasSpriteMargin);

    [ObservableProperty] private string statsFrameText = "No frame";
    [ObservableProperty] private string encountersFrameText = "No frame";
    [ObservableProperty] private string spriteBoxFrameText = "No frame";
    [ObservableProperty] private string spriteRowFrameText = "No frame";

    [ObservableProperty] private bool hasStatsFrame;
    [ObservableProperty] private bool hasEncountersFrame;
    [ObservableProperty] private bool hasSpriteBoxFrame;
    [ObservableProperty] private bool hasSpriteRowFrame;

    /// <summary>The canvas draws its sprites 8px in from the box, as the
    /// mock always has; a frame's margin adds to that.</summary>
    private const double CanvasSpriteMargin = 8;

    private readonly Dictionary<string, (long Length, DateTime Written, IImage? Image)> _frameImages =
        new(StringComparer.OrdinalIgnoreCase);

    private void RefreshBorderPreview()
    {
        SpriteBoxPreviewBorderThickness = new Thickness(ThemeManager.ClampBorderWidth(SpriteBoxBorderWidth));
        SpriteBoxPreviewCornerRadius = new CornerRadius(ThemeManager.ClampCornerRadius(SpriteBoxCornerRadius));

        double tableWidth = ThemeManager.ClampBorderWidth(EncountersBorderWidth);
        EncountersPreviewBorderThickness = new Thickness(tableWidth);
        EncountersPreviewHeaderLine = new Thickness(0, 0, 0, tableWidth);
        EncountersPreviewCornerRadius = new CornerRadius(ThemeManager.ClampCornerRadius(EncountersCornerRadius));

        StatsPreviewBorderThickness = new Thickness(ThemeManager.ClampBorderWidth(StatsBorderWidth));
        StatsPreviewCornerRadius = new CornerRadius(ThemeManager.ClampCornerRadius(StatsCornerRadius));
        ButtonPreviewBorderThickness = new Thickness(ThemeManager.ClampBorderWidth(ButtonBorderWidth));
        ButtonPreviewCornerRadius = new CornerRadius(ThemeManager.ClampCornerRadius(ButtonCornerRadius));

        StatsFrameImage = FrameImageFor(_workingSettings.StatsFrameImagePath);
        TableFrameImage = FrameImageFor(_workingSettings.EncountersFrameImagePath);
        SpriteBoxFrameImage = FrameImageFor(_workingSettings.SpriteBoxFrameImagePath);

        HasStatsFrame = StatsFrameImage is not null;
        HasEncountersFrame = TableFrameImage is not null;
        HasSpriteBoxFrame = SpriteBoxFrameImage is not null;

        StatsFrameText = HasStatsFrame ? $"Frame  ·  {DescribePicture(_workingSettings.StatsFrameImagePath)}" : "No frame  ·  the line alone";
        EncountersFrameText = HasEncountersFrame ? $"Frame  ·  {DescribePicture(_workingSettings.EncountersFrameImagePath)}" : "No frame  ·  the line alone";
        SpriteBoxFrameText = HasSpriteBoxFrame ? $"Frame  ·  {DescribePicture(_workingSettings.SpriteBoxFrameImagePath)}" : "No frame  ·  the line alone";

        double statsInset = ThemeManager.ClampFrameInset(StatsFrameInset);
        double tableInset = ThemeManager.ClampFrameInset(EncountersFrameInset);
        double spriteInset = ThemeManager.ClampFrameInset(SpriteBoxFrameInset);

        // §393: the overhangs, through the same clamp and rules as Apply.
        double statsOverhang = ThemeManager.ClampFrameOverhang(StatsFrameOverhang);
        double tableOverhang = ThemeManager.ClampFrameOverhang(EncountersFrameOverhang);
        double spriteOverhang = ThemeManager.ClampFrameOverhang(SpriteBoxFrameOverhang);

        StatsFramePreviewOverhang = ThemeManager.FrameOverhangMargin(statsOverhang);
        TableFramePreviewOverhang = ThemeManager.FrameOverhangMargin(tableOverhang);
        SpriteBoxFramePreviewOverhang = ThemeManager.FrameOverhangMargin(spriteOverhang);

        StatsPreviewPadding = new Thickness(ThemeManager.StatsPanelPaddingFor(HasStatsFrame, statsInset, statsOverhang));
        TablePreviewMargin = new Thickness(ThemeManager.TableFrameMarginFor(HasEncountersFrame, tableInset, tableOverhang));
        SpriteImagePreviewMargin = new Thickness(CanvasSpriteMargin + ThemeManager.SpriteImageMarginFor(HasSpriteBoxFrame, spriteInset, spriteOverhang));

        // §387. The row panel: its line and inset only once something shows.
        SpriteRowFrameImage = FrameImageFor(_workingSettings.SpriteRowFrameImagePath);
        HasSpriteRowFrame = SpriteRowFrameImage is not null;
        SpriteRowFrameText = HasSpriteRowFrame ? $"Frame  ·  {DescribePicture(_workingSettings.SpriteRowFrameImagePath)}" : "No frame  ·  the line alone";

        double rowWidth = ThemeManager.ClampBorderWidth(SpriteRowBorderWidth);
        double rowInset = ThemeManager.ClampFrameInset(SpriteRowFrameInset);
        double rowOverhang = ThemeManager.ClampFrameOverhang(SpriteRowFrameOverhang);
        bool rowVisible = ThemeManager.SpriteRowIsVisible(SpriteRowBackgroundColor, SpriteRowBorderColor, rowWidth,
            ThemeManager.HasPanelPicture(_workingSettings.SpriteRowBackgroundImagePath));

        SpriteRowPreviewBorderThickness = new Thickness(rowVisible ? rowWidth : 0);
        SpriteRowPreviewCornerRadius = new CornerRadius(ThemeManager.ClampCornerRadius(SpriteRowCornerRadius));
        SpriteRowPreviewPadding = new Thickness(ThemeManager.SpriteRowPaddingFor(rowVisible, HasSpriteRowFrame, rowInset, rowOverhang));
        SpriteRowFramePreviewOverhang = ThemeManager.FrameOverhangMargin(rowOverhang);

        RefreshAssetUsage();
    }

    /// <summary>The frame picture for a path, read once per file: a slider
    /// drag would otherwise decode it at every tick.</summary>
    private IImage? FrameImageFor(string path)
    {
        if (!ThemeManager.HasPanelPicture(path))
            return null;

        try
        {
            var info = new FileInfo(path);

            if (_frameImages.TryGetValue(path, out var cached) &&
                cached.Length == info.Length &&
                cached.Written == info.LastWriteTimeUtc)
            {
                return cached.Image;
            }

            IImage? image = ThemeManager.LoadFrameImage(path);
            _frameImages[path] = (info.Length, info.LastWriteTimeUtc, image);
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Sets or clears (with null) a panel's frame picture.</summary>
    private void SetFramePicture(AppearanceElementKind panel, string? path)
    {
        string value = path ?? string.Empty;

        switch (panel)
        {
            case AppearanceElementKind.Statistics:
                _workingSettings.StatsFrameImagePath = value;
                break;
            case AppearanceElementKind.EncounterTable:
                _workingSettings.EncountersFrameImagePath = value;
                break;
            case AppearanceElementKind.SpriteBoxes:
                _workingSettings.SpriteBoxFrameImagePath = value;
                break;
            case AppearanceElementKind.SpriteRow:
                _workingSettings.SpriteRowFrameImagePath = value;
                break;
            default:
                return;
        }

        RefreshBorderPreview();
        Track($"{panel}Frame");
    }

    [RelayCommand]
    private void RemoveSpriteRowFrame()
    {
        SetFramePicture(AppearanceElementKind.SpriteRow, null);
        StatusText = "The sprite row panel has no frame. Press Apply to keep it.";
    }

    [RelayCommand]
    private void RemoveStatsFrame()
    {
        SetFramePicture(AppearanceElementKind.Statistics, null);
        StatusText = "The statistics panel has no frame. Press Apply to keep it.";
    }

    [RelayCommand]
    private void RemoveEncountersFrame()
    {
        SetFramePicture(AppearanceElementKind.EncounterTable, null);
        StatusText = "The encounter table has no frame. Press Apply to keep it.";
    }

    [RelayCommand]
    private void RemoveSpriteBoxFrame()
    {
        SetFramePicture(AppearanceElementKind.SpriteBoxes, null);
        StatusText = "The sprite boxes have no frame. Press Apply to keep it.";
    }

    /// <summary>Called by the window before it opens the picker for one of
    /// the inspector's Picture… / Frame… buttons, which name their target
    /// in Tag, so the picked file lands there whatever the library's own
    /// picker was pointing at.</summary>
    public void PointAssetTargetAt(AssetTargetKind kind) =>
        SelectedAssetTarget = AssetTargetOptions.First(o => o.Kind == kind);

    // ============================================================ §381
    // The editor. Everything from here down is STATE about editing - what
    // is selected, what the inspector shows, the undo history, the asset
    // library - and none of it is persisted; the settings above are.
    // ============================================================

    // ------------------------------------------------------------ navigation

    public IReadOnlyList<AppearanceCategory> Categories { get; } = BuildCategories();

    [ObservableProperty] private AppearanceCategory? selectedCategory;

    [ObservableProperty] private AppearanceElementKind selectedElement = AppearanceElementKind.Background;

    [ObservableProperty] private string selectedElementName = "Background";

    [ObservableProperty] private string selectedElementDetail = string.Empty;

    [ObservableProperty] private string breadcrumb = "Appearance  ›  Background";

    /// <summary>Preview hides the selection outline and the element tag, so
    /// the canvas shows the appearance and nothing else.</summary>
    [ObservableProperty] private bool isPreviewMode;

    // Which inspector is showing. One is true at a time.
    [ObservableProperty] private bool showElementInspector = true;
    [ObservableProperty] private bool showBordersInspector;
    [ObservableProperty] private bool showTypographyInspector;
    [ObservableProperty] private bool showAssetsInspector;

    // Which element panel the element inspector shows.
    [ObservableProperty] private bool isBackgroundSelected = true;
    [ObservableProperty] private bool isSpriteBoxesSelected;
    [ObservableProperty] private bool isEncounterTableSelected;
    [ObservableProperty] private bool isStatisticsSelected;
    [ObservableProperty] private bool isButtonsSelected;
    [ObservableProperty] private bool isMenuBarSelected;
    [ObservableProperty] private bool isSpriteRowSelected;

    // Selection outlines on the canvas: selected AND not previewing.
    [ObservableProperty] private bool showBackgroundOutline;
    [ObservableProperty] private bool showSpriteBoxesOutline;
    [ObservableProperty] private bool showEncounterTableOutline;
    [ObservableProperty] private bool showStatisticsOutline;
    [ObservableProperty] private bool showButtonsOutline;
    [ObservableProperty] private bool showSpriteRowOutline;
    [ObservableProperty] private bool showMenuBarOutline;

    private bool _syncingSelection;

    private static IReadOnlyList<AppearanceCategory> BuildCategories() =>
    [
        new(AppearanceCategoryId.Background, "Background", Geometry.Parse(IconImage), AppearanceElementKind.Background),
        new(AppearanceCategoryId.MenuBar, "Menu Bar", Geometry.Parse(IconMenu), AppearanceElementKind.MenuBar),
        new(AppearanceCategoryId.PokemonSprites, "Pokémon Sprites", Geometry.Parse(IconPokeball), AppearanceElementKind.SpriteBoxes),
        new(AppearanceCategoryId.SpriteRow, "Sprite Row Panel", Geometry.Parse(IconRow), AppearanceElementKind.SpriteRow),
        new(AppearanceCategoryId.EncounterTables, "Encounter Tables", Geometry.Parse(IconTable), AppearanceElementKind.EncounterTable),
        new(AppearanceCategoryId.Statistics, "Statistics", Geometry.Parse(IconChart), AppearanceElementKind.Statistics),
        new(AppearanceCategoryId.Buttons, "Buttons", Geometry.Parse(IconButton), AppearanceElementKind.Buttons),
        new(AppearanceCategoryId.Borders, "Borders", Geometry.Parse(IconBorders), null),
        new(AppearanceCategoryId.Typography, "Typography", Geometry.Parse(IconType), null),
        new(AppearanceCategoryId.Assets, "Assets", Geometry.Parse(IconFolder), null)
    ];

    // Path data for the sidebar's PathIcons (24x24 grid), so the editor
    // needs no icon font. Simple shapes by design: they read at 16px.
    private const string IconImage = "M21 19V5c0-1.1-.9-2-2-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2zM8.5 13.5l2.5 3.01L14.5 12l4.5 6H5l3.5-4.5z";
    private const string IconPokeball = "M12 2a10 10 0 0 1 9.95 9H15.9a4 4 0 0 0-7.8 0H2.05A10 10 0 0 1 12 2zm0 20a10 10 0 0 1-9.95-9H8.1a4 4 0 0 0 7.8 0h6.05A10 10 0 0 1 12 22zm0-12a2 2 0 1 1 0 4 2 2 0 0 1 0-4z";
    private const string IconTable = "M3 3h18v18H3V3zm2 2v4h14V5H5zm0 6v3h6v-3H5zm8 0v3h6v-3h-6zM5 16v3h6v-3H5zm8 0v3h6v-3h-6z";
    private const string IconChart = "M4 20h16v2H4v-2zm1-2V9h3v9H5zm5.5 0V4h3v14h-3zm5.5 0v-6h3v6h-3z";
    private const string IconButton = "M4 7h16a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2zm0 2v6h16V9H4zm3 2h10v2H7v-2z";
    private const string IconBorders = "M3 3h18v18H3V3zm2 2v14h14V5H5zm2 2h10v10H7V7zm2 2v6h6V9H9z";
    private const string IconType = "M9.5 4h2l6 16h-2.3l-1.6-4.4H7.4L5.8 20H3.5l6-16zm-1.4 9.6h4.8L10.5 6.9 8.1 13.6zM19 4h2v16h-2V4z";
    private const string IconRow = "M3 6a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6zm2 0v12h14V6H5zm2 3h3v6H7V9zm4 0h3v6h-3V9zm4 0h2v6h-2V9z";
    private const string IconMenu = "M3 6h18v2H3V6zm0 5h18v2H3v-2zm0 5h12v2H3v-2z";
    private const string IconFolder = "M10 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z";

    partial void OnSelectedCategoryChanged(AppearanceCategory? value)
    {
        if (value is null)
            return;

        Breadcrumb = $"Appearance  ›  {value.Label}";

        if (value.Element is AppearanceElementKind element)
        {
            ShowElementInspector = true;
            ShowBordersInspector = false;
            ShowTypographyInspector = false;
            ShowAssetsInspector = false;

            if (!_syncingSelection)
                SelectElement(element);

            return;
        }

        ShowElementInspector = false;
        ShowBordersInspector = value.Id == AppearanceCategoryId.Borders;
        ShowTypographyInspector = value.Id == AppearanceCategoryId.Typography;
        ShowAssetsInspector = value.Id == AppearanceCategoryId.Assets;

        // The cross-cutting views still have a canvas selection behind them,
        // so an outline shows what the Borders view is drawing, say.
        RefreshSelectionFlags();
    }

    /// <summary>Called by the canvas when an element is clicked, and by the
    /// navigation when a category that IS an element is chosen. Keeps the two
    /// in step without either re-entering the other.</summary>
    public void SelectElement(AppearanceElementKind element)
    {
        SelectedElement = element;

        (SelectedElementName, SelectedElementDetail) = element switch
        {
            AppearanceElementKind.Background => ("Background", "Window  ·  picture, gradient or flat colour"),
            AppearanceElementKind.SpriteBoxes => ("Pokémon Sprite Boxes", "3 panels  ·  fill, border, caption text, font"),
            AppearanceElementKind.EncounterTable => ("Encounter Table", "Table  ·  fill, header, border, text, font"),
            AppearanceElementKind.Statistics => ("Statistics Panel", "Panel  ·  fill, border, text, font  ·  also every other window"),
            AppearanceElementKind.Buttons => ("Toolbar Buttons", "Buttons  ·  fill, border, text, font"),
            AppearanceElementKind.SpriteRow => ("Sprite Row Panel", "Panel behind the three groups  ·  fill, border, picture, frame"),
            AppearanceElementKind.MenuBar => ("Menu Bar", "File, Game Info and the rest  ·  text, highlight, font"),
            _ => ("Background", string.Empty)
        };

        IsBackgroundSelected = element == AppearanceElementKind.Background;
        IsSpriteBoxesSelected = element == AppearanceElementKind.SpriteBoxes;
        IsEncounterTableSelected = element == AppearanceElementKind.EncounterTable;
        IsStatisticsSelected = element == AppearanceElementKind.Statistics;
        IsButtonsSelected = element == AppearanceElementKind.Buttons;
        IsSpriteRowSelected = element == AppearanceElementKind.SpriteRow;
        IsMenuBarSelected = element == AppearanceElementKind.MenuBar;

        RefreshSelectionFlags();
        DefaultAssetTargetFor(element);

        // A click on the canvas moves the navigation to the matching row,
        // which also puts the element inspector up. Guarded so the row's own
        // handler does not call back in here.
        AppearanceCategory? row = Categories.FirstOrDefault(c => c.Element == element);

        if (row is not null && !ReferenceEquals(SelectedCategory, row))
        {
            _syncingSelection = true;
            try
            {
                SelectedCategory = row;
            }
            finally
            {
                _syncingSelection = false;
            }
        }
    }

    partial void OnIsPreviewModeChanged(bool value) => RefreshSelectionFlags();

    private void RefreshSelectionFlags()
    {
        bool show = !IsPreviewMode;

        ShowBackgroundOutline = show && SelectedElement == AppearanceElementKind.Background;
        ShowSpriteBoxesOutline = show && SelectedElement == AppearanceElementKind.SpriteBoxes;
        ShowEncounterTableOutline = show && SelectedElement == AppearanceElementKind.EncounterTable;
        ShowStatisticsOutline = show && SelectedElement == AppearanceElementKind.Statistics;
        ShowButtonsOutline = show && SelectedElement == AppearanceElementKind.Buttons;
        ShowSpriteRowOutline = show && SelectedElement == AppearanceElementKind.SpriteRow;
        ShowMenuBarOutline = show && SelectedElement == AppearanceElementKind.MenuBar;
    }

    // ------------------------------------------------------------ canvas

    // The canvas draws the main window's regions in the fonts the pickers
    // name, resolved the way ThemeManager resolves them for the real window.
    [ObservableProperty] private FontFamily spriteBoxPreviewFontFamily = ThemeManager.BuildFontFamily("Default");
    [ObservableProperty] private double spriteBoxPreviewFontSize = ThemeManager.DefaultFontSize;
    [ObservableProperty] private FontFamily encountersPreviewFontFamily = ThemeManager.BuildFontFamily("Default");
    [ObservableProperty] private double encountersPreviewFontSize = ThemeManager.DefaultFontSize;
    [ObservableProperty] private FontFamily statsPreviewFontFamily = ThemeManager.BuildFontFamily("Default");
    [ObservableProperty] private double statsPreviewFontSize = ThemeManager.DefaultFontSize;
    [ObservableProperty] private FontFamily buttonPreviewFontFamily = ThemeManager.BuildFontFamily("Default");
    [ObservableProperty] private double buttonPreviewFontSize = ThemeManager.DefaultFontSize;
    [ObservableProperty] private FontFamily menuPreviewFontFamily = ThemeManager.BuildFontFamily("Default");
    [ObservableProperty] private double menuPreviewFontSize = ThemeManager.DefaultFontSize;
    [ObservableProperty] private FontWeight headingFontWeight = FontWeight.Bold;

    // Real sprites for the three boxes, when the sprite index has them; the
    // boxes draw empty otherwise, which is what the main window does too.
    [ObservableProperty] private Bitmap? previewSpriteHunting;
    [ObservableProperty] private Bitmap? previewSpriteCurrent;
    [ObservableProperty] private Bitmap? previewSpritePrevious;

    /// <summary>What the background is right now, in words, for the
    /// Background inspector and the asset library's header.</summary>
    [ObservableProperty] private string backgroundModeText = string.Empty;

    private void RefreshPreviewFonts()
    {
        SpriteBoxPreviewFontFamily = ThemeManager.BuildFontFamily(SpriteBoxFontFamily);
        SpriteBoxPreviewFontSize = ThemeManager.BuildFontSize(SpriteBoxFontSize);
        EncountersPreviewFontFamily = ThemeManager.BuildFontFamily(EncountersFontFamily);
        EncountersPreviewFontSize = ThemeManager.BuildFontSize(EncountersFontSize);
        StatsPreviewFontFamily = ThemeManager.BuildFontFamily(StatsFontFamily);
        StatsPreviewFontSize = ThemeManager.BuildFontSize(StatsFontSize);
        ButtonPreviewFontFamily = ThemeManager.BuildFontFamily(ButtonFontFamily);
        ButtonPreviewFontSize = ThemeManager.BuildFontSize(ButtonFontSize);
        HeadingFontWeight = BoldHeadings ? FontWeight.Bold : FontWeight.Normal;

        // §394. The menu strip: its own font, or Statistics', resolved the
        // way Apply resolves it.
        MenuPreviewFontFamily = ThemeManager.BuildFontFamily(ThemeManager.EffectiveMenuFontFamilyName(_workingSettings));
        MenuPreviewFontSize = ThemeManager.BuildFontSize(ThemeManager.EffectiveMenuFontSizeName(_workingSettings));

        // §349. The preview's own title follows STATISTICS, because Statistics
        // is what the rest of the app follows. Scaled up a bit, the same
        // relative bump as the fixed FontSize="20" it replaced at the Default
        // size (14 * ~1.43 ≈ 20).
        PreviewFontFamily = StatsPreviewFontFamily;
        PreviewFontSize = StatsPreviewFontSize * 1.43;
    }

    private void LoadPreviewSprites()
    {
        try
        {
            PreviewSpriteHunting = PokemonSpriteService.GetSprite("Pikachu");
            PreviewSpriteCurrent = PokemonSpriteService.GetSprite("Eevee");
            PreviewSpritePrevious = PokemonSpriteService.GetSprite("Gengar");
        }
        catch
        {
            // The sprite index is a convenience for the canvas, not a
            // requirement of the editor.
        }
    }

    /// <summary>The picture the canvas is showing, if the background is a
    /// picture: the one chosen this session before Apply, else the applied
    /// one, else the built-in. Null for a gradient or a flat colour.</summary>
    private string? CurrentPicturePath()
    {
        if (_workingSettings.UseCustomGradient)
            return null;

        if (_workingSettings.UseCustomBackground)
        {
            if (!string.IsNullOrWhiteSpace(_pendingCustomImagePath))
                return _pendingCustomImagePath;

            return string.IsNullOrWhiteSpace(_workingSettings.CustomBackgroundPath)
                ? null
                : _workingSettings.CustomBackgroundPath;
        }

        return ThemeManager.GetBackgroundPath(_workingSettings.BackgroundId);
    }

    private void RefreshBackgroundModeText()
    {
        if (_workingSettings.UseCustomGradient)
        {
            int stops = _workingSettings.CustomGradientColorArgbs.Count;
            BackgroundModeText = $"Gradient  ·  {stops} colours  ·  {_workingSettings.CustomGradientDirection}";
            return;
        }

        if (_workingSettings.UseCustomBackground)
        {
            string? picture = CurrentPicturePath();

            BackgroundModeText = string.IsNullOrWhiteSpace(picture)
                ? "Flat colour"
                : $"Picture  ·  {DescribePicture(picture)}";
            return;
        }

        BackgroundModeText = $"Built-in  ·  {_workingSettings.BackgroundId}";
    }

    // ------------------------------------------------------------ history

    private readonly AppearanceHistory _history = new();
    private bool _suspendHistory;
    private string _lastSnapshot = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool canUndo;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    private bool canRedo;

    /// <summary>The working appearance as one string: the settings as JSON,
    /// then the picture chosen but not yet applied. That pair is everything
    /// the editor can change.</summary>
    private string Snapshot() =>
        JsonSerializer.Serialize(_workingSettings) + "\n" + (_pendingCustomImagePath ?? string.Empty);

    /// <summary>Every edit ends here. Compares the working appearance with
    /// the last one seen and, if it moved, records the previous state under
    /// <paramref name="key"/> - see AppearanceHistory for why the key.</summary>
    private void Track(string key)
    {
        if (_suspendHistory)
            return;

        string now = Snapshot();

        if (string.Equals(now, _lastSnapshot, StringComparison.Ordinal))
            return;

        _history.Record(key, _lastSnapshot);
        _lastSnapshot = now;
        RefreshHistoryFlags();
    }

    private void Restore(string snapshot)
    {
        int split = snapshot.LastIndexOf('\n');
        string json = split < 0 ? snapshot : snapshot[..split];
        string pending = split < 0 ? string.Empty : snapshot[(split + 1)..];

        AppearanceSettings? settings = null;

        try
        {
            settings = JsonSerializer.Deserialize<AppearanceSettings>(json);
        }
        catch
        {
            // A snapshot this class wrote itself should always parse; if it
            // does not, the step is skipped rather than the window lost.
        }

        if (settings is null)
            return;

        LoadFrom(settings, string.IsNullOrEmpty(pending) ? null : pending, null);
        _lastSnapshot = snapshot;
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        string? before = _history.Undo(_lastSnapshot);

        if (before is null)
            return;

        Restore(before);
        RefreshHistoryFlags();
        StatusText = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        string? next = _history.Redo(_lastSnapshot);

        if (next is null)
            return;

        Restore(next);
        RefreshHistoryFlags();
        StatusText = string.Empty;
    }

    private void RefreshHistoryFlags()
    {
        CanUndo = _history.CanUndo;
        CanRedo = _history.CanRedo;
    }

    // ------------------------------------------------------------ assets

    /// <summary>The library's cards, filtered by <see cref="AssetFilter"/>
    /// and <see cref="AssetSearchText"/>.</summary>
    public ObservableCollection<AppearanceAssetItem> Assets { get; } = new();

    public IReadOnlyList<string> AssetFilters { get; } = ["All backgrounds", "Custom", "Built-in"];

    [ObservableProperty] private string assetFilter = "All backgrounds";

    [ObservableProperty] private string assetSearchText = string.Empty;

    [ObservableProperty] private string assetCountText = string.Empty;

    [ObservableProperty] private bool hasNoAssets;

    public string AssetsFolder => AppearanceAssetLibrary.LibraryFolder;

    private readonly Dictionary<string, (long Length, DateTime Written, Bitmap? Thumbnail)> _thumbnails =
        new(StringComparer.OrdinalIgnoreCase);

    partial void OnAssetFilterChanged(string value) => ReloadAssets();

    partial void OnAssetSearchTextChanged(string value) => ReloadAssets();

    /// <summary>Re-reads the library folder and rebuilds the cards.
    /// Thumbnails are cached by path, size and write time, so a filter or a
    /// search does not decode every picture again.</summary>
    public void ReloadAssets()
    {
        Assets.Clear();

        string search = AssetSearchText.Trim();

        foreach (AppearanceAsset asset in AppearanceAssetLibrary.List())
        {
            if (AssetFilter == "Custom" && asset.IsBuiltIn)
                continue;

            if (AssetFilter == "Built-in" && !asset.IsBuiltIn)
                continue;

            if (search.Length > 0 && !asset.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;

            Assets.Add(new AppearanceAssetItem(asset, ThumbnailFor(asset.Path), DimensionsOf(asset.Path)));
        }

        HasNoAssets = Assets.Count == 0;
        AssetCountText = Assets.Count == 1 ? "1 picture" : $"{Assets.Count} pictures";

        RefreshAssetUsage();
    }

    private Bitmap? ThumbnailFor(string path)
    {
        try
        {
            var info = new FileInfo(path);

            if (_thumbnails.TryGetValue(path, out var cached) &&
                cached.Length == info.Length &&
                cached.Written == info.LastWriteTimeUtc)
            {
                return cached.Thumbnail;
            }

            Bitmap? thumbnail = AppearanceAssetLibrary.LoadThumbnail(path);
            _thumbnails[path] = (info.Length, info.LastWriteTimeUtc, thumbnail);
            return thumbnail;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Marks the card whose picture the current target is showing,
    /// and captions every card with the targets using it - the window, the
    /// statistics panel, the table. An applied copy lives under another
    /// name (custom-background-clientN, panel-stats-clientN), so a custom
    /// card is also matched by content when no path matches - which is how
    /// the card stays lit after a restart.</summary>
    private void RefreshAssetUsage()
    {
        if (Assets.Count == 0)
            return;

        var usedBy = new Dictionary<AppearanceAssetItem, List<string>>();
        AppearanceAssetItem? current = null;

        foreach (AssetTargetOption option in AssetTargetOptions)
        {
            AppearanceAssetItem? match = MatchAsset(PicturePathFor(option.Kind));

            if (match is null)
                continue;

            if (!usedBy.TryGetValue(match, out List<string>? labels))
                usedBy[match] = labels = new List<string>();

            labels.Add(option.Short);

            if (option.Kind == CurrentTarget)
                current = match;
        }

        foreach (AppearanceAssetItem item in Assets)
        {
            item.IsInUse = ReferenceEquals(item, current);
            string kind = item.Dimensions.Length > 0 ? $"{item.Kind}  ·  {item.Dimensions}" : item.Kind;

            // §390: the targets on their own, for Remove; the caption is
            // for reading.
            item.UsedBy = usedBy.TryGetValue(item, out List<string>? labels) ? string.Join(", ", labels) : string.Empty;
            item.Caption = item.UsedBy.Length > 0 ? $"{kind}  ·  {item.UsedBy}" : kind;
        }
    }

    private AppearanceAssetItem? MatchAsset(string? picture)
    {
        if (string.IsNullOrWhiteSpace(picture))
            return null;

        string full = Path.GetFullPath(picture);

        AppearanceAssetItem? byPath = Assets.FirstOrDefault(a =>
            string.Equals(Path.GetFullPath(a.Asset.Path), full, StringComparison.OrdinalIgnoreCase));

        if (byPath is not null)
            return byPath;

        if (File.Exists(full) && !AppearanceAssetLibrary.IsInsideLibrary(full))
            return Assets.FirstOrDefault(a => !a.IsBuiltIn && AppearanceAssetLibrary.SameContent(a.Asset.Path, full));

        return null;
    }

    /// <summary>Called by AppearanceWindow.axaml.cs after the picker returns.
    /// Every file is copied into the library; the LAST one becomes the
    /// background, the way choosing a picture always has.</summary>
    public void ImportAssets(IReadOnlyList<string> paths)
    {
        SaveError = null;
        AppearanceAsset? last = null;
        int added = 0;

        foreach (string path in paths)
        {
            try
            {
                last = AppearanceAssetLibrary.Import(path);
                added++;
            }
            catch (Exception ex)
            {
                SaveError = $"{Path.GetFileName(path)} could not be added.\n\n{ex.Message}";
            }
        }

        ReloadAssets();

        if (last is null)
            return;

        string where = ApplyPictureToTarget(last.Path);
        StatusText = added == 1
            ? $"{last.Name} added to the library and used {where}. Press Apply to keep it."
            : $"{added} pictures added to the library; {last.Name} is used {where}. Press Apply to keep it.";
    }

    /// <summary>Puts a picture where the picker points and says where that
    /// was, for the status line.</summary>
    private string ApplyPictureToTarget(string path)
    {
        switch (CurrentTarget)
        {
            case AssetTargetKind.StatsPicture:
                SetPanelPicture(AppearanceElementKind.Statistics, path);
                return "behind the statistics panel";
            case AssetTargetKind.TablePicture:
                SetPanelPicture(AppearanceElementKind.EncounterTable, path);
                return "behind the encounter table";
            case AssetTargetKind.SpritesPicture:
                SetPanelPicture(AppearanceElementKind.SpriteBoxes, path);
                return "behind the sprite boxes";
            case AssetTargetKind.StatsFrame:
                SetFramePicture(AppearanceElementKind.Statistics, path);
                return "as the statistics panel's frame";
            case AssetTargetKind.TableFrame:
                SetFramePicture(AppearanceElementKind.EncounterTable, path);
                return "as the encounter table's frame";
            case AssetTargetKind.SpritesFrame:
                SetFramePicture(AppearanceElementKind.SpriteBoxes, path);
                return "as the sprite boxes' frame";
            case AssetTargetKind.SpriteRowPicture:
                SetPanelPicture(AppearanceElementKind.SpriteRow, path);
                return "behind the whole sprite row";
            case AssetTargetKind.SpriteRowFrame:
                SetFramePicture(AppearanceElementKind.SpriteRow, path);
                return "as the sprite row panel's frame";
            default:
                SetCustomBackground(path);
                return "as the window background";
        }
    }

    [RelayCommand]
    private void UseAsset(AppearanceAssetItem? item)
    {
        if (item is null)
            return;

        SaveError = null;

        // The built-in picture is the window's built-in MODE, not a file
        // to copy - unless a panel is the target, where it is just a picture.
        if (item.IsBuiltIn && CurrentTarget == AssetTargetKind.Window)
        {
            ClearBackground();
            StatusText = "Back to the built-in background. Press Apply to keep it.";
            return;
        }

        if (!File.Exists(item.Asset.Path))
        {
            SaveError = $"{item.Name} is no longer in the library folder.";
            ReloadAssets();
            return;
        }

        string where = ApplyPictureToTarget(item.Asset.Path);
        StatusText = $"{item.Name} is used {where}. Press Apply to keep it.";
    }

    [RelayCommand]
    private void RemoveAsset(AppearanceAssetItem? item)
    {
        if (item is null || !item.CanRemove)
            return;

        SaveError = null;

        // §390: read off the targets, not the caption - the caption has
        // carried the size since §386, so "caption differs from kind" was
        // true of every picture with a readable header, and nothing could
        // be removed.
        if (item.IsInUse || item.UsedBy.Length > 0)
        {
            string where = item.UsedBy.Length > 0 ? item.UsedBy : "the selected target";
            StatusText = $"{item.Name} is in use ({where}) - choose another picture there first.";
            return;
        }

        try
        {
            if (AppearanceAssetLibrary.Remove(item.Asset))
                StatusText = $"{item.Name} removed from the library. The background you applied is not affected.";
        }
        catch (Exception ex)
        {
            SaveError = $"{item.Name} could not be removed.\n\n{ex.Message}";
        }

        ReloadAssets();
    }

    [RelayCommand]
    private void RefreshAssets()
    {
        ReloadAssets();
        StatusText = string.Empty;
    }
}