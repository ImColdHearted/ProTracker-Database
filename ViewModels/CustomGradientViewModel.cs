using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// One color swatch inside CustomGradientViewModel.Colors. A plain
/// observable holder rather than a bare Color, so each ColorPicker's
/// Mode=TwoWay binding has somewhere to write back into, and so editing one
/// swatch can call straight back into the parent dialog's live preview
/// (onChanged) without the parent needing to subscribe/unsubscribe
/// PropertyChanged handlers as swatches are added and removed. Label is
/// assigned once at creation ("Color 1", "Color 2", ...) and never
/// recomputed - Add only ever appends and Remove only ever drops the last
/// slot (see CustomGradientViewModel), so an existing slot's position, and
/// therefore its label, never changes underneath it.
/// </summary>
public sealed partial class GradientColorSlot : ObservableObject
{
    public string Label { get; }

    private readonly Action? _onChanged;

    [ObservableProperty] private Color color;

    public GradientColorSlot(string label, Color color, Action? onChanged)
    {
        Label = label;
        this.color = color;
        _onChanged = onChanged;
    }

    partial void OnColorChanged(Color value) => _onChanged?.Invoke();
}

/// <summary>
/// Backs the "Create Gradient" dialog opened from AppearanceWindow (see
/// AppearanceWindow.axaml.cs's CreateGradientButton_Click and
/// AppearanceViewModel.SetCustomGradient). Seeded from whatever gradient (or
/// sensible defaults) is already stored on AppearanceSettings, so reopening
/// this dialog picks up where the user left off rather than resetting to
/// scratch - same idea as SwapPokemonViewModel seeding from the target it's
/// replacing.
///
/// Holds 2-4 colors (AddColor/RemoveColor only ever act on the last slot, so
/// existing slots never get renumbered or reshuffled) plus a direction, and
/// combines them into evenly-spaced gradient stops - see ThemeManager.
/// BuildGradientBrush. Unlike AppearanceWindow itself (one big form with a
/// single Save button at the bottom), this is a small, self-contained
/// picker in the same shape as PokemonSelectorWindow/SwapPokemonWindow: it
/// raises Applied when the user hits Apply, the View closes itself with a
/// confirmed dialog result, and the caller reads Colors/SelectedDirection
/// back off this instance. Nothing here touches
/// AppearanceSettingsRepository directly - the chosen gradient only
/// actually gets persisted when the user later clicks AppearanceWindow's
/// own Save.
/// </summary>
public sealed partial class CustomGradientViewModel : ViewModelBase
{
    public const int MinColors = 2;
    public const int MaxColors = 4;

    public IReadOnlyList<string> DirectionOptions { get; } =
        ThemeManager.GradientDirectionCatalog.Keys.ToList();

    public ObservableCollection<GradientColorSlot> Colors { get; } = new();

    [ObservableProperty] private string selectedDirection = "Top to Bottom";
    [ObservableProperty] private IBrush previewBrush = Brushes.Black;

    /// <summary>Raised when Apply is pressed - the View closes itself with a
    /// confirmed result, same shape as PokemonSelectorViewModel.Confirmed.</summary>
    public event Action? Applied;

    public CustomGradientViewModel(IReadOnlyList<Color> initialColors, string initialDirection)
    {
        IReadOnlyList<Color> seedColors = initialColors is { Count: >= MinColors }
            ? initialColors
            : new[] { Color.FromRgb(10, 15, 61), Color.FromRgb(89, 87, 87) };

        int seedCount = Math.Min(seedColors.Count, MaxColors);
        for (int i = 0; i < seedCount; i++)
        {
            Colors.Add(new GradientColorSlot($"Color {i + 1}", seedColors[i], RefreshPreview));
        }

        SelectedDirection = DirectionOptions.Contains(initialDirection)
            ? initialDirection
            : DirectionOptions[0];

        RefreshPreview();
    }

    partial void OnSelectedDirectionChanged(string value) => RefreshPreview();

    private bool CanAddColor() => Colors.Count < MaxColors;

    [RelayCommand(CanExecute = nameof(CanAddColor))]
    private void AddColor()
    {
        Color seedColor = Colors.Count > 0
            ? Colors[Colors.Count - 1].Color
            : Color.FromRgb(89, 87, 87);

        Colors.Add(new GradientColorSlot($"Color {Colors.Count + 1}", seedColor, RefreshPreview));

        RefreshPreview();
        AddColorCommand.NotifyCanExecuteChanged();
        RemoveColorCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemoveColor() => Colors.Count > MinColors;

    [RelayCommand(CanExecute = nameof(CanRemoveColor))]
    private void RemoveColor()
    {
        Colors.RemoveAt(Colors.Count - 1);

        RefreshPreview();
        AddColorCommand.NotifyCanExecuteChanged();
        RemoveColorCommand.NotifyCanExecuteChanged();
    }

    private void RefreshPreview()
    {
        PreviewBrush = ThemeManager.BuildGradientBrush(
            Colors.Select(slot => slot.Color).ToList(),
            SelectedDirection);
    }

    [RelayCommand]
    private void Apply()
    {
        Applied?.Invoke();
    }
}
