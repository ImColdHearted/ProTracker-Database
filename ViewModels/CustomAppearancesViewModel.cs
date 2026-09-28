using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// §345. One appearance in the community gallery, with its colours already
/// turned into brushes so the card can paint itself.
///
/// The preview is built from the theme's OWN colours through the same
/// ThemeManager.BuildGradientBrush the app uses for real - so a gradient in
/// the gallery looks like the gradient you get after applying it, rather
/// than like a second implementation's idea of one.
/// </summary>
public sealed partial class CommunityThemeItem : ObservableObject
{
    public CommunityThemeService.CommunityTheme Source { get; }

    public CommunityThemeItem(CommunityThemeService.CommunityTheme source)
    {
        Source = source;

        AppearanceSettingsRepository.ThemeFile colours =
            source.Colours ?? new AppearanceSettingsRepository.ThemeFile();

        Name = string.IsNullOrWhiteSpace(source.Name) ? "(untitled)" : source.Name;

        // §366: the card labels the value rather than decorating it -
        // "Creator: Lacomus", not "by Lacomus" - so the four rows read as a
        // small table. Sharing a name is optional, and a card with a blank
        // line where the others have a name looks broken, so an anonymous
        // share says so.
        Creator = string.IsNullOrWhiteSpace(source.Author) ? "Anonymous" : source.Author;

        TextSwatch = Solid(colours.TextColorArgb);
        ButtonSwatch = Solid(colours.ButtonColorArgb);
        HeaderSwatch = Solid(colours.HeaderBackgroundColorArgb);
        StatsSwatch = Solid(colours.StatsBackgroundColorArgb);
        EncountersSwatch = Solid(colours.EncountersBackgroundColorArgb);
        BorderSwatch = Solid(colours.BorderColorArgb);

        BackgroundPreview =
            colours.UseCustomGradient && colours.CustomGradientColorArgbs.Count >= 2
                ? ThemeManager.BuildGradientBrush(
                    colours.CustomGradientColorArgbs
                        .Select(AppearanceSettings.FromArgbInt)
                        .ToList(),
                    colours.CustomGradientDirection)
                : Solid(colours.CustomBackgroundColorArgb);

        // §366: one line each, because the card gives them a line each. A
        // theme carries a font family and a size independently - either can
        // be absent, and an absent one means "leave this machine's alone"
        // (see AppearanceSettingsRepository.ImportTheme), which is what
        // "Default" says here.
        FontName =
            string.IsNullOrWhiteSpace(colours.FontFamilyName)
                ? "Default"
                : colours.FontFamilyName;

        FontSizeText =
            string.IsNullOrWhiteSpace(colours.FontSizeName)
                ? ThemeManager.DefaultFontSizeName
                : colours.FontSizeName;

        // §366: what is BEHIND the theme, in three words rather than a
        // sentence. The picture is still only described here, never fetched -
        // a gallery of thirty cards would otherwise pull thirty backgrounds
        // the moment the window opened, most of which nobody is going to
        // apply. View Image fetches one, for the one card that asked.
        BackgroundSummary =
            source.HasImage ? "Custom Image"
            : colours.UseCustomGradient ? "Gradient"
            : "Solid Colour";

        ImageSizeNote = source.HasImage
            ? $"{source.ImageWidth}x{source.ImageHeight}"
            : string.Empty;

        AppliedNote = source.Applied == 1 ? "Applied once" : $"Applied {source.Applied} times";

        // §366: the mockup's card has four rows and no room for a fifth, so
        // the things worth keeping but not worth a line of their own live in
        // the card's tooltip instead of being deleted.
        CardTooltip = source.HasImage
            ? $"{AppliedNote}. Background image {ImageSizeNote}."
            : AppliedNote + ".";
    }

    public string Name { get; }

    public string Creator { get; }

    public string FontName { get; }

    public string FontSizeText { get; }

    public string BackgroundSummary { get; }

    public string ImageSizeNote { get; }

    public string AppliedNote { get; }

    public string CardTooltip { get; }

    public bool HasImage => Source.HasImage;

    public IBrush BackgroundPreview { get; }

    public IBrush TextSwatch { get; }

    public IBrush ButtonSwatch { get; }

    public IBrush HeaderSwatch { get; }

