using Avalonia.Media.Imaging;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

public sealed record TargetDisplayItem(string Name, Bitmap? Sprite)
{
    /// <summary>Backs a small type-icon row next to this target's name.</summary>
    public IReadOnlyList<string> Types => PokemonSpriteService.GetTypes(Name);
}