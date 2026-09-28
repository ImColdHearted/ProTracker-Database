using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Foot_Tracker.Models;
using Foot_Tracker.Tracking;
using Serilog;
using SkiaSharp;

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
        // ==============================================================
        // CUSTOM FONTS - see Assets/Fonts/README.txt for the full walkthrough.
        //
        // To add a new bundled font:
        //   1. Drop the .ttf/.otf file(s) in Assets/Fonts/.
        //   2. Add one line below: ["Dropdown Display Name"] = "Font's Actual Internal Name".
        //   3. Rebuild - it shows up in Appearance > Font automatically.
        //
        // The dictionary key is what appears in the Appearance window's Font
        // dropdown. The value must match the font file's own internal family name
        // (often different from the filename) - check by double-clicking the
        // .ttf/.otf file to preview it; the preview window's title bar shows the
        // real name.
        //
        // NOTE: if the font is only a single weight (no separate Bold file), that's
        // fine - BuildFontFamily() below automatically adds Inter as a fallback so
        // Bold-styled text (headers/labels throughout the app) renders in Inter
        // instead of crashing. No extra step needed for that here.
        // ==============================================================
        public static readonly Dictionary<string, string> CustomFontCatalog = new()
        {
            ["American Slasher"] = "American Slasher",
            ["Aquire"] = "Aquire",
            ["Champions Stencil"] = "Champions Stencil",
            ["Lemondrop"] = "Lemondrop",
            ["Minecraft"] = "Minecraft",
            ["Nearo"] = "Nearo",
            ["Neura"] = "Neura",
            ["Sanger Memo"] = "Sanger Memo",
            ["Skylane Horizon"] = "Skylane Horizon",
            // §380. Google Fonts, SIL Open Font License (Assets/Fonts/OFL.txt) -
            // one weight, so pair it with Bold headings off.
            ["Griffy"] = "Griffy"

            // ["Montserrat"] = "Montserrat",
            // ["Press Start 2P"] = "Press Start 2P",
        };

        // Must exactly match the compiled assembly name (defaults to the .csproj
        // filename minus extension, unless overridden by an explicit <AssemblyName>
        // property) - if the project gets renamed again later, update this to match,
        // or every avares:// custom-font lookup silently falls back to Inter with
        // no error at all (this is exactly what happened after the Pro_Tracker rename).
        private const string CustomFontAssemblyName = "ProTrackerDatabase.Avalonia";

        public static AppearanceSettings Current { get; private set; } =
            AppearanceSettingsRepository.Load();

        public const string BackgroundBrushKey = "ThemeBackgroundBrush";
        public const string TextBrushKey = "ThemeTextBrush";
        public const string BorderBrushKey = "ThemeBorderBrush";
        public const string FontFamilyKey = "ThemeFontFamily";
        public const string FontSizeKey = "ThemeFontSize";
        public const string SpriteBoxBackgroundBrushKey = "ThemeSpriteBoxBackgroundBrush";
        public const string ButtonBrushKey = "ThemeButtonBrush";
        public const string StatsBackgroundBrushKey = "ThemeStatsBackgroundBrush";
        public const string EncountersBackgroundBrushKey = "ThemeEncountersBackgroundBrush";

        // §349. Three regions of the main window that now carry their own
        // text colour, border colour and font.
        //
        // There is deliberately no ThemeStatsTextBrush pair here. Statistics
        // IS the global: TextBrushKey, BorderBrushKey, FontFamilyKey and
        // FontSizeKey are filled from the Stats fields below, so the stats
        // panel and every other window in the app move together - the same
        // rule §188 set for the panel background, extended to the rest.
        //
        // That is also why this change touched no other window. ThemeTextBrush
        // is bound 548 times across 54 files; giving Statistics its own key
        // would have meant deciding, for each of those, which section it
        // belonged to - and 39 of those files have no sprites, no encounter
        // table and no toolbar to belong to.
        public const string SpriteBoxTextBrushKey = "ThemeSpriteBoxTextBrush";
        public const string SpriteBoxBorderBrushKey = "ThemeSpriteBoxBorderBrush";
        public const string SpriteBoxFontFamilyKey = "ThemeSpriteBoxFontFamily";
        public const string SpriteBoxFontSizeKey = "ThemeSpriteBoxFontSize";

        public const string EncountersTextBrushKey = "ThemeEncountersTextBrush";
        public const string EncountersBorderBrushKey = "ThemeEncountersBorderBrush";
        public const string EncountersFontFamilyKey = "ThemeEncountersFontFamily";
        public const string EncountersFontSizeKey = "ThemeEncountersFontSize";

        public const string ButtonTextBrushKey = "ThemeButtonTextBrush";
        public const string ButtonBorderBrushKey = "ThemeButtonBorderBrush";
        public const string ButtonFontFamilyKey = "ThemeButtonFontFamily";
        public const string ButtonFontSizeKey = "ThemeButtonFontSize";

        // §380. FontWeight.Bold or FontWeight.Normal, from BoldHeadings. The
        // headings on MainWindow and CompactWindow take their weight from
        // this instead of writing Bold, so a theme can turn it off.
        public const string HeadingFontWeightKey = "ThemeHeadingFontWeight";

        // §384. Border width and corner radius per section, as the Thickness
        // and CornerRadius the windows bind to; the frame pictures (an
        // IImage, or null for none) and their corner size; and the layout
        // values a frame changes - the padding that keeps the stats panel's
        // text out from under it, the margin that keeps the table inside
        // it, the margin that shrinks a sprite away from it.
        public const string SpriteBoxBorderThicknessKey = "ThemeSpriteBoxBorderThickness";
        public const string SpriteBoxCornerRadiusKey = "ThemeSpriteBoxCornerRadius";
        public const string EncountersBorderThicknessKey = "ThemeEncountersBorderThickness";
        public const string EncountersHeaderLineThicknessKey = "ThemeEncountersHeaderLineThickness";
        public const string EncountersCornerRadiusKey = "ThemeEncountersCornerRadius";
        public const string StatsBorderThicknessKey = "ThemeStatsBorderThickness";
        public const string StatsCornerRadiusKey = "ThemeStatsCornerRadius";
        public const string ButtonBorderThicknessKey = "ThemeButtonBorderThickness";
        public const string ButtonCornerRadiusKey = "ThemeButtonCornerRadius";

        public const string StatsFrameImageKey = "ThemeStatsFrameImage";
        public const string StatsFrameInsetKey = "ThemeStatsFrameInset";
        public const string StatsPanelPaddingKey = "ThemeStatsPanelPadding";
        public const string TableFrameImageKey = "ThemeTableFrameImage";
        public const string TableFrameInsetKey = "ThemeTableFrameInset";
        public const string TableFrameMarginKey = "ThemeTableFrameMargin";
        public const string SpriteBoxFrameImageKey = "ThemeSpriteBoxFrameImage";
        public const string SpriteBoxFrameInsetKey = "ThemeSpriteBoxFrameInset";
        public const string SpriteBoxFrameImageMarginKey = "ThemeSpriteBoxFrameImageMargin";

        // §393. How far each frame reaches past its panel, as the negative
        // Margin the NineSliceFrame wears (a frame is a sibling of its
        // panel in a Panel, so a negative margin grows it outward over the
        // window around, and nothing in that tree clips).
        public const string StatsFrameOverhangKey = "ThemeStatsFrameOverhang";
        public const string TableFrameOverhangKey = "ThemeTableFrameOverhang";
        public const string SpriteBoxFrameOverhangKey = "ThemeSpriteBoxFrameOverhang";
        public const string SpriteRowFrameOverhangKey = "ThemeSpriteRowFrameOverhang";

        // §393. The menu bar's text and its highlight - see
        // EffectiveMenuTextColor for what "automatic" resolves to.
        public const string MenuTextBrushKey = "ThemeMenuTextBrush";
        public const string MenuHighlightBrushKey = "ThemeMenuHighlightBrush";

        // §394. The menu bar's font - Statistics' unless its own is named.
        public const string MenuFontFamilyKey = "ThemeMenuFontFamily";
        public const string MenuFontSizeKey = "ThemeMenuFontSize";

        // §387. The sprite row panel. Thickness and padding are derived:
        // nothing showing means no line and no inset, so a window that
        // never set it lays out as it did before the panel existed.
        public const string SpriteRowBackgroundBrushKey = "ThemeSpriteRowBackgroundBrush";
        public const string SpriteRowBorderBrushKey = "ThemeSpriteRowBorderBrush";
        public const string SpriteRowBorderThicknessKey = "ThemeSpriteRowBorderThickness";
        public const string SpriteRowCornerRadiusKey = "ThemeSpriteRowCornerRadius";
        public const string SpriteRowPaddingKey = "ThemeSpriteRowPadding";
        public const string SpriteRowFrameImageKey = "ThemeSpriteRowFrameImage";
        public const string SpriteRowFrameInsetKey = "ThemeSpriteRowFrameInset";

        /// <summary>§387. The inset the sprite row keeps from its panel's edge
        /// once there is a panel to see.</summary>
        internal const double SpriteRowPadding = 8;

        /// <summary>§384. The bounds the editor's sliders and ImportTheme
        /// hold the numbers to. A border wider than 8 is a bar, corners
        /// beyond 40 make a pill of a panel, and a frame corner past 160px is
        /// bigger than the sprite boxes it goes round.</summary>
        public const double MaxBorderWidth = 8;
        public const double MaxCornerRadius = 40;
        public const double MinFrameInset = 4;
        public const double MaxFrameInset = 160;

        /// <summary>§393. A frame may reach this far past its panel. Beyond
        /// 64 it is over the next panel's content, not round its own.</summary>
        public const double MaxFrameOverhang = 64;

        /// <summary>§384. The stats panel's padding with no frame - what
        /// MainWindow set directly before the value became a resource.</summary>
        internal const double StatsPanelPadding = 10;

        public static double ClampBorderWidth(double value) =>
            double.IsFinite(value) ? Math.Clamp(Math.Round(value * 2) / 2, 0, MaxBorderWidth) : 1;

        public static double ClampCornerRadius(double value) =>
            double.IsFinite(value) ? Math.Clamp(Math.Round(value), 0, MaxCornerRadius) : 0;

        public static double ClampFrameInset(double value) =>
            double.IsFinite(value) ? Math.Clamp(Math.Round(value), MinFrameInset, MaxFrameInset) : 24;

        public static double ClampFrameOverhang(double value) =>
            double.IsFinite(value) ? Math.Clamp(Math.Round(value), 0, MaxFrameOverhang) : 0;

        /// <summary>§393. The frame's negative margin for an overhang.</summary>
        public static Thickness FrameOverhangMargin(double overhang) =>
            new(-ClampFrameOverhang(overhang));

        // ============================================================
        // §365. THE MAIN TOOLBAR'S BUTTON SIZES.
        // ============================================================
        //
        // The row across the top of MainWindow - Set Target, Start, Stop,
        // Reset, the pin, Remove Stats, Set Screen, and World Quest mode's
        // two - had its widths and heights written into the XAML as fixed
        // numbers, tuned by hand until the row looked right. §349 had already
        // given every Button in the app its own FontSize from
        // ButtonFontSizeKey above, so those numbers were tuned at one font
        // size and stayed put at every other: pick 15px in Appearance and the
        // labels grow inside buttons that do not, until they are trimmed.
        //
        // The fix keeps the hand-tuned numbers and makes them a FUNCTION of
        // the chosen Buttons font size instead of a constant. They are
        // published as resources, the same way PanelBorderThicknessKey and
        // PanelMarginKey below already publish layout values, so the toolbar
        // resizes the moment Appearance applies - no binding, no event, no
        // restart, and nothing in MainWindow.axaml to keep in step but the
        // resource names.
        //
        // They are used as MinWidth/MinHeight in the XAML, not Width/Height.
        // That is §355's rule and it matters more here, not less: a scaled
        // number still cannot know how wide a FONT FAMILY draws a label, so a
        // floor lets a wide family push a button out instead of clipping its
        // text, while a button whose label fits sits at exactly the number
        // below.
        //
        // TO RETUNE THE ROW, EDIT ToolbarButtonSizes - not the XAML.
        public const string ToolbarButtonHeightKey = "ThemeToolbarButtonHeight";
        public const string ToolbarSetTargetWidthKey = "ThemeToolbarSetTargetWidth";
        public const string ToolbarStartWidthKey = "ThemeToolbarStartWidth";
        public const string ToolbarStopWidthKey = "ThemeToolbarStopWidth";
        public const string ToolbarResetWidthKey = "ThemeToolbarResetWidth";
        public const string ToolbarRemoveStatsWidthKey = "ThemeToolbarRemoveStatsWidth";
        public const string ToolbarSetScreenWidthKey = "ThemeToolbarSetScreenWidth";
        public const string ToolbarAutoDetectWidthKey = "ThemeToolbarAutoDetectWidth";
        public const string ToolbarSubmitScreenshotWidthKey = "ThemeToolbarSubmitScreenshotWidth";

        /// <summary>§365. The Buttons font size the sizes below were drawn
        /// at. Every number in ToolbarButtonSizes is multiplied by
        /// (chosen size / this), so at this size the row renders at exactly
        /// the numbers written there and nothing about the layout moves.
        ///
        /// 12 rather than DefaultFontSize's 11 because the row was tuned with
        /// Buttons set to 12px, which is what the user who tuned it had on
        /// screen. Changing this number rescales the whole row at once.</summary>
        public const double ToolbarBaseFontSize = 12;

        /// <summary>§365. The toolbar's sizes at ToolbarBaseFontSize. This is
        /// the one place they live; MainWindow.axaml names the resource and
        /// nothing else.</summary>
        private static readonly (string Key, double AtBaseFont)[] ToolbarButtonSizes =
        {
            (ToolbarButtonHeightKey, 27),
            (ToolbarSetTargetWidthKey, 80),
            (ToolbarStartWidthKey, 60),
            (ToolbarStopWidthKey, 55),
            (ToolbarResetWidthKey, 70),
            (ToolbarRemoveStatsWidthKey, 100),
            (ToolbarSetScreenWidthKey, 80),
            // World Quest mode's two, in the slot the four hunt controls
            // vacate (§264). Auto Detect was already a floor - §355 gave it
            // one because its label changes by a character - and its 150 now
            // scales like the rest. Submit Screenshot was a fixed 170 and is
            // the longest label in the row, so it was the next one due to
            // clip.
            (ToolbarAutoDetectWidthKey, 150),
            (ToolbarSubmitScreenshotWidthKey, 170),
        };

        /// <summary>§208. The three states a button has besides its resting
        /// one, derived from the user's Button Color rather than settings of
        /// their own.
        ///
        /// THE BUG THIS EXISTS FOR. App.axaml sets Background on a blanket
        /// Button selector so the Button Color reaches every button in the
        /// app. FluentTheme's own hover, pressed and disabled visuals are
        /// setters on PART_ContentPresenter inside the button's template, and
        /// they set Background, BorderBrush AND Foreground - so on a themed
        /// button the pointer either changed nothing or swapped in Fluent's
        /// stock grey and stock text colour, neither of which has anything to
        /// do with the colour the user picked. §103 hit the same wall with
        /// the Always on Top toggle and worked around it with a doubled
        /// border rather than a fill.
        ///
        /// Deriving them means there is nothing new to set and nothing that
        /// can drift: pick any Button Color and its three states follow.
        /// Hover and pressed move TOWARD the text colour (14% and 30%), which
        /// is direction-agnostic - it lightens a dark button and darkens a
        /// light one, so it works without knowing which the user chose.
        /// Disabled moves most of the way to the window background instead,
        /// which is what "not part of the conversation" looks like.</summary>
        public const string ButtonHoverBrushKey = "ThemeButtonHoverBrush";

        public const string ButtonPressedBrushKey = "ThemeButtonPressedBrush";

        public const string ButtonDisabledBrushKey = "ThemeButtonDisabledBrush";

        /// <summary>§208. One red for every error line in the app and one
        /// amber for every warning.
        ///
        /// Before this, error text was Red in eight windows, IndianRed in
        /// three and OrangeRed in four, and BossScraperWindow and
        /// GuideScraperWindow drew the SAME "you are about to overwrite"
        /// sentence in two different colours. These are deliberately NOT
        /// user-settable: a colour whose whole job is to say "this went
        /// wrong" should not be something you can accidentally set to the
        /// same value as your body text. They are resources rather than
        /// literals so there is one place to change them.
        ///
        /// Both are picked to stay readable on a light or a dark surface,
        /// since the user chooses the background.</summary>
        public const string ErrorBrushKey = "ThemeErrorBrush";

        public const string WarningBrushKey = "ThemeWarningBrush";

        /// <summary>§208. The fill behind the main window's encounter-table
        /// column headings.
        ///
        /// Those six chips were hard-coded Black on Black with White text, so
        /// the app's most-looked-at surface ignored Border Color, Font Color
        /// and Table Background entirely and sat above a list that follows all
        /// three. They cannot simply be given the table's own brush, though:
        /// Stats Background and Table Background both default to Transparent -
        /// a true no-op for the surfaces they were introduced for - and a
        /// transparent header is not what a user who never opened Appearance
        /// had yesterday. It would have been a visible change from a setting
        /// they never chose.
        ///
        /// §209 puts a Header Background swatch in the Appearance window, so
        /// there are now three rungs and the first one that has been set wins:
        ///
        ///   1. Header Background, if the user picked one. Says exactly this.
        ///   2. Table Background, if they picked one. The header then reads as
        ///      part of the table it heads, which is the sensible default for
        ///      somebody who coloured the table and stopped there.
        ///   3. Otherwise a band derived from the window background, 12% of the
        ///      way toward the text colour.
        ///
        /// The last rung is why this never resolves to nothing: it is always a
        /// real fill, always a step away from whatever is behind it, and always
        /// keeps the heading readable, because the text is the colour the fill
        /// is only a tenth of the way toward.</summary>
        public const string TableHeaderBrushKey = "ThemeTableHeaderBrush";

        /// <summary>§208. How far the derived header band travels from the
        /// window background toward the text colour. Small: it is a heading
        /// band, not a panel.</summary>
        private const double TableHeaderMix = 0.12;

        /// <summary>§208. Fixed, not derived from the user's palette - see
        /// ErrorBrushKey.</summary>
        private static readonly Color ErrorColor = Color.FromRgb(0xE0, 0x52, 0x52);

        private static readonly Color WarningColor = Color.FromRgb(0xE0, 0xA0, 0x30);

        /// <summary>§208. How far the hover and pressed fills travel from the
        /// button's own colour toward the text colour.</summary>
        private const double ButtonHoverMix = 0.14;

        private const double ButtonPressedMix = 0.30;

        /// <summary>§208. How far a disabled button's fill travels toward the
        /// window background. Far enough to read as absent, not so far that
        /// the button's outline becomes the only thing left of it.</summary>
        private const double ButtonDisabledMix = 0.55;

        /// <summary>§188. The two measurements of the panel that every window
        /// other than the main one now puts its content on: the width of its
        /// outline, and the gap between it and the window edge. Both are
        /// Thickness resources rather than fixed numbers in the style,
        /// because both have to be able to be nothing.
        ///
        /// The panels take their fill from the Stats Background colour, whose
        /// default is Transparent. A fixed outline and a fixed inset would
        /// therefore draw a box around, and shift inward, the content of
        /// every window in the app for every user who has never opened the
        /// Appearance window - a visible change from a setting they never
        /// chose, which is the one thing every other colour option here is
        /// careful not to be. Deriving both from the colour makes the default
        /// a complete no-op: with no fill the panel has no outline and takes
        /// no space, so it is not merely invisible but absent from the
        /// layout. The first colour picked brings all three at once.</summary>
        public const string PanelBorderThicknessKey = "ThemePanelBorderThickness";

        public const string PanelMarginKey = "ThemePanelMargin";

        /// <summary>§188. The outline width a panel gets once it has a fill,
        /// matching the main window's stats panel, which sets 1 directly.</summary>
        internal const double PanelBorderWidth = 1;

        /// <summary>§188. How much background shows around a panel once there
        /// is one, so it reads as a card on the picture rather than as a
        /// second window filling the first.</summary>
        internal const double PanelInset = 10;

        /// <summary>§188. The one place that decides whether there is a panel
        /// at all. Any alpha counts as a deliberate choice; only the default
        /// fully transparent colour means no panel.</summary>
        internal static bool PanelIsVisible(Color panelColor) =>
            panelColor.A != 0;

        /// <summary>§141. A ThemeVariant (Light or Dark) for the main window's
        /// menu bar, chosen from the background behind it - see
        /// BackgroundIsLightBehindMenu. MainWindow.axaml wraps its Menu in a
        /// ThemeVariantScope bound to this, so the menu text (and its hover
        /// highlight and dropdowns) come from Fluent's light palette over a
        /// light background and from the dark one everywhere else. The menu
        /// never used the Appearance text colour; it used the app's dark
        /// theme default, white, which disappears on a pale background.</summary>
        public const string MenuVariantKey = "ThemeMenuVariant";

        /// <summary>§252. The colour the main window's World Quest menu item
        /// wears while a quest is running and World Quest mode is off. Chosen
        /// by the same predicate as MenuVariantKey, in the same place, so the
        /// two cannot disagree: red against the light menu's black text, blue
        /// against the dark menu's white.</summary>
        public const string WorldQuestLiveBrushKey = "ThemeWorldQuestLiveBrush";

        /// <summary>§267. The colour the News menu item wears while there is
        /// an announcement from the last day that the player has not opened.
        /// Yellow, as asked - but chosen by the same predicate as the one
        /// above, in the same place, because pure yellow on a light menu is
        /// barely there: dark goldenrod against black text, gold against
        /// white.</summary>
        public const string NewsLiveBrushKey = "ThemeNewsLiveBrush";

        /// <summary>§369. The colour the Update menu item wears while a newer
        /// build is available. Green, as asked, and picked by the same
        /// predicate as the two above for the same reason: a green bright
        /// enough to read against the dark menu's white text disappears into
        /// a light menu's white background. Plain green against black text,
        /// lime green against white.
        ///
        /// The item is not merely coloured - it is not there at all until
        /// there is something to install (MainWindowViewModel.UpdateAvailable),
        /// so unlike World Quest and News this colour is never the only
        /// signal. It is the second one.</summary>
        public const string UpdateAvailableBrushKey = "ThemeUpdateAvailableBrush";

        /// <summary>§374. The two colours a team card in the Simulator draws
        /// its stat table with: the stat a nature raises by 10% wears the
        /// first, the stat it lowers wears the second, and an EV that has
        /// anything in it wears the first again - green for more, orange for
        /// less, as asked. Chosen by StatsPanelIsLight rather than the menu
        /// predicate the three above share, because the table sits on the
        /// Statistics panel and not on the strip behind the menu: plain green
        /// and dark orange against a light panel's dark text, lime green and
        /// orange against a dark panel's white.</summary>
        public const string NatureBoostBrushKey = "ThemeNatureBoostBrush";
        public const string NatureLossBrushKey = "ThemeNatureLossBrush";

        /// <summary>§376. The two colours a team card's numbers wear, as
        /// PRO's own summary card draws them: every IV in its burnt orange,
        /// every EV in its steel blue, zero included. PRO's exact values
        /// against a dark panel - they were read off its card - and a
        /// deeper pair against a light one, where the blue in particular
        /// would wash out. Same predicate as the nature pair above, same
        /// moment, same reason.</summary>
        public const string IvBrushKey = "ThemeIvBrush";
        public const string EvBrushKey = "ThemeEvBrush";

        /// <summary>§141. Relative luminance (WCAG, 0 = black, 1 = white)
        /// above which the strip behind the menu counts as light. Black and
        /// white text have equal contrast at 0.18; a little above that, so a
        /// mid-tone keeps the dark menu the rest of the app wears unless dark
        /// text is clearly the more readable of the two. The green in the
        /// report that asked for this measures 0.31 - white on it is 3.0:1,
        /// black 7.1:1 - so it switches. Raise this to keep white text on
        /// more mid-tones; lower it to switch sooner.</summary>
        public const double LightBackgroundLuminance = 0.25;

        /// <summary>§141. How much of the visible background's height the
        /// menu strip covers - the menu is about 30 px of a 600 px window -
        /// and the main window's default shape, which decides which rows of a
        /// UniformToFill image are on screen at all.</summary>
        internal const double MenuStripFraction = 0.08;
        internal const double MainWindowAspect = 875.0 / 600.0;

        /// <summary>§133. The size a name falls back to, as a NAME rather
        /// than a lookup. It has to exist in FontSizeCatalog below, and the
        /// battery checks that it does - which is the whole point of naming
        /// it here instead of writing the string at each use site.</summary>
        public const string DefaultFontSizeName = "11px";

        /// <summary>The same value as a number, for the one place that needs
        /// a size without a catalog present: BuildFontSize's last resort.</summary>
        public const double DefaultFontSize = 11;

        // Display name -> pixel size. Renamed from descriptive tiers (Default,
        // Large, ... Extra Extra Large) to plain pixel names, so the dropdown
        // says what it does. 11px is what "Default" was, which is why it is
        // the fallback - anyone who never touched the setting sees no change.
        public static readonly Dictionary<string, double> FontSizeCatalog = new()
        {
            ["8px"] = 8,
            ["9px"] = 9,
            ["10px"] = 10,
            ["11px"] = 11,
            ["12px"] = 12,
            ["13px"] = 13,
            ["14px"] = 14,
            ["15px"] = 15
        };

        // For the "Create Gradient" dialog's Direction dropdown (see
        // CustomGradientViewModel). Start/End are relative points (0..1 on
        // each axis, matching Avalonia's own LinearGradientBrush.StartPoint/
        // EndPoint shape) so BuildGradientBrush below can hand a chosen
        // preset straight to the brush with no further conversion.
        public readonly record struct GradientDirectionPreset(RelativePoint Start, RelativePoint End);

        public static readonly Dictionary<string, GradientDirectionPreset> GradientDirectionCatalog = new()
        {
            ["Left to Right"] = new GradientDirectionPreset(
                new RelativePoint(0, 0, RelativeUnit.Relative),
                new RelativePoint(1, 0, RelativeUnit.Relative)),
            ["Right to Left"] = new GradientDirectionPreset(
                new RelativePoint(1, 0, RelativeUnit.Relative),
                new RelativePoint(0, 0, RelativeUnit.Relative)),
            ["Top to Bottom"] = new GradientDirectionPreset(
                new RelativePoint(0, 0, RelativeUnit.Relative),
                new RelativePoint(0, 1, RelativeUnit.Relative)),
            ["Bottom to Top"] = new GradientDirectionPreset(
                new RelativePoint(0, 1, RelativeUnit.Relative),
                new RelativePoint(0, 0, RelativeUnit.Relative)),
            ["Diagonal Down"] = new GradientDirectionPreset(
                new RelativePoint(0, 0, RelativeUnit.Relative),
                new RelativePoint(1, 1, RelativeUnit.Relative)),
            ["Diagonal Up"] = new GradientDirectionPreset(
                new RelativePoint(0, 1, RelativeUnit.Relative),
                new RelativePoint(1, 0, RelativeUnit.Relative))
        };

        public static void Reload()
        {
            Current = AppearanceSettingsRepository.Load();
            Apply();
        }

        /// <summary>§208. A straight linear blend of two colours, t of the
        /// way from a to b, per channel. Alpha travels with them, so mixing
        /// toward a transparent colour fades rather than jumping opaque.
        ///
        /// Deliberately not gamma-correct: these are small nudges of 14% to
        /// 55% between two colours the user already chose to sit next to each
        /// other, and a perceptual blend would land somewhere they did not
        /// pick for no visible gain at this distance.
        ///
        /// Per-channel work is the same Lerp SampleGradient already uses, so
        /// there is one definition of "part way between two colours" in this
        /// file rather than two that could drift.</summary>
        private static Color Mix(Color a, Color b, double t)
        {
            double f = Math.Clamp(t, 0.0, 1.0);

            return Color.FromArgb(
                Lerp(a.A, b.A, f),
                Lerp(a.R, b.R, f),
                Lerp(a.G, b.G, f),
                Lerp(a.B, b.B, f));
        }

        /// <summary>Call once at startup (e.g. from App.axaml.cs OnFrameworkInitializationCompleted).</summary>
        public static void Apply()
        {
            var app = Application.Current;
            if (app is null)
                return;

            app.Resources[BackgroundBrushKey] =
                BuildBackgroundBrush();

            // §141: decided from the same settings the brush above was built
            // from, so the two can never disagree about what is behind the menu.
            // §393: decided once for the five resources that depend on it.
            bool lightBehindMenu = BackgroundIsLightBehindMenu();

            app.Resources[MenuVariantKey] =
                lightBehindMenu ? ThemeVariant.Light : ThemeVariant.Dark;

            // §393: the menu bar's own colours, automatic ones resolved by
            // the same decision - see EffectiveMenuTextColor.
            app.Resources[MenuTextBrushKey] = new SolidColorBrush(EffectiveMenuTextColor(Current, lightBehindMenu));
            app.Resources[MenuHighlightBrushKey] = new SolidColorBrush(EffectiveMenuHighlightColor(Current, lightBehindMenu));

            // §394: the menu's font, its own or Statistics'.
            app.Resources[MenuFontFamilyKey] = BuildFontFamily(EffectiveMenuFontFamilyName(Current));
            app.Resources[MenuFontSizeKey] = BuildFontSize(EffectiveMenuFontSizeName(Current));

            // §252: same predicate, same moment - see WorldQuestLiveBrushKey.
            app.Resources[WorldQuestLiveBrushKey] =
                new SolidColorBrush(lightBehindMenu ? Colors.Red : Colors.DodgerBlue);

            // §267: and again for News - see NewsLiveBrushKey for why the
            // light menu gets goldenrod rather than the yellow it was asked
            // for.
            // §369: and once more for Update - see UpdateAvailableBrushKey.
            app.Resources[UpdateAvailableBrushKey] =
                new SolidColorBrush(lightBehindMenu ? Colors.Green : Colors.LimeGreen);

            app.Resources[NewsLiveBrushKey] =
                new SolidColorBrush(lightBehindMenu ? Colors.DarkGoldenrod : Colors.Gold);

            // §374: the Simulator card's nature colours - by the panel they
            // sit on, not the menu strip. See NatureBoostBrushKey.
            bool statsPanelLight = StatsPanelIsLight();

            app.Resources[NatureBoostBrushKey] =
                new SolidColorBrush(statsPanelLight ? Colors.Green : Colors.LimeGreen);

            app.Resources[NatureLossBrushKey] =
                new SolidColorBrush(statsPanelLight ? Colors.DarkOrange : Colors.Orange);

            // §376: the IV and EV colours, by the same panel. See IvBrushKey.
            app.Resources[IvBrushKey] =
                new SolidColorBrush(statsPanelLight ? Color.Parse("#B45800") : Color.Parse("#C86200"));

            app.Resources[EvBrushKey] =
                new SolidColorBrush(statsPanelLight ? Color.Parse("#1E78B4") : Color.Parse("#5FADD4"));

            // §349. These four are Statistics now, not a separate global.
            // Everything that binds ThemeTextBrush - the stats panel, and
            // every other window in the app - follows the Statistics section,
            // which is what §188 already did for the panel background.
            //
            // Current.TextColor and Current.BorderColor still exist and are
            // no longer read here: they are the legacy fields the §349
            // migration seeds these from, which is why an existing appearance
            // looks unchanged after the update.
            app.Resources[TextBrushKey] =
                new SolidColorBrush(Current.StatsTextColor);

            app.Resources[BorderBrushKey] =
                new SolidColorBrush(Current.StatsBorderColor);

            app.Resources[FontFamilyKey] =
                BuildFontFamily(Current.StatsFontFamilyName);

            app.Resources[FontSizeKey] =
                BuildFontSize(Current.StatsFontSizeName);

            // §349. The three regions that do not follow Statistics.
            app.Resources[SpriteBoxTextBrushKey] =
                new SolidColorBrush(Current.SpriteBoxTextColor);

            app.Resources[SpriteBoxBorderBrushKey] =
                new SolidColorBrush(Current.SpriteBoxBorderColor);

            app.Resources[SpriteBoxFontFamilyKey] =
                BuildFontFamily(Current.SpriteBoxFontFamilyName);

            app.Resources[SpriteBoxFontSizeKey] =
                BuildFontSize(Current.SpriteBoxFontSizeName);

            app.Resources[EncountersTextBrushKey] =
                new SolidColorBrush(Current.EncountersTextColor);

            app.Resources[EncountersBorderBrushKey] =
                new SolidColorBrush(Current.EncountersBorderColor);

            app.Resources[EncountersFontFamilyKey] =
                BuildFontFamily(Current.EncountersFontFamilyName);

            app.Resources[EncountersFontSizeKey] =
                BuildFontSize(Current.EncountersFontSizeName);

            app.Resources[ButtonTextBrushKey] =
                new SolidColorBrush(Current.ButtonTextColor);

            app.Resources[ButtonBorderBrushKey] =
                new SolidColorBrush(Current.ButtonBorderColor);

            app.Resources[ButtonFontFamilyKey] =
                BuildFontFamily(Current.ButtonFontFamilyName);

            app.Resources[ButtonFontSizeKey] =
                BuildFontSize(Current.ButtonFontSizeName);

            // §380. See HeadingFontWeightKey.
            app.Resources[HeadingFontWeightKey] =
                Current.BoldHeadings ? FontWeight.Bold : FontWeight.Normal;

            // §384. Widths and corners, clamped here as well as on the way
            // in, because a settings file can be edited by hand.
            double spriteWidth = ClampBorderWidth(Current.SpriteBoxBorderWidth);
            double tableWidth = ClampBorderWidth(Current.EncountersBorderWidth);
            double statsWidth = ClampBorderWidth(Current.StatsBorderWidth);
            double buttonWidth = ClampBorderWidth(Current.ButtonBorderWidth);

            app.Resources[SpriteBoxBorderThicknessKey] = new Thickness(spriteWidth);
            app.Resources[SpriteBoxCornerRadiusKey] = new CornerRadius(ClampCornerRadius(Current.SpriteBoxCornerRadius));
            app.Resources[EncountersBorderThicknessKey] = new Thickness(tableWidth);
            app.Resources[EncountersHeaderLineThicknessKey] = new Thickness(0, 0, 0, tableWidth);
            app.Resources[EncountersCornerRadiusKey] = new CornerRadius(ClampCornerRadius(Current.EncountersCornerRadius));
            app.Resources[StatsBorderThicknessKey] = new Thickness(statsWidth);
            app.Resources[StatsCornerRadiusKey] = new CornerRadius(ClampCornerRadius(Current.StatsCornerRadius));
            app.Resources[ButtonBorderThicknessKey] = new Thickness(buttonWidth);
            app.Resources[ButtonCornerRadiusKey] = new CornerRadius(ClampCornerRadius(Current.ButtonCornerRadius));

            // §384. The frames. A picture that fails to load is no frame,
            // logged, rather than a window that will not open.
            IImage? statsFrame = LoadFrameImage(Current.StatsFrameImagePath);
            IImage? tableFrame = LoadFrameImage(Current.EncountersFrameImagePath);
            IImage? spriteFrame = LoadFrameImage(Current.SpriteBoxFrameImagePath);
            double statsInset = ClampFrameInset(Current.StatsFrameInset);
            double tableInset = ClampFrameInset(Current.EncountersFrameInset);
            double spriteInset = ClampFrameInset(Current.SpriteBoxFrameInset);

            // §393: how far each reaches past its panel; the layout inside
            // only has to clear the part of the band that is inside.
            double statsOverhang = ClampFrameOverhang(Current.StatsFrameOverhang);
            double tableOverhang = ClampFrameOverhang(Current.EncountersFrameOverhang);
            double spriteOverhang = ClampFrameOverhang(Current.SpriteBoxFrameOverhang);

            app.Resources[StatsFrameImageKey] = statsFrame;
            app.Resources[StatsFrameInsetKey] = statsInset;
            app.Resources[StatsFrameOverhangKey] = FrameOverhangMargin(statsOverhang);
            app.Resources[StatsPanelPaddingKey] = new Thickness(StatsPanelPaddingFor(statsFrame is not null, statsInset, statsOverhang));
            app.Resources[TableFrameImageKey] = tableFrame;
            app.Resources[TableFrameInsetKey] = tableInset;
            app.Resources[TableFrameOverhangKey] = FrameOverhangMargin(tableOverhang);
            app.Resources[TableFrameMarginKey] = new Thickness(TableFrameMarginFor(tableFrame is not null, tableInset, tableOverhang));
            app.Resources[SpriteBoxFrameImageKey] = spriteFrame;
            app.Resources[SpriteBoxFrameInsetKey] = spriteInset;
            app.Resources[SpriteBoxFrameOverhangKey] = FrameOverhangMargin(spriteOverhang);
            app.Resources[SpriteBoxFrameImageMarginKey] = new Thickness(SpriteImageMarginFor(spriteFrame is not null, spriteInset, spriteOverhang));

            // §387. The sprite row panel, derived the same way as the frames.
            IImage? rowFrame = LoadFrameImage(Current.SpriteRowFrameImagePath);
            double rowInset = ClampFrameInset(Current.SpriteRowFrameInset);
            double rowOverhang = ClampFrameOverhang(Current.SpriteRowFrameOverhang);
            double rowWidth = ClampBorderWidth(Current.SpriteRowBorderWidth);
            bool rowVisible = SpriteRowIsVisible(Current.SpriteRowBackgroundColor, Current.SpriteRowBorderColor, rowWidth, HasPanelPicture(Current.SpriteRowBackgroundImagePath));

            app.Resources[SpriteRowBackgroundBrushKey] =
                BuildPanelBrush(Current.SpriteRowBackgroundColor, Current.SpriteRowBackgroundImagePath);
            app.Resources[SpriteRowBorderBrushKey] = new SolidColorBrush(Current.SpriteRowBorderColor);
            app.Resources[SpriteRowBorderThicknessKey] = new Thickness(rowVisible ? rowWidth : 0);
            app.Resources[SpriteRowCornerRadiusKey] = new CornerRadius(ClampCornerRadius(Current.SpriteRowCornerRadius));
            app.Resources[SpriteRowPaddingKey] = new Thickness(SpriteRowPaddingFor(rowVisible, rowFrame is not null, rowInset, rowOverhang));
            app.Resources[SpriteRowFrameImageKey] = rowFrame;
            app.Resources[SpriteRowFrameInsetKey] = rowInset;
            app.Resources[SpriteRowFrameOverhangKey] = FrameOverhangMargin(rowOverhang);

            // §365: the toolbar's sizes, in the same pass as the font size
            // they are derived from, so the row can never be drawn for one
            // font while wearing another. Rounded to whole pixels - the
            // numbers in ToolbarButtonSizes are whole and at the base size
            // they come back out unchanged.
            double toolbarScale =
                BuildFontSize(Current.ButtonFontSizeName) / ToolbarBaseFontSize;

            foreach ((string key, double atBaseFont) in ToolbarButtonSizes)
            {
                app.Resources[key] =
                    Math.Round(atBaseFont * toolbarScale);
            }

            // §383: the sprite boxes take a picture under their fill too.
            app.Resources[SpriteBoxBackgroundBrushKey] =
                BuildPanelBrush(Current.SpriteBoxBackgroundColor, Current.SpriteBoxBackgroundImagePath);

            app.Resources[ButtonBrushKey] =
                new SolidColorBrush(Current.ButtonColor);

            // §382: a picture under the fill when the settings name one, the
            // flat fill otherwise. Every window that fills a panel with this
            // brush gets the picture, which is what "Statistics paints every
            // other window" has meant since §188.
            app.Resources[StatsBackgroundBrushKey] =
                BuildPanelBrush(Current.StatsBackgroundColor, Current.StatsBackgroundImagePath);

            app.Resources[EncountersBackgroundBrushKey] =
                BuildPanelBrush(Current.EncountersBackgroundColor, Current.EncountersBackgroundImagePath);

            // §208: the button's other three states, in the same pass as the
            // colour they come from, so a saved Appearance change can never
            // leave a button resting in one palette and hovering in another.
            app.Resources[ButtonHoverBrushKey] =
                new SolidColorBrush(Mix(Current.ButtonColor, Current.ButtonTextColor, ButtonHoverMix));

            app.Resources[ButtonPressedBrushKey] =
                new SolidColorBrush(Mix(Current.ButtonColor, Current.ButtonTextColor, ButtonPressedMix));

            app.Resources[ButtonDisabledBrushKey] =
                new SolidColorBrush(Mix(Current.ButtonColor, Current.BackgroundColor, ButtonDisabledMix));

            // §208: fixed, so that "this went wrong" always looks the same
            // and always looks different from ordinary text.
            app.Resources[ErrorBrushKey] = new SolidColorBrush(ErrorColor);

            app.Resources[WarningBrushKey] = new SolidColorBrush(WarningColor);

            // §208/§209: the encounter table's heading band, first rung that
            // has been set. Its own colour if the user picked one; else the
            // table's, so the two read as one surface; else a step away from
            // the window background, so it is never the transparent nothing
            // both of those default to. See TableHeaderBrushKey.
            app.Resources[TableHeaderBrushKey] = new SolidColorBrush(
                PanelIsVisible(Current.HeaderBackgroundColor)
                    ? Current.HeaderBackgroundColor
                    : PanelIsVisible(Current.EncountersBackgroundColor)
                        ? Current.EncountersBackgroundColor
                        : Mix(Current.BackgroundColor, Current.EncountersTextColor, TableHeaderMix));

            // §188: both derived from the same colour the panels are filled
            // with, in the same pass, so nothing can disagree about whether
            // there is a panel there at all.
            // §382: a picture is a panel too, whatever the fill's alpha.
            bool panel = PanelIsVisible(Current.StatsBackgroundColor) ||
                         HasPanelPicture(Current.StatsBackgroundImagePath);

            // §384: the width is the Statistics border width now, not a
            // fixed 1 - the same rule (no fill, no picture: no line) as before.
            app.Resources[PanelBorderThicknessKey] =
                new Thickness(panel ? ClampBorderWidth(Current.StatsBorderWidth) : 0);

            app.Resources[PanelMarginKey] =
                new Thickness(panel ? PanelInset : 0);
        }

        /// <summary>
        /// NOTE: this only affects text that doesn't already set its own explicit
        /// FontSize - a local value on a control always wins over an inherited
        /// Style default, same rule that made the custom-font Bold fallback
        /// necessary. Headers/labels with a hardcoded size elsewhere in the app
        /// won't change; general body text, buttons, and unstyled labels will.
        /// </summary>
        public static double BuildFontSize(string fontSizeName)
        {
            if (FontSizeCatalog.TryGetValue(
                    NormalizeFontSizeName(fontSizeName), out double size))
            {
                return size;
            }

            // §133. Unreachable while DefaultFontSizeName is a catalog key,
            // which the battery checks. It is here anyway because the line it
            // replaces was `return FontSizeCatalog["Default"];` - also
            // believed unreachable, also correct until the catalog was
            // renamed, and then a KeyNotFoundException on startup for every
            // settings file in existence. A constant cannot do that.
            return DefaultFontSize;
        }

        /// <summary>
        /// §133. Turns whatever a settings file happens to hold into a name
        /// FontSizeCatalog actually contains.
        ///
        /// The catalog was renamed from descriptive tiers to pixel names, and
        /// the rename left three lookups behind that the compiler could not
        /// see: BuildFontSize's fallback indexed the dictionary with the
        /// literal "Default", AppearanceViewModel initialised two fields from
        /// the same string, and AppearanceSettings defaulted FontSizeName to
        /// it. Every one of those was correct the day it was written. None of
        /// them is checked by anything, because a dictionary key is a string
        /// and a string always compiles.
        ///
        /// So there is one place that decides, and everything else asks it.
        ///
        /// Three cases, in order:
        ///   - a current name ("12px") is returned unchanged;
        ///   - anything that parses as a pixel count is snapped INTO the
        ///     catalog's range, so a file holding a size that was once
        ///     offered and no longer is lands on the nearest one that exists
        ///     rather than silently reverting;
        ///   - a legacy tier name falls back to the default, except the two
        ///     whose pixel size is actually recorded in MIGRATION_GUIDE.md
        ///     (§53): "Huge" and "Extra Extra Large" were 23px, above
        ///     anything the new range offers, so they take the largest one.
        ///     The other old tiers are deliberately NOT guessed at - their
        ///     sizes are not written down anywhere, and inventing a mapping
        ///     to look thorough is how a wrong number gets recorded as a
        ///     right one.
        /// </summary>
        public static string NormalizeFontSizeName(string? fontSizeName)
        {
            if (string.IsNullOrWhiteSpace(fontSizeName))
                return DefaultFontSizeName;

            string trimmed = fontSizeName.Trim();

            if (FontSizeCatalog.ContainsKey(trimmed))
                return trimmed;

            if (trimmed.Equals("Huge", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("Extra Extra Large", StringComparison.OrdinalIgnoreCase))
            {
                return LargestFontSizeName();
            }

            string digits = trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase)
                ? trimmed[..^2].Trim()
                : trimmed;

            if (!double.TryParse(
                    digits,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double requested))
            {
                return DefaultFontSizeName;
            }

            // Nearest offered size, so 6px lands on the smallest and 20px on
            // the largest instead of both reverting to the middle.
            string nearest = DefaultFontSizeName;
            double bestDistance = double.MaxValue;

            foreach (KeyValuePair<string, double> entry in FontSizeCatalog)
            {
                double distance = Math.Abs(entry.Value - requested);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearest = entry.Key;
                }
            }

            return nearest;
        }

        /// <summary>The biggest size the catalog offers, found rather than
        /// written down, so adding a tier does not leave this behind.</summary>
        private static string LargestFontSizeName()
        {
            string largest = DefaultFontSizeName;
            double best = double.MinValue;

            foreach (KeyValuePair<string, double> entry in FontSizeCatalog)
            {
                if (entry.Value > best)
                {
                    best = entry.Value;
                    largest = entry.Key;
                }
            }

            return largest;
        }

        /// <summary>
        /// "Default" (or blank/whitespace) maps to Inter, the font bundled via the
        /// Avalonia.Fonts.Inter package - guaranteed present on every platform,
        /// unlike a named system font which may or may not exist on a given machine.
        /// A name matching CustomFontCatalog loads from our own bundled Assets/Fonts/
        /// files via the avares:// resource URI. Anything else falls back to a plain
        /// system-font-name lookup - Avalonia falls back gracefully (not an exception)
        /// if that name isn't found on the current machine.
        /// </summary>
        public static FontFamily BuildFontFamily(string fontFamilyName)
        {
            if (string.IsNullOrWhiteSpace(fontFamilyName) ||
                fontFamilyName.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                return new FontFamily("Inter");
            }

            if (CustomFontCatalog.TryGetValue(fontFamilyName, out string? internalFontName))
            {
                // Fallback chain, not just the bare font name: many custom fonts
                // (like the ones this was hit with) only ship a single weight, with
                // no embedded Bold variant. Avalonia throws InvalidOperationException
                // ("Could not create glyph typeface... Weight: Bold") rather than
                // synthesizing a fake bold or substituting automatically - confirmed
                // via multiple Avalonia GitHub issues covering this exact error.
                // Appending Inter as a fallback lets Bold-styled text (headers/labels
                // throughout the app) render in Inter instead of crashing, while
                // Regular-weight text still uses the custom font as intended.
                return new FontFamily($"avares://{CustomFontAssemblyName}/Assets/Fonts#{internalFontName}, Inter");
            }

            // §385. A font the user added as a file: the same shape of
            // family, from the user collection instead of the assembly.
            if (UserFontService.IsUserFont(fontFamilyName))
                return UserFontService.BuildFontFamily(fontFamilyName);

            return new FontFamily(fontFamilyName);
        }

        // ============================================================
        // §384. FRAMES.
        // ============================================================

        /// <summary>§384. The frame picture as an IImage for NineSliceFrame,
        /// or null when there is none or it cannot be read. Frames are small
        /// (a few hundred pixels square), so they are loaded whole.</summary>
        public static IImage? LoadFrameImage(string? imagePath)
        {
            if (!HasPanelPicture(imagePath))
                return null;

            try
            {
                return new Bitmap(imagePath!);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ThemeManager could not read the frame picture {Path}; drawing no frame", imagePath);
                return null;
            }
        }

        /// <summary>§393. How much of a frame's band lies inside its panel:
        /// the corner size less the overhang, never below nothing. What the
        /// three layout rules below keep the content clear of.</summary>
        public static double FrameInnerBand(double inset, double overhang) =>
            Math.Max(0, inset - overhang);

        /// <summary>§384. The stats panel's padding: its usual 10, or the
        /// frame's corner size plus a little when a frame would otherwise
        /// cover the first line of text. §393: less the overhang, which is
        /// outside the panel.</summary>
        public static double StatsPanelPaddingFor(bool framed, double inset, double overhang = 0) =>
            framed ? Math.Max(StatsPanelPadding, FrameInnerBand(inset, overhang) + 2) : StatsPanelPadding;

        /// <summary>§384. The margin that keeps the table's header and rows
        /// inside the frame drawn around them; nothing without one. §393:
        /// the inside part of the band only.</summary>
        public static double TableFrameMarginFor(bool framed, double inset, double overhang = 0) =>
            framed ? FrameInnerBand(inset, overhang) : 0;

        /// <summary>§387. Whether the sprite row panel shows at all: a fill
        /// with any alpha, a border that has both a colour and a width, or a
        /// picture. A frame alone is drawn regardless; it has its own size.</summary>
        public static bool SpriteRowIsVisible(Color fill, Color border, double width, bool hasPicture) =>
            fill.A != 0 || (border.A != 0 && width > 0) || hasPicture;

        /// <summary>§387. The row's inset from its panel: nothing without a
        /// panel, 8 with one, the frame's corner size plus two under a frame
        /// (§393: the inside part of it).</summary>
        public static double SpriteRowPaddingFor(bool visible, bool framed, double inset, double overhang = 0) =>
            framed ? Math.Max(SpriteRowPadding, FrameInnerBand(inset, overhang) + 2) : visible ? SpriteRowPadding : 0;

        /// <summary>§384. How far a sprite pulls in from its box's edge so a
        /// frame does not cover it: half the corner size, capped so a big
        /// ornate frame still leaves most of a 96px box to the sprite. §393:
        /// half of the inside part of the band.</summary>
        public static double SpriteImageMarginFor(bool framed, double inset, double overhang = 0) =>
            framed ? Math.Min(FrameInnerBand(inset, overhang) * 0.5, 28) : 0;

        /// <summary>§393. The menu bar's text colour: the one set, or - when
        /// it is transparent, "automatic" - black over a light strip and
        /// white over a dark one, which is what Fluent's palette gave the
        /// menu before the colour could be chosen at all (§141).</summary>
        public static Color EffectiveMenuTextColor(AppearanceSettings settings, bool lightBehindMenu) =>
            settings.MenuTextColor.A != 0 ? settings.MenuTextColor : lightBehindMenu ? Colors.Black : Colors.White;

        /// <summary>§393. The highlight behind the menu item the pointer is
        /// on or that is open: the one set, or a faint black over a light
        /// strip and a faint white over a dark one, near Fluent's own.</summary>
        public static Color EffectiveMenuHighlightColor(AppearanceSettings settings, bool lightBehindMenu) =>
            settings.MenuHighlightColor.A != 0
                ? settings.MenuHighlightColor
                : lightBehindMenu ? Color.FromArgb(0x1A, 0, 0, 0) : Color.FromArgb(0x26, 255, 255, 255);

        /// <summary>§394. The menu bar's font family name: its own when one
        /// is set, else Statistics' - the window font, which is what the
        /// menu inherited before it could have its own.</summary>
        public static string EffectiveMenuFontFamilyName(AppearanceSettings settings) =>
            string.IsNullOrWhiteSpace(settings.MenuFontFamilyName) ? settings.StatsFontFamilyName : settings.MenuFontFamilyName;

        /// <summary>§394. The same for the size.</summary>
        public static string EffectiveMenuFontSizeName(AppearanceSettings settings) =>
            string.IsNullOrWhiteSpace(settings.MenuFontSizeName) ? settings.StatsFontSizeName : settings.MenuFontSizeName;

        // ============================================================
        // §382. PANEL PICTURES.
        // ============================================================

        /// <summary>§382. Whether a panel picture path names a file that is
        /// there. An empty path, or one whose file has gone, is no picture.</summary>
        public static bool HasPanelPicture(string? imagePath) =>
            !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath);

        /// <summary>§382. Pictures are drawn no larger than this on their
        /// long edge before the fill goes over them: a panel is a few hundred
        /// pixels wide, and a 4K texture composed whole would cost tens of
        /// megabytes per Apply for nothing anyone could see.</summary>
        internal const int PanelPictureMaxEdge = 1600;

        /// <summary>
        /// §382. The brush a panel is filled with: the flat colour when there
        /// is no picture, and otherwise the picture with the fill colour
        /// painted OVER it, composed once into one bitmap.
        ///
        /// Composed rather than layered because every panel in the app is one
        /// Border (or one ListBox) with one Background, and the main window's
        /// stats panel, its encounter table and every other window's
        /// themedPanel all take this brush - one composed image reaches them
        /// all without a nested element in any of them. The fill's alpha is
        /// the tint: a navy at 70% over a texture dims it enough to read text
        /// on; an opaque fill hides the picture entirely, which the editor
        /// says. The picture is stretched to cover the panel and centred, so
        /// it crops rather than squashes.
        ///
        /// Anything that goes wrong - a file that is not an image, a render
        /// surface that cannot be made - falls back to the flat fill, logged,
        /// so a bad picture can never take the window with it.
        /// </summary>
        public static IBrush BuildPanelBrush(Color fill, string? imagePath)
        {
            if (!HasPanelPicture(imagePath))
                return new SolidColorBrush(fill);

            try
            {
                using var picture = new Bitmap(imagePath!);

                int sourceWidth = Math.Max(1, picture.PixelSize.Width);
                int sourceHeight = Math.Max(1, picture.PixelSize.Height);
                double scale = Math.Min(1.0, (double)PanelPictureMaxEdge / Math.Max(sourceWidth, sourceHeight));
                int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
                int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));

                var composed = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));

                using (DrawingContext context = composed.CreateDrawingContext())
                {
                    context.DrawImage(
                        picture,
                        new Rect(0, 0, sourceWidth, sourceHeight),
                        new Rect(0, 0, width, height));

                    if (fill.A != 0)
                        context.FillRectangle(new SolidColorBrush(fill), new Rect(0, 0, width, height));
                }

                return new ImageBrush(composed)
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                };
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ThemeManager could not compose the panel picture {Path}; using the flat fill", imagePath);
                return new SolidColorBrush(fill);
            }
        }

        /// <summary>§104: resolves the one built-in background (Slate) to its
        /// image under SharedPokemonLibrary/Assets/Custom. The preset PICKER
        /// is gone and AppearanceSettingsRepository coerces any older saved
        /// preset name to Slate, so in practice this is only ever called with
        /// "Slate" - it stays parameterised rather than hardcoded so a future
        /// built-in look needs no new plumbing, and an unknown id simply
        /// misses on disk and falls back to the flat color.</summary>
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
        /// Backs the "Create Gradient" dialog (see CustomGradientViewModel) - a
        /// fourth background mode alongside the preset/custom-image/custom-color
        /// trio in BuildBackgroundBrush below, gated by its own
        /// UseCustomGradient flag since a gradient needs multiple colors and a
        /// direction rather than the single color custom-color mode stores.
        /// Accepts 2-4 colors (the range CustomGradientViewModel's +/- buttons
        /// enforce) and spaces them evenly across the gradient - two colors
        /// sit at offsets 0/1, three at 0/0.5/1, and so on. Looks up the
        /// direction's start/end points from GradientDirectionCatalog;
        /// "None"/an unrecognized direction name falls back to the catalog's
        /// first entry, and fewer than 2 colors (only possible from a
        /// corrupted/hand-edited settings file - nothing upstream of this
        /// ever validates a saved value before startup) falls back to the
        /// model's own default color pair, rather than either case throwing -
        /// the same defensive shape BuildFontSize/BuildFontFamily above use
        /// for their own unrecognized-input case.
        /// </summary>
        public static LinearGradientBrush BuildGradientBrush(IReadOnlyList<Color> colors, string direction)
        {
            if (string.IsNullOrWhiteSpace(direction) ||
                !GradientDirectionCatalog.TryGetValue(direction, out GradientDirectionPreset preset))
            {
                preset = GradientDirectionCatalog.Values.First();
            }

            IReadOnlyList<Color> stopColors = colors is { Count: >= 2 }
                ? colors
                : new[] { Color.FromRgb(10, 15, 61), Color.FromRgb(89, 87, 87) };

            var brush = new LinearGradientBrush
            {
                StartPoint = preset.Start,
                EndPoint = preset.End
            };

            int lastIndex = stopColors.Count - 1;
            for (int i = 0; i < stopColors.Count; i++)
            {
                brush.GradientStops.Add(new GradientStop(stopColors[i], (double)i / lastIndex));
            }

            return brush;
        }

        /// <summary>
        /// Builds the actual background brush: a live two-color gradient when
        /// UseCustomGradient is set (see BuildGradientBrush above), otherwise
        /// the preset/custom gradient image when one exists on disk, otherwise
        /// a flat SolidColorBrush fallback (either the preset's base color or
        /// the user's custom picked color). Previously this always returned a
        /// SolidColorBrush and loaded the image into an unused resource -
        /// presets never actually showed their gradient art.
        /// </summary>
        private static IBrush BuildBackgroundBrush()
        {
            // §141: whatever GIF was playing belonged to the previous
            // appearance; a new brush is about to replace its target.
            AnimatedBackgroundService.Stop();

            if (Current.UseCustomGradient)
            {
                return BuildGradientBrush(Current.CustomGradientColors, Current.CustomGradientDirection);
            }

            string backgroundPath = GetCurrentBackgroundPath();

            if (!string.IsNullOrWhiteSpace(backgroundPath) && File.Exists(backgroundPath))
            {
                var brush = new ImageBrush(new Bitmap(backgroundPath))
                {
                    Stretch = Stretch.UniformToFill
                };

                // §141: a GIF shows its first frame at once through the line
                // above (Avalonia decodes a GIF's first frame like any still
                // image); the remaining frames start playing into this same
                // brush once AnimatedBackgroundService has decoded them.
                if (AnimatedBackgroundService.IsGif(backgroundPath))
                    AnimatedBackgroundService.Start(brush, backgroundPath);

                return brush;
            }

            return new SolidColorBrush(Current.BackgroundColor);
        }

        // ==============================================================
        // §141 - IS THE BACKGROUND LIGHT BEHIND THE MENU?
        // ==============================================================

        /// <summary>§374. True when the Statistics panel - the fill behind the
        /// stats table, every themedPanel and the Simulator's team cards - is
        /// light enough that a colour picked for white text would be lost on
        /// it. The panel's own colour when there is a panel and it is mostly
        /// opaque (RelativeLuminance already counts a faint fill as the dark
        /// thing it looks like); the strip behind the menu otherwise, since
        /// with no panel it is the window background that shows through.
        /// Logged like BackgroundIsLightBehindMenu, for the same reason.</summary>
        public static bool StatsPanelIsLight()
        {
            Color panel = Current.StatsBackgroundColor;

            if (!PanelIsVisible(panel) || panel.A < 128)
                return BackgroundIsLightBehindMenu();

            double luminance = RelativeLuminance(panel);
            bool light = luminance > LightBackgroundLuminance;

            Log.Information(
                "Appearance: relative luminance of the stats panel {Luminance:0.000} - nature colours use the {Variant} pair",
                luminance, light ? "light" : "dark");

            return light;
        }

        /// <summary>True when the strip of background behind the main window's
        /// menu bar is light enough that Fluent's dark-theme white menu text
        /// would be hard to read - see LightBackgroundLuminance. Logged each
        /// time, so a support bundle shows the number behind the decision.</summary>
        public static bool BackgroundIsLightBehindMenu()
        {
            double luminance = MenuStripLuminance();
            bool light = luminance > LightBackgroundLuminance;

            Log.Information(
                "Appearance: relative luminance behind the menu bar {Luminance:0.000} - menu uses the {Variant} palette",
                luminance, light ? "light" : "dark");

            return light;
        }

        /// <summary>Mean relative luminance of the menu strip for the current
        /// settings: sampled along the top edge of a gradient (whatever its
        /// direction), over the top band of an image that is actually on
        /// screen after UniformToFill (a GIF by its first frame), or the flat
        /// colour itself.</summary>
        internal static double MenuStripLuminance() =>
            MenuStripLuminance(Current, GetCurrentBackgroundPath());

        /// <summary>§393. The same, for any settings and the picture they
        /// would show - the editor asks it about its working copy, whose
        /// picture may not be applied yet.</summary>
        internal static double MenuStripLuminance(AppearanceSettings settings, string? backgroundPath)
        {
            if (settings.UseCustomGradient)
                return GradientTopEdgeLuminance(settings.CustomGradientColors, settings.CustomGradientDirection);

            if (!string.IsNullOrWhiteSpace(backgroundPath) && File.Exists(backgroundPath))
            {
                double? fromImage = ImageTopBandLuminance(backgroundPath);

                if (fromImage is double value)
                    return value;
            }

            return RelativeLuminance(settings.BackgroundColor);
        }

        /// <summary>§393. Whether the strip behind the menu is light, for
        /// any settings - the editor's canvas resolves its automatic menu
        /// colours through this, the way Apply does through
        /// BackgroundIsLightBehindMenu.</summary>
        public static bool BackgroundIsLightBehindMenu(AppearanceSettings settings, string? backgroundPath) =>
            MenuStripLuminance(settings, backgroundPath) > LightBackgroundLuminance;

        /// <summary>WCAG relative luminance of a colour, composited over black
        /// (what a translucent window background sits on) so a faint colour
        /// reads as the dark thing it looks like rather than as its hue.</summary>
        internal static double RelativeLuminance(Color color) =>
            RelativeLuminance(color.R, color.G, color.B, color.A);

        internal static double RelativeLuminance(SKColor color) =>
            RelativeLuminance(color.Red, color.Green, color.Blue, color.Alpha);

        internal static double RelativeLuminance(byte r, byte g, byte b, byte a)
        {
            double luminance =
                0.2126 * Linear(r) +
                0.7152 * Linear(g) +
                0.0722 * Linear(b);

            return luminance * (a / 255.0);
        }

        private static double Linear(byte channel)
        {
            double c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        /// <summary>The gradient BuildGradientBrush would draw, sampled at 32
        /// points across the middle of the menu strip - the same stops, the
        /// same start/end points, the same fallbacks - and averaged. A
        /// top-to-bottom gradient answers with its first colour, a left-to-
        /// right one with the mean of all of them, which is what the eye sees
        /// behind a menu that spans the width.</summary>
        internal static double GradientTopEdgeLuminance(IReadOnlyList<Color> colors, string direction)
        {
            if (string.IsNullOrWhiteSpace(direction) ||
                !GradientDirectionCatalog.TryGetValue(direction, out GradientDirectionPreset preset))
            {
                preset = GradientDirectionCatalog.Values.First();
            }

            IReadOnlyList<Color> stops = colors is { Count: >= 2 }
                ? colors
                : new[] { Color.FromRgb(10, 15, 61), Color.FromRgb(89, 87, 87) };

            double startX = preset.Start.Point.X;
            double startY = preset.Start.Point.Y;
            double deltaX = preset.End.Point.X - startX;
            double deltaY = preset.End.Point.Y - startY;
            double lengthSquared = deltaX * deltaX + deltaY * deltaY;

            const int samples = 32;
            double y = MenuStripFraction / 2;
            double total = 0;

            for (int i = 0; i < samples; i++)
            {
                double x = (i + 0.5) / samples;

                double t = lengthSquared <= 0
                    ? 0
                    : Math.Clamp(((x - startX) * deltaX + (y - startY) * deltaY) / lengthSquared, 0, 1);

                total += RelativeLuminance(SampleGradient(stops, t));
            }

            return total / samples;
        }

        /// <summary>The colour at position <paramref name="t"/> (0..1) of a
        /// gradient whose stops are spaced evenly, as BuildGradientBrush
        /// spaces them.</summary>
        internal static Color SampleGradient(IReadOnlyList<Color> stops, double t)
        {
            int lastIndex = stops.Count - 1;
            double scaled = Math.Clamp(t, 0, 1) * lastIndex;
            int index = (int)Math.Floor(scaled);

            if (index >= lastIndex)
                return stops[lastIndex];

            double fraction = scaled - index;
            Color from = stops[index];
            Color to = stops[index + 1];

            return Color.FromArgb(
                Lerp(from.A, to.A, fraction),
                Lerp(from.R, to.R, fraction),
                Lerp(from.G, to.G, fraction),
                Lerp(from.B, to.B, fraction));
        }

        private static byte Lerp(byte from, byte to, double fraction) =>
            (byte)Math.Clamp(Math.Round(from + (to - from) * fraction), 0, 255);

        /// <summary>Mean relative luminance of the band of an image that sits
        /// behind the menu. UniformToFill scales the image to cover the window
        /// and centres it, so an image taller than the window's shape loses
        /// its top and bottom: the visible rows are worked out for the main
        /// window's default shape first, then the top MenuStripFraction of
        /// those is sampled on a grid (64 columns, up to 16 rows). Null when
        /// the file will not decode, so the caller falls back to the flat
        /// colour rather than guess.</summary>
        internal static double? ImageTopBandLuminance(string path)
        {
            try
            {
                using SKBitmap? image = ImageOps.DecodePng(File.ReadAllBytes(path));

                if (image is null || image.Width <= 0 || image.Height <= 0)
                    return null;

                (int visibleTop, int visibleHeight) = VisibleRows(image.Width, image.Height);
                int bandRows = Math.Max(1, (int)Math.Round(visibleHeight * MenuStripFraction));

                SKColor[] pixels = image.Pixels;
                int rowStep = Math.Max(1, bandRows / 16);
                int columnStep = Math.Max(1, image.Width / 64);
                double total = 0;
                int count = 0;

                for (int y = visibleTop; y < visibleTop + bandRows && y < image.Height; y += rowStep)
                {
                    for (int x = 0; x < image.Width; x += columnStep)
                    {
                        total += RelativeLuminance(pixels[y * image.Width + x]);
                        count++;
                    }
                }

                return count == 0 ? null : total / count;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Appearance: could not read the background image {Path} to judge its lightness", path);
                return null;
            }
        }

        /// <summary>Which rows of a <paramref name="width"/> x
        /// <paramref name="height"/> image are on screen under UniformToFill
        /// in the main window's default shape: all of them for an image at
        /// least as wide as the window is, a centred band for a taller one.</summary>
        internal static (int Top, int Height) VisibleRows(int width, int height)
        {
            double imageAspect = (double)width / height;

            if (imageAspect >= MainWindowAspect)
                return (0, height);

            int visibleHeight = Math.Max(1, (int)Math.Round(width / MainWindowAspect));
            return ((height - visibleHeight) / 2, visibleHeight);
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