    public IBrush StatsSwatch { get; }

    public IBrush EncountersSwatch { get; }

    public IBrush BorderSwatch { get; }

    private static IBrush Solid(int argb) =>
        new SolidColorBrush(AppearanceSettings.FromArgbInt(argb));
}

/// <summary>
/// §345. The Custom Appearances window: browse what other players have
/// shared, apply one, and share your own.
///
/// Applying goes through AppearanceSettingsRepository.ImportTheme and then
/// Save + ThemeManager.Reload - the identical path the Appearance window's
/// own Apply takes (see AppearanceViewModel.Persist). A community theme is
/// not a special kind of appearance; it is the same appearance arriving by
/// a different road.
/// </summary>
public sealed partial class CustomAppearancesViewModel : ViewModelBase
{
    /// <summary>Everything the server returned. The grid does not bind
    /// this - see PageItems.</summary>
    public ObservableCollection<CommunityThemeItem> Themes { get; } = new();

    // ============================================================
    // §366. PAGING.
    // ============================================================
    //
    // The gallery used to be one scrolling column of wide rows. It is a grid
    // of cards four across now, and a grid that grows without limit is a
    // scrollbar with no sense of how much is below it. Eight to a page is
    // two full rows at the window's default width, which is what the mockup
    // this was built from shows.
    //
    // The page is a WINDOW ONTO Themes, not a separate fetch: the server
    // hands back the whole list in one call and always has, so paging here
    // costs nothing and cannot get out of step with what was loaded. If the
    // gallery ever outgrows one response, this is the property that turns
    // into a real request - not the grid.

    /// <summary>§366. The cards actually on screen. Rebuilt whenever the
    /// page or the list changes; the grid binds this and nothing else.</summary>
    public ObservableCollection<CommunityThemeItem> PageItems { get; } = new();

    public const int PageSize = 8;

    [ObservableProperty] private int pageIndex;

    [ObservableProperty] private bool isBusy;

    [ObservableProperty] private string statusText = string.Empty;

    [ObservableProperty] private string shareName = string.Empty;

    [ObservableProperty] private string shareAuthor = string.Empty;

    /// <summary>Off by default. Sharing the picture is a bigger step than
    /// sharing eight colours - it uploads a file the user chose off their
    /// own disk - so it is opted into rather than out of.</summary>
    [ObservableProperty] private bool includeBackgroundImage;

    /// <summary>True when the appearance currently applied actually has a
    /// picture background to offer. The checkbox is hidden otherwise, since
    /// a gradient or a flat colour has no image to send.</summary>
    public bool CanIncludeBackgroundImage { get; private set; }

    public string CurrentAppearanceNote { get; private set; } = string.Empty;

    public bool IsEmpty => Themes.Count == 0 && !IsBusy;

    /// <summary>At least 1, so "Page 1 of 1" is what an empty gallery says
    /// rather than "Page 1 of 0".</summary>
    public int PageCount =>
        Math.Max(1, (Themes.Count + PageSize - 1) / PageSize);

    public string PageLabel => $"Page {PageIndex + 1} of {PageCount}";

    /// <summary>The pager is not drawn at all while everything fits on one
    /// page - a disabled ‹ 1 of 1 › is furniture, not information.</summary>
    public bool ShowPager => Themes.Count > PageSize;

    public bool CanGoBack => PageIndex > 0;

    public bool CanGoForward => PageIndex + 1 < PageCount;

    [RelayCommand]
    private void PreviousPage()
    {
        if (CanGoBack)
            PageIndex--;
    }

    [RelayCommand]
    private void NextPage()
    {
        if (CanGoForward)
            PageIndex++;
    }

    partial void OnPageIndexChanged(int value) => RebuildPage();

