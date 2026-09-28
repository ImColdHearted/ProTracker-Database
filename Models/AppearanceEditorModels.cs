using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Foot_Tracker.Models
{
    /// <summary>§381. The five things on the main window a theme can paint.
    /// One per region of AppearanceSettings (§349's four sections and the
    /// window background), so selecting one on the editor's canvas selects a
    /// set of EXISTING settings rather than anything new.</summary>
    public enum AppearanceElementKind
    {
        Background,
        SpriteBoxes,
        EncounterTable,
        Statistics,
        Buttons,
        SpriteRow,
        MenuBar
    }

    /// <summary>§381. What the editor's left-hand navigation lists. The first
    /// five select an element on the canvas; the last three switch the
    /// inspector to a cross-cutting view of settings that already exist -
    /// every border colour together, every font together, the asset
    /// library - without inventing a second copy of them.</summary>
    public enum AppearanceCategoryId
    {
        Background,
        PokemonSprites,
        EncounterTables,
        Statistics,
        Buttons,
        Borders,
        Typography,
        Assets,
        SpriteRow,
        MenuBar
    }

    /// <summary>§381. One row of the editor's navigation. The icon is a
    /// parsed path for a PathIcon, so the sidebar needs no icon font or
    /// image files - and parsed once here rather than on every bind.</summary>
    public sealed record AppearanceCategory(
        AppearanceCategoryId Id,
        string Label,
        Geometry Icon,
        AppearanceElementKind? Element);

    /// <summary>§384. Where a picture from the library can go: the window
    /// background, behind one of the three panels (§382/§383), or round one
    /// of them as a frame (§384). The editor's library has a picker for it,
    /// defaulted from the element selected on the canvas.</summary>
    public enum AssetTargetKind
    {
        Window,
        StatsPicture,
        TablePicture,
        SpritesPicture,
        StatsFrame,
        TableFrame,
        SpritesFrame,
        SpriteRowPicture,
        SpriteRowFrame
    }

    /// <summary>§384. One entry of that picker: the target, the words in the
    /// picker, the short word on a card's caption.</summary>
    public sealed record AssetTargetOption(AssetTargetKind Kind, string Label, string Short);

    /// <summary>§386. What the main window measures right now, for the
    /// editor to say beside each Picture… button. Logical sizes plus the
    /// render scaling, so the editor can quote device pixels.</summary>
    public sealed record AppearancePanelSizes(
        Size Window,
        Size Stats,
        Size Table,
        Size SpriteBox,
        double TargetSpriteBox,
        Size SpriteRow,
        double Scaling);

    /// <summary>§381. A picture the background can use: either the one
    /// built-in background (§104: Slate) or a file the user imported into the
    /// asset library (see AppearanceAssetLibrary). The path is on this disk;
    /// a theme FILE never carries it, as before.</summary>
    public sealed record AppearanceAsset(string Name, string Path, bool IsBuiltIn);

    /// <summary>§381. An asset as the library shows it: with its thumbnail,
    /// and whether it is the picture the canvas currently shows.</summary>
    public sealed partial class AppearanceAssetItem : ObservableObject
    {
        public AppearanceAssetItem(AppearanceAsset asset, Bitmap? thumbnail, string dimensions = "")
        {
            Asset = asset;
            Thumbnail = thumbnail;
            Dimensions = dimensions;
            caption = Kind;
        }

        /// <summary>§386. "1920 × 1080", or empty when the file could not be read.</summary>
        public string Dimensions { get; }

        public AppearanceAsset Asset { get; }

        public Bitmap? Thumbnail { get; }

        public string Name => Asset.Name;

        public bool IsBuiltIn => Asset.IsBuiltIn;

        public bool CanRemove => !Asset.IsBuiltIn;

        public string Kind => Asset.IsBuiltIn ? "Built-in" : "Custom";

        [ObservableProperty] private bool isInUse;

        /// <summary>§382. The kind, and the targets using the picture -
        /// "Custom  ·  Window, Stats" - set by the view model. §386 put the
        /// size in it too, which is why it is no longer read back to decide
        /// anything: see UsedBy.</summary>
        [ObservableProperty] private string caption = string.Empty;

        /// <summary>§390. The targets showing this picture - "Window, Stats"
        /// - or empty when none does. Set by the view model beside Caption;
        /// what Remove reads.</summary>
        public string UsedBy { get; set; } = string.Empty;
    }
}