    /// <summary>§366. Fills PageItems from Themes for the current page, and
    /// tells the view about every figure derived from the pair. Clamps the
    /// page first: a refresh that returns fewer appearances than last time
    /// can leave PageIndex past the end, and a page past the end is an empty
    /// grid rather than an error.</summary>
    private void RebuildPage()
    {
        int lastPage = PageCount - 1;

        if (PageIndex > lastPage)
        {
            // Assigning re-enters through OnPageIndexChanged, which rebuilds
            // with the clamped value - so this one returns rather than
            // filling the page twice.
            PageIndex = lastPage;
            return;
        }

        if (PageIndex < 0)
        {
            PageIndex = 0;
            return;
        }

        PageItems.Clear();

        foreach (CommunityThemeItem item in
                 Themes.Skip(PageIndex * PageSize).Take(PageSize))
        {
            PageItems.Add(item);
        }

        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(ShowPager));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public CustomAppearancesViewModel()
    {
        RefreshCurrentAppearanceNote();
    }

    private void RefreshCurrentAppearanceNote()
    {
        AppearanceSettings current = AppearanceSettingsRepository.Load();

        CanIncludeBackgroundImage =
            current.UseCustomBackground &&
            !string.IsNullOrWhiteSpace(current.CustomBackgroundPath);

        CurrentAppearanceNote = current.UseCustomGradient
            ? "Sharing your current appearance: gradient, colours and font."
            : CanIncludeBackgroundImage
                ? "Sharing your current appearance: colours and font. Tick the box to include your background picture."
                : "Sharing your current appearance: colours and font.";

        OnPropertyChanged(nameof(CanIncludeBackgroundImage));
        OnPropertyChanged(nameof(CurrentAppearanceNote));
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = "Loading appearances…";
        OnPropertyChanged(nameof(IsEmpty));

        try
        {
            IReadOnlyList<CommunityThemeService.CommunityTheme> loaded =
                await CommunityThemeService.ListAsync();

            Themes.Clear();

            foreach (CommunityThemeService.CommunityTheme theme in loaded)
                Themes.Add(new CommunityThemeItem(theme));

            // §366: back to the first page on every load. A refresh is a new
            // list, and holding page 3 of a list that just changed underneath
            // shows the user somebody else's eight.
            PageIndex = 0;
            RebuildPage();

            StatusText = Themes.Count == 0
                ? "No appearances have been shared yet - yours could be the first."
                : $"{Themes.Count} appearance{(Themes.Count == 1 ? string.Empty : "s")} shared by other players.";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    public async Task ApplyAsync(CommunityThemeItem? item)
    {
        if (item?.Source.Colours is null)
            return;

        IsBusy = true;
        StatusText = $"Applying {item.Name}…";

        try
        {
            AppearanceSettings current = AppearanceSettingsRepository.Load();

            AppearanceSettings merged =
                AppearanceSettingsRepository.ImportTheme(item.Source.Colours, current);

            // The picture, if the theme carries one. Fetched only now, for
            // the one theme actually being applied. A failed download is not
            // a failed apply: the colours are the theme, the picture is an
            // extra, and losing it should not leave the user with nothing.
            if (item.HasImage)
            {
                string? localPath = await CommunityThemeService.DownloadBackgroundAsync(item.Source);

                if (localPath is not null)
                {
                    merged.UseCustomBackground = true;
                    merged.UseCustomGradient = false;
                    merged.CustomBackgroundPath = localPath;
                }
            }

            AppearanceSettingsRepository.Save(merged);
            ThemeManager.Reload();

            RefreshCurrentAppearanceNote();

            StatusText = item.HasImage
                ? $"Applied {item.Name}."
                : $"Applied {item.Name} - colours and font only.";
        }
        catch (Exception ex)
        {
            StatusText = "That appearance could not be applied - " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ShareAsync()
    {
        if (string.IsNullOrWhiteSpace(ShareName))
        {
            StatusText = "Give your appearance a name first.";
            return;
        }

        IsBusy = true;
        StatusText = "Sending…";

        try
        {
            AppearanceSettings current = AppearanceSettingsRepository.Load();

            string? imagePath =
                IncludeBackgroundImage && CanIncludeBackgroundImage
                    ? current.CustomBackgroundPath
                    : null;

            string? error = await CommunityThemeService.ShareAsync(
                current,
                ShareName,
                ShareAuthor,
                imagePath);

            if (error is null)
            {
                ShareName = string.Empty;

                // Deliberately not "it is live". Everything shared waits for
                // a human to look at it, and saying otherwise would have
                // people refreshing a gallery their theme is not in yet.
                StatusText = "Sent. It will appear here once it has been reviewed.";
            }
            else
            {
                StatusText = error;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